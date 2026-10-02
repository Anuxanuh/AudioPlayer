namespace AudioPlayer.Services;

public sealed class PortablePaths
{
    public string Root { get; }
    public PortablePaths(string? root = null) => Root = Path.GetFullPath(root ?? AppContext.BaseDirectory);
    public string Expand(string path) => string.IsNullOrWhiteSpace(path) ? path : Path.GetFullPath(path, Root);
    public string ExpandExecutable(string path) => Path.IsPathRooted(path) || path.Contains('/') || path.Contains('\\') ? Expand(path) : path;
    public string Store(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return path;
        string relative = Path.GetRelativePath(Root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative)
            ? relative.Replace('\\', '/') : path;
    }
}
