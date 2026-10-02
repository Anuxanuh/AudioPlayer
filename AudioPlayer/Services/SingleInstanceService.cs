using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AudioPlayer.Services;

/// <summary>One player per Windows user/session, including copies in other folders.</summary>
public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;
    private bool _disposed;
    public bool IsPrimary { get; }
    public SingleInstanceService(string? instanceKey = null)
    {
        string identity = instanceKey ?? $"ShengYu.AudioPlayer.{WindowsIdentity.GetCurrent().User!.Value}.{Process.GetCurrentProcess().SessionId}";
        _pipeName = identity + ".activation";
        _mutex = new Mutex(false, @"Local\" + identity);
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
    }
    public void StartListening(Func<string[], Task> activate)
    {
        if (!IsPrimary || _listener is not null) throw new InvalidOperationException("Only the first instance can listen.");
        _listener = ListenAsync(activate);
    }
    public async Task<bool> ActivateExistingAsync(string[] arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            if (int.TryParse(await reader.ReadLineAsync(timeout.Token), out int pid)) AllowSetForegroundWindow(pid);
            // Relative command-line paths belong to the new process's working directory.
            string[] files = arguments.Where(File.Exists).Select(Path.GetFullPath).ToArray();
            await writer.WriteLineAsync(JsonSerializer.Serialize(files).AsMemory(), timeout.Token);
            return await reader.ReadLineAsync(timeout.Token) == "ok";
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }
    private async Task ListenAsync(Func<string[], Task> activate)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_shutdown.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(Environment.ProcessId.ToString().AsMemory(), timeout.Token);
                string? message = await reader.ReadLineAsync(timeout.Token);
                if (message is null || message.Length > 65536) continue;
                string[] files = JsonSerializer.Deserialize<string[]>(message) ?? Array.Empty<string>();
                await activate(files);
                await writer.WriteLineAsync("ok".AsMemory(), timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException) { }
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        if (IsPrimary) _mutex.ReleaseMutex(); // Acquired/released on the application dispatcher thread.
        _mutex.Dispose();
    }
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
