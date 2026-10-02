using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AudioPlayer.Models;

namespace AudioPlayer.Services;

public sealed record BatchRecognitionProgress(int Index, RecognitionState State, double Percent, string Message, RecognizedSegment? Segment = null);
public sealed record BatchRecognitionResult(int Completed, int Skipped, int Failed);

public sealed class BatchTranscriptionService(string? scriptPath = null)
{
    private sealed record Job(int Index, string Audio, string Output, string Temporary);
    public async Task<BatchRecognitionResult> RunAsync(PlayerSettings settings, IReadOnlyList<string> audioPaths, bool overwrite,
        Action<BatchRecognitionProgress> progress, CancellationToken cancellationToken, RecognitionPauseControl? pauseControl = null)
    {
        // Capture engine settings once; editing settings must not change a running batch.
        string python = settings.PythonPath.Trim(), model = settings.ModelPath, language = settings.Language.Trim();
        bool cuda = settings.UseCuda, vad = settings.SpeechVad;
        int workers = Math.Clamp(settings.RecognitionParallelism, 1, 4);
        var jobs = new List<Job>();
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var finished = new HashSet<int>();
        int completed = 0, skipped = 0, failed = 0;
        void Notify(int index, RecognitionState state, string message, double percent = 100) => progress(new(index, state, percent, message));
        for (int index = 0; index < audioPaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string audio = Path.GetFullPath(audioPaths[index]), output = Path.ChangeExtension(audio, ".lrc");
                if (!File.Exists(audio)) throw new FileNotFoundException("音频文件不存在。", audio);
                if (string.Equals(audio, output, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("LRC 文件不能作为识别音频。");
                if (!outputs.Add(output)) { skipped++; Notify(index, RecognitionState.Skipped, "已跳过：与队列前项的同名 LRC 路径冲突"); continue; }
                if (!overwrite && File.Exists(output)) { skipped++; Notify(index, RecognitionState.Skipped, "已跳过：同名 LRC 已存在"); continue; }
                jobs.Add(new(index, audio, output, Path.Combine(Path.GetDirectoryName(output)!, "." + Guid.NewGuid().ToString("N") + ".lrc.tmp")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { failed++; Notify(index, RecognitionState.Failed, ex.Message); }
        }
        if (jobs.Count == 0) return new(completed, skipped, failed);
        string script = scriptPath ?? Path.Combine(AppContext.BaseDirectory, "recognition", "transcribe.py");
        if (!Directory.Exists(model)) throw new InvalidOperationException("请先在设置中选择完整的本地模型。");
        if (!File.Exists(script)) throw new FileNotFoundException("找不到识别脚本。", script);
        string manifest = Path.Combine(Path.GetTempPath(), "shengyu-batch-" + Guid.NewGuid().ToString("N") + ".json");
        var start = new ProcessStartInfo
        {
            FileName = python, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-u", script, "--model", model, "--device", cuda ? "cuda" : "cpu", "--language", language, "--batch", manifest }) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--workers"); start.ArgumentList.Add(Math.Min(workers, jobs.Count).ToString());
        if (pauseControl is not null) { start.ArgumentList.Add("--pause-file"); start.ArgumentList.Add(pauseControl.SignalPath); }
        if (vad) start.ArgumentList.Add("--vad");
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        using var process = new Process { StartInfo = start };
        var error = new StringBuilder();
        var byIndex = jobs.ToDictionary(j => j.Index);
        async Task ReadEventsAsync()
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (cancellationToken.IsCancellationRequested) continue;
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (document)
                {
                    var root = document.RootElement;
                    if (!root.TryGetProperty("index", out var id) || !id.TryGetInt32(out int index) || !byIndex.TryGetValue(index, out var job) || finished.Contains(index)) continue;
                    string state = root.GetProperty("state").GetString() ?? "";
                    string message = root.GetProperty("message").GetString() ?? "";
                    if (state == "segment")
                    {
                        double begin = root.GetProperty("start").GetDouble(), end = root.GetProperty("end").GetDouble();
                        if (double.IsFinite(begin) && double.IsFinite(end) && begin >= 0 && end >= begin)
                            progress(new(index, RecognitionState.Running, 0, message, new(begin, end, root.GetProperty("text").GetString() ?? "")));
                        continue;
                    }
                    if (state == "completed")
                    {
                        try
                        {
                            if (!File.Exists(job.Temporary) || new FileInfo(job.Temporary).Length == 0) throw new IOException("未生成有效歌词文件。");
                            // No overwrite unless explicitly selected, including files created during recognition.
                            File.Move(job.Temporary, job.Output, overwrite);
                            completed++; Notify(index, RecognitionState.Completed, "已生成：" + Path.GetFileName(job.Output));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { failed++; Notify(index, RecognitionState.Failed, "保存失败：" + ex.Message); }
                        finished.Add(index);
                    }
                    else if (state == "failed") { failed++; finished.Add(index); Notify(index, RecognitionState.Failed, message); }
                    else if (state == "paused") Notify(index, RecognitionState.Paused, message, Math.Clamp(root.GetProperty("percent").GetDouble(), 0, 99));
                    else if (state == "running") Notify(index, RecognitionState.Running, message, Math.Clamp(root.GetProperty("percent").GetDouble(), 0, 99));
                }
            }
        }
        async Task ReadErrorsAsync()
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            { error.AppendLine(line); if (error.Length > 12000) error.Remove(0, error.Length - 12000); }
        }
        void Kill()
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        try
        {
            File.WriteAllText(manifest, JsonSerializer.Serialize(jobs.Select(j => new { index = j.Index, audio = j.Audio, temporary = j.Temporary })), new UTF8Encoding(false));
            cancellationToken.ThrowIfCancellationRequested();
            try { process.Start(); }
            catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException("无法启动本地 Python：" + ex.Message, ex); }
            using var registration = cancellationToken.Register(Kill);
            await Task.WhenAll(ReadEventsAsync(), ReadErrorsAsync(), process.WaitForExitAsync());
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException(error.Length > 0 ? error.ToString().Trim() : $"识别进程退出，代码 {process.ExitCode}。");
            foreach (var job in jobs.Where(j => !finished.Contains(j.Index)))
            { failed++; Notify(job.Index, RecognitionState.Failed, "识别进程未返回该文件的完成结果"); }
            return new(completed, skipped, failed);
        }
        finally
        {
            Kill();
            foreach (string path in jobs.Select(j => j.Temporary).Append(manifest))
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
