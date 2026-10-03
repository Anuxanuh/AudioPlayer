using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AudioPlayer.Plugin.Abstractions;

namespace AudioPlayer.Plugin.Bilibili;

public sealed class WorkerClient(PluginContext context)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string DependencyDirectory(PluginContext context, string name)
    {
        string local = Path.Combine(context.PluginDirectory, name);
        if (Directory.Exists(local)) return local;
        for (var directory = new DirectoryInfo(context.ApplicationDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "runtime", "bilibili", name);
            if (Directory.Exists(candidate)) return candidate;
        }
        return local;
    }
    public async Task RunAsync(object request, Action<JsonElement> onEvent, CancellationToken token)
    {
        var info = new ProcessStartInfo(context.GetPythonPath())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false, true), StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8
        };
        // -I ignores PYTHONUTF8/PYTHONIOENCODING; use the interpreter option even on non-UTF-8 Windows.
        foreach (string argument in new[] { "-I", "-X", "utf8", "-u", Path.Combine(context.PluginDirectory, "worker.py") })
            info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        void Kill() { try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        try
        {
            token.ThrowIfCancellationRequested();
            process.Start();
            using var registration = token.Register(Kill);
            Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null); // Never log credentials/library dumps.
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, Json));
            process.StandardInput.Close();
            string? error = null;
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                token.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(line);
                var data = document.RootElement;
                if (data.GetProperty("type").GetString() == "error") error = data.GetProperty("message").GetString();
                onEvent(data);
            }
            await Task.WhenAll(process.WaitForExitAsync(), stderr);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException(error ?? $"插件进程退出（{process.ExitCode}），请检查 Python 和插件依赖。");
        }
        finally { Kill(); }
    }
}
