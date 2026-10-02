using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AudioPlayer.Models;

namespace AudioPlayer.Services;

public sealed record RecognitionProgress(double Percent, string Message);
public sealed record RecognizedSegment(double Start, double End, string Text);

public sealed class TranscriptionService
{
    private readonly string _scriptPath;
    public TranscriptionService(string? scriptPath = null) => _scriptPath = scriptPath ?? Path.Combine(AppContext.BaseDirectory, "recognition", "transcribe.py");

    public async Task RunAsync(PlayerSettings settings, string? audio, string? output, IProgress<RecognitionProgress> progress, CancellationToken cancellationToken,
        Action<RecognizedSegment>? onSegment = null, bool overwrite = true)
    {
        if (!Directory.Exists(settings.ModelPath)) throw new InvalidOperationException("请先在设置中选择本地 faster-whisper 模型文件夹。");
        string script = _scriptPath;
        if (!File.Exists(script)) throw new FileNotFoundException("找不到本地识别脚本，请重新构建或完整复制发布目录。", script);
        string? temporary = output is null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "." + Guid.NewGuid().ToString("N") + ".lrc.tmp");
        var start = new ProcessStartInfo
        {
            FileName = settings.PythonPath.Trim(), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-u", script, "--model", settings.ModelPath, "--device", settings.UseCuda ? "cuda" : "cpu" }) start.ArgumentList.Add(argument);
        if (audio is null) start.ArgumentList.Add("--check");
        else
        {
            foreach (string argument in new[] { "--audio", audio, "--output", temporary!, "--language", settings.Language.Trim() }) start.ArgumentList.Add(argument);
            if (settings.SpeechVad) start.ArgumentList.Add("--vad");
            if (onSegment is not null) start.ArgumentList.Add("--stream");
        }
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        using var process = new Process { StartInfo = start };
        var error = new StringBuilder();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { process.Start(); }
            catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException("无法启动 Python。请在设置中选择已安装 faster-whisper 的 python.exe。", ex); }
            using var registration = cancellationToken.Register(() => Kill(process));
            Task stderr = DrainErrorAsync(process.StandardError, error);
            Task stdout = ReadProgressAsync(process.StandardOutput, progress, onSegment, cancellationToken);
            await Task.WhenAll(process.WaitForExitAsync(), stderr, stdout);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException(error.Length > 0 ? error.ToString().Trim() : $"识别进程退出，代码 {process.ExitCode}。");
            if (temporary is not null)
            {
                if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new InvalidOperationException("未生成歌词文件。");
                // Only replace the user's destination after successful completion; a cancelled job leaves it untouched.
                // An external/batch LRC created while streaming wins unless replacement was requested.
                if (!overwrite && File.Exists(output)) File.Delete(temporary);
                else File.Move(temporary, output!, overwrite);
            }
        }
        finally
        {
            Kill(process);
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task DrainErrorAsync(StreamReader reader, StringBuilder text)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            text.AppendLine(line);
            if (text.Length > 12000) text.Remove(0, text.Length - 12000);
        }
    }

    private static async Task ReadProgressAsync(StreamReader reader, IProgress<RecognitionProgress> progress, Action<RecognizedSegment>? onSegment, CancellationToken token)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (token.IsCancellationRequested) continue;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "segment")
                {
                    double start = root.GetProperty("start").GetDouble(), end = root.GetProperty("end").GetDouble();
                    if (double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end >= start)
                        onSegment?.Invoke(new(start, end, root.GetProperty("text").GetString() ?? ""));
                    continue;
                }
                string message = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
                double percent = root.TryGetProperty("percent", out var pct) ? pct.GetDouble() : 0;
                progress.Report(new RecognitionProgress(Math.Clamp(percent, 0, 100), message));
            }
            catch (JsonException) { /* Ignore non-protocol library diagnostic output. */ }
        }
    }
}
