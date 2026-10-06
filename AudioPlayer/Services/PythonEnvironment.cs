using Microsoft.Win32;

namespace AudioPlayer.Services;

public static class PythonEnvironment
{
    public const string SetupMessage = "未找到 Python 环境。请自行安装 Python 3.13 x64，或准备自己的虚拟环境，"
        + "然后在“设置 → 离线识别引擎”选择 python.exe。\n"
        + "官网下载：https://www.python.org/downloads/windows/\n\n"
        + "识别和翻译还需使用所选 Python 安装程序目录 recognition/requirements-lock.txt 中的依赖，"
        + "再点击“检测本机环境”。具体命令见 README.md。\n\n普通音频播放和已有歌词仍可使用。";

    public static string RequireExecutable(string configured) => ResolveExecutable(configured)
        ?? throw new InvalidOperationException(SetupMessage);

    public static string? ResolveExecutable(string configured)
    {
        configured = configured.Trim().Trim('"');
        if (string.IsNullOrEmpty(configured)) return null;
        if (Path.IsPathRooted(configured) || configured.Contains('/') || configured.Contains('\\'))
        {
            try { return ExistingExecutable(Path.GetFullPath(configured, AppContext.BaseDirectory)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
        }
        return SystemCandidates(configured).Select(ExistingExecutable).FirstOrDefault(path => path is not null);
    }

    internal static string? FindDefault(string configured, string root, bool portable, IEnumerable<string>? systemCandidates = null)
    {
        // Explicit paths win. A moved/missing environment falls back to this package, then the machine.
        bool defaultCommand = configured.Trim() is "" or "python" or "python.exe" or "python3" or "python3.exe";
        if (!defaultCommand && ResolveExecutable(configured) is { } selected) return selected;
        var directory = new DirectoryInfo(root);
        for (int depth = 0; directory is not null && depth < (portable ? 1 : 6); depth++, directory = directory.Parent)
            foreach (string relative in new[] { "python/python.exe", "runtime/python/python.exe", ".venv/Scripts/python.exe" })
                if (ExistingExecutable(Path.Combine(directory.FullName, relative)) is { } bundled) return bundled;
        return (systemCandidates ?? SystemCandidates("python")).Select(ExistingExecutable).FirstOrDefault(path => path is not null);
    }

    private static string? ExistingExecutable(string path)
    {
        // Windows Store execution aliases are placeholders and can open the Store instead of Python.
        if (!File.Exists(path) || string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "WindowsApps", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.GetFullPath(path);
    }

    private static IEnumerable<string> SystemCandidates(string command)
    {
        string name = Path.HasExtension(command) ? command : command + ".exe";
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string directory = entry.Trim().Trim('"');
            // Do not search relative PATH entries or the current working directory.
            if (Path.IsPathFullyQualified(directory)) yield return Path.Combine(directory, name);
        }
        if (name.Equals("python.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("python3.exe", StringComparison.OrdinalIgnoreCase))
            foreach (string path in RegisteredPython()) yield return path;
    }

    private static IEnumerable<string> RegisteredPython()
    {
        var candidates = new List<string>();
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var python = registry.OpenSubKey(@"SOFTWARE\Python");
                if (python is null) continue;
                foreach (string company in python.GetSubKeyNames())
                {
                    using var vendor = python.OpenSubKey(company);
                    if (vendor is null) continue;
                    foreach (string tag in vendor.GetSubKeyNames().OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase))
                    {
                        using var install = vendor.OpenSubKey(tag + @"\InstallPath");
                        if (install?.GetValue("ExecutablePath") is string executable) candidates.Add(executable);
                        else if (install?.GetValue("") is string directory && !string.IsNullOrWhiteSpace(directory)) candidates.Add(Path.Combine(directory, "python.exe"));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { Log.Debug(ex, "Unable to inspect registered Python installations; hive={Hive}", hive); }
        }
        return candidates;
    }
}
