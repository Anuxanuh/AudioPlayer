using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AudioPlayer.Services;

public sealed class OnlineModelService
{
    public async Task RunAsync(string python, string modelRoot, string? downloadId, Action<JsonElement> onEvent, CancellationToken token)
    {
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-u", Path.Combine(AppContext.BaseDirectory, "recognition", "model_manager.py"), "--root", Path.GetFullPath(modelRoot) }) start.ArgumentList.Add(argument);
        if (downloadId is null) start.ArgumentList.Add("--list");
        else { start.ArgumentList.Add("--download"); start.ArgumentList.Add(downloadId); }
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["HF_HUB_OFFLINE"] = "0";
        using var process = new Process { StartInfo = start };
        var errors = new StringBuilder();
        void Kill()
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        async Task ReadEvents()
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (token.IsCancellationRequested) continue;
                try { using var document = JsonDocument.Parse(line); onEvent(document.RootElement); }
                catch (JsonException) { }
            }
        }
        async Task ReadErrors()
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            { errors.AppendLine(line); if (errors.Length > 8000) errors.Remove(0, errors.Length - 8000); }
        }
        try
        {
            token.ThrowIfCancellationRequested();
            process.Start();
            using var registration = token.Register(Kill);
            await Task.WhenAll(ReadEvents(), ReadErrors(), process.WaitForExitAsync());
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException(errors.Length == 0 ? $"模型操作退出代码 {process.ExitCode}" : errors.ToString().Trim());
        }
        finally { Kill(); }
    }
}
