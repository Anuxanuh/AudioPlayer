using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using AudioPlayer.Models;
using AudioPlayer.Plugin.Abstractions;

namespace AudioPlayer.Services;

public sealed record PluginManifest(string Id, string Name, string Version, string Description, string Assembly, string EntryType, int ApiVersion);

public sealed class PluginEntry : ObservableObject
{
    public required PluginManifest Manifest { get; init; }
    public required string DirectoryPath { get; init; }
    public string Name => Manifest.Name;
    public string Description => Manifest.Description;
    public string Version => Manifest.Version;
    public FrameworkElement? Page { get; internal set; }
    internal IPlayerPlugin? Instance;
    internal AssemblyLoadContext? LoadContext;
    internal bool StartedEnabled;
    private bool _enabled;
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) Raise(nameof(Status)); } }
    internal string StartupStatus = "未启用";
    public string Status => Enabled != StartedEnabled ? (Enabled ? "重启播放器后启用" : "重启播放器后停用") : StartupStatus;
}

public sealed class PluginManager : IDisposable
{
    public IReadOnlyList<PluginEntry> Entries { get; private set; } = Array.Empty<PluginEntry>();
    public IReadOnlyList<string> Errors => _errors;
    private readonly List<string> _errors = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;
    private sealed class PluginLoadContext(string assembly) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assembly);
        protected override Assembly? Load(AssemblyName name)
        {
            // Framework/WPF and the contract retain the host's type identity.
            if (name.Name == typeof(IPlayerPlugin).Assembly.GetName().Name) return typeof(IPlayerPlugin).Assembly;
            string? path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }

    public void Start(string root, string dataRoot, IDictionary<string, bool> enabled, Func<string> python, Action changed, IPlaybackHost? playback = null)
    {
        if (!Directory.Exists(root)) return;
        var entries = new List<PluginEntry>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] directories;
        try { directories = Directory.GetDirectories(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _errors.Add("插件目录无法读取：" + ex.Message); Log.Warning(ex, "Plugin directory unavailable"); return; }
        foreach (string directory in directories.Order(StringComparer.OrdinalIgnoreCase))
        {
            string manifestPath = Path.Combine(directory, "plugin.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("清单为空");
                if (string.IsNullOrWhiteSpace(manifest.Id) || !Regex.IsMatch(manifest.Id, @"^[a-z0-9][a-z0-9._-]{0,63}$") || !ids.Add(manifest.Id)) throw new InvalidDataException("插件 ID 无效或重复");
                if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.ApiVersion is not (1 or 2)) throw new InvalidDataException("插件名称或 API 版本不兼容");
                if (string.IsNullOrWhiteSpace(manifest.Assembly) || Path.GetFileName(manifest.Assembly) != manifest.Assembly || !manifest.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("程序集必须位于插件目录内");
                var entry = new PluginEntry { Manifest = manifest, DirectoryPath = Path.GetFullPath(directory), Enabled = enabled.TryGetValue(manifest.Id, out bool value) && value };
                entry.StartedEnabled = entry.Enabled;
                entries.Add(entry);
                if (entry.Enabled)
                {
                    try
                    {
                        string path = Path.Combine(entry.DirectoryPath, manifest.Assembly);
                        var context = new PluginLoadContext(path);
                        entry.LoadContext = context;
                        var assembly = context.LoadFromAssemblyPath(path);
                        Type type = assembly.GetType(manifest.EntryType, throwOnError: true)!;
                        if (!typeof(IPlayerPlugin).IsAssignableFrom(type)) throw new InvalidDataException("入口类型未实现 IPlayerPlugin");
                        entry.Instance = (IPlayerPlugin)Activator.CreateInstance(type)!;
                        var pluginContext = new PluginContext(AppContext.BaseDirectory, entry.DirectoryPath, Path.Combine(dataRoot, "plugins", manifest.Id), python,
                            (level, message) => { if (level == "error") Log.Error("Plugin {Plugin}: {Message}", manifest.Id, message); else Log.Information("Plugin {Plugin}: {Message}", manifest.Id, message); }, _shutdown.Token) { Playback = playback };
                        entry.Page = entry.Instance.CreatePage(pluginContext);
                        entry.StartupStatus = "已加载";
                        Log.Information("Plugin loaded; id={Id}; version={Version}", manifest.Id, manifest.Version);
                    }
                    catch (Exception ex)
                    {
                        entry.StartupStatus = "加载失败：" + ex.GetBaseException().Message;
                        try { entry.Instance?.Dispose(); } catch { }
                        entry.Instance = null;
                        Log.Error(ex, "Plugin load failed; id={Id}", manifest.Id);
                    }
                }
                entry.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PluginEntry.Enabled)) { enabled[manifest.Id] = entry.Enabled; changed(); } };
            }
            catch (Exception ex)
            {
                _errors.Add(Path.GetFileName(directory) + "：" + ex.Message);
                Log.Warning(ex, "Plugin manifest rejected; path={Path}", manifestPath);
            }
        }
        Entries = entries;
    }
    public async Task StopAsync()
    {
        _shutdown.Cancel();
        foreach (var entry in Entries.Where(e => e.Instance is not null))
            try { await entry.Instance!.StopAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (Exception ex) { Log.Warning(ex, "Plugin shutdown failed; id={Id}", entry.Manifest.Id); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _shutdown.Cancel();
        foreach (var entry in Entries)
            try { entry.Instance?.Dispose(); } catch (Exception ex) { Log.Warning(ex, "Plugin disposal failed; id={Id}", entry.Manifest.Id); }
        _shutdown.Dispose();
    }
}
