using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AudioPlayer.Services;

public sealed record TranslationOptions(string Python, string Model, string SourceLanguage, string TargetLanguage, bool UseCuda);
public sealed record TranslationProgress(double Percent, string Message);

public sealed class LyricTranslationService
{
    private readonly string _script;
    public LyricTranslationService(string? script = null) => _script = script ?? Path.Combine(AppContext.BaseDirectory, "recognition", "translate_lyrics.py");

    public async Task<string> RunAsync(TranslationOptions options, LrcDocument original, string audio,
        IProgress<TranslationProgress>? progress, CancellationToken token, bool overwrite = false)
    {
        if ((options.SourceLanguage != "auto" && !LyricTranslation.IsLanguage(options.SourceLanguage)) || !LyricTranslation.IsLanguage(options.TargetLanguage))
            throw new ArgumentException("请选择原文语言和目标语言。");
        if (options.SourceLanguage == options.TargetLanguage) throw new ArgumentException("原文语言和目标语言不能相同。");
        if (!original.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Text))) throw new InvalidOperationException("没有可翻译的歌词，请先载入或识别生成 LRC。");
        if (!LyricTranslation.IsModelComplete(options.Model)) throw new InvalidOperationException("翻译模型不完整，请先下载 M2M100 模型或选择完整的本地模型目录。");
        string output = LyricTranslation.OutputPath(audio, options.TargetLanguage);
        if (File.Exists(output) && !overwrite) throw new IOException("译文已经存在；勾选覆盖后可重新翻译。");
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var start = new ProcessStartInfo(PythonEnvironment.RequireExecutable(options.Python))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-I", "-X", "utf8", "-u", _script, "--model", Path.GetFullPath(options.Model),
            "--source", options.SourceLanguage, "--target", options.TargetLanguage, "--device", options.UseCuda ? "cuda" : "cpu" }) start.ArgumentList.Add(argument);
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        using var process = new Process { StartInfo = start };
        var errors = new StringBuilder();
        var translated = new string?[original.Lines.Count];
        void Kill()
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        async Task ReadOutput()
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (token.IsCancellationRequested) continue;
                    using var json = JsonDocument.Parse(line);
                    var data = json.RootElement;
                    switch (data.GetProperty("type").GetString())
                    {
                        case "line":
                            int index = data.GetProperty("index").GetInt32();
                            string? value = data.GetProperty("text").GetString();
                            if (index < 0 || index >= translated.Length || translated[index] is not null || value is null)
                                throw new InvalidDataException("翻译引擎返回了无效的歌词行。");
                            if (!string.IsNullOrWhiteSpace(original.Lines[index].Text) && string.IsNullOrWhiteSpace(value))
                                throw new InvalidDataException($"第 {index + 1} 行没有生成有效译文。");
                            translated[index] = value;
                            break;
                        case "progress": progress?.Report(new(data.GetProperty("percent").GetDouble(), data.GetProperty("message").GetString() ?? "")); break;
                    }
                }
            }
            catch { Kill(); throw; }
        }
        async Task ReadErrors()
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            { errors.AppendLine(line); if (errors.Length > 8000) errors.Remove(0, errors.Length - 8000); }
        }
        async Task WriteInput()
        {
            try { await process.StandardInput.WriteAsync(JsonSerializer.Serialize(original.Lines.Select(l => l.Text))); }
            catch (IOException) when (token.IsCancellationRequested || process.HasExited) { }
            finally { process.StandardInput.Close(); }
        }
        try
        {
            token.ThrowIfCancellationRequested();
            Log.Information("Offline lyric translation starting; model={Model}; source={Source}; target={Target}; device={Device}; lines={Lines}; output={Output}",
                options.Model, options.SourceLanguage, options.TargetLanguage, options.UseCuda ? "cuda" : "cpu", original.Lines.Count, output);
            process.Start();
            using var registration = token.Register(Kill);
            await Task.WhenAll(ReadOutput(), ReadErrors(), WriteInput(), process.WaitForExitAsync());
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException(errors.Length == 0 ? $"翻译引擎退出代码 {process.ExitCode}" : errors.ToString().Trim());
            if (translated.Any(text => text is null)) throw new InvalidDataException("翻译引擎未返回完整歌词，已有文件保持不变。");
            var result = new LrcDocument(original.Lines.Select((line, i) => new LyricLine(line.Time, translated[i]!)).ToArray());
            await File.WriteAllTextAsync(temporary, LyricTranslation.Serialize(result, options.TargetLanguage), new UTF8Encoding(false), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite);
            Log.Information("Offline lyric translation completed; lines={Lines}; output={Output}", translated.Length, output);
            return output;
        }
        catch (OperationCanceledException) { Log.Information("Lyric translation cancelled; output={Output}", output); throw; }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception ex) { Log.Error(ex, "Lyric translation failed; output={Output}", output); throw; }
        finally { Kill(); if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
