using System.IO;
using AudioPlayer.Services;

internal static partial class Program
{
    private static void PythonEnvironments()
    {
        string root = Path.Combine(_root, "环境 测试");
        string package = Path.Combine(root, "播放器");
        Directory.CreateDirectory(package);
        string Stub(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(root, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "resolver fixture; never executed");
            return path;
        }
        string host = Stub("本机 Python/python.exe");
        string alias = Stub("Microsoft/WindowsApps/python.exe");
        string missing = Path.Combine(root, "missing/python.exe");
        Assert(PythonEnvironment.FindDefault("python", package, true, [alias, missing, host]) == host, "External Python was not selected / Store alias was used");
        Assert(PythonEnvironment.FindDefault("python", package, true, [alias, missing]) is null, "Absent Python resolved to an execution alias");
        string ancestor = Stub("runtime/python/python.exe");
        Assert(PythonEnvironment.FindDefault("python", package, true, []) is null, "Portable package leaked an ancestor's environment");
        Assert(PythonEnvironment.FindDefault("python", package, false, []) == ancestor, "Development environment discovery regressed");
        string bundled = Stub("播放器/python/python.exe");
        Assert(PythonEnvironment.FindDefault("python", package, true, [host]) == bundled, "Bundled runtime must win over automatic system discovery");
        Assert(PythonEnvironment.FindDefault(host, package, true, []) == host, "User-selected Python was replaced");
        Assert(PythonEnvironment.FindDefault(missing, package, true, [host]) == bundled, "Moved/missing configured Python did not fall back");
        Assert(PythonEnvironment.ResolveExecutable('"' + host + '"') == host, "Quoted Unicode executable path was not resolved");
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(alias) + ";\"" + Path.GetDirectoryName(host) + "\"");
            Assert(PythonEnvironment.RequireExecutable("python") == host, "PATH resolution failed or launched a Store alias");
        }
        finally { Environment.SetEnvironmentVariable("PATH", originalPath); }
        try { PythonEnvironment.RequireExecutable(missing); throw new Exception("Missing Python should produce setup instructions"); }
        catch (InvalidOperationException ex) { Assert(ex.Message.Contains("请自行安装") && ex.Message.Contains("requirements-lock.txt"), "Missing Python instructions are incomplete"); }
        var report = new EnvironmentInspectionService().InspectAsync(missing, CancellationToken.None).GetAwaiter().GetResult();
        Assert(!report.CpuReady && report.Items.Any(item => item.Detail.Contains("请自行安装")), "Environment inspection lost the missing-Python guidance");
    }
}
