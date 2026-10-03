using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AudioPlayer.Services;

public sealed record EnvironmentCheck(string Name, string Status, string Detail)
{
    public string Label => Status switch { "ok" => "✓  " + Name, "error" => "✕  " + Name, "warning" => "!  " + Name, _ => "•  " + Name };
    public string Color => Status switch { "ok" => "#176E61", "error" => "#B42318", "warning" => "#986200", _ => "#586D69" };
}
public sealed record EnvironmentReport(IReadOnlyList<EnvironmentCheck> Items, bool CpuReady, bool CudaReady, string Summary);

public sealed class EnvironmentInspectionService
{
    public async Task<EnvironmentReport> InspectAsync(string pythonPath, CancellationToken token)
    {
        Log.Information("Inspecting local recognition environment; python={Python}", pythonPath);
        var start = new ProcessStartInfo
        {
            FileName = pythonPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "recognition", "inspect_environment.py"));
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["HF_HUB_OFFLINE"] = "1";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            using var registration = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            });
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string json = await output, diagnostic = await error;
            timeout.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException($"检测进程退出代码 {process.ExitCode}。" + (diagnostic.Length > 5000 ? diagnostic[^5000..] : diagnostic));
            var report = JsonSerializer.Deserialize<EnvironmentReport>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException("无检测结果");
            Log.Information("Environment inspection completed; cpuReady={CpuReady}; cudaReady={CudaReady}; checks={@Checks}", report.CpuReady, report.CudaReady, report.Items);
            return report;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { Log.Warning("Environment inspection timed out"); return Error("检测超时；请检查 Python 与驱动是否可以正常加载。"); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or JsonException or IOException)
        { Log.Error(ex, "Environment inspection failed"); return Error("环境检测失败：" + ex.Message); }
    }
    private static EnvironmentReport Error(string message) => new(new[] { new EnvironmentCheck("Python / 检测程序", "error", message) }, false, false, "请检查配置的 Python 路径，或重新解压完整便携包。");
}
