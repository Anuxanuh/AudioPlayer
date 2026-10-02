using System.Text.Json;

namespace AudioPlayer.Services;

public sealed record LocalModel(string Id, string DirectoryPath, string Description, long Bytes)
{
    public string DisplayName => $"{Id} · {Description} · {(Bytes / 1024d / 1024d >= 1024 ? $"{Bytes / 1024d / 1024d / 1024d:F2} GB" : $"{Bytes / 1024d / 1024d:F0} MB")}";
}

public static class ModelCatalog
{
    public static IReadOnlyList<LocalModel> Discover(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<LocalModel>();
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string catalogPath = Path.Combine(AppContext.BaseDirectory, "recognition", "model_catalog.json");
        if (File.Exists(catalogPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(catalogPath));
                foreach (var entry in document.RootElement.EnumerateArray())
                    descriptions[entry.GetProperty("id").GetString()!] = entry.GetProperty("description").GetString()!;
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        var models = new List<LocalModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Scan(string directory, int depth, string? inheritedId = null)
        {
            if (depth > 4) return;
            try
            {
                string name = Path.GetFileName(directory);
                if (name.StartsWith('.') || name is "blobs" or "refs" or "__pycache__") return;
                string id = name.Contains("--") ? name.Split("--")[^1] : name;
                if (id.StartsWith("faster-whisper-", StringComparison.OrdinalIgnoreCase)) id = id[15..];
                bool namedModel = descriptions.ContainsKey(id) || name.StartsWith("faster-whisper-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("models--", StringComparison.OrdinalIgnoreCase);
                if (!namedModel && inheritedId is not null) id = inheritedId;
                if (IsEnglishOnly(id)) return;
                if (IsComplete(directory))
                {
                    string path = Path.GetFullPath(directory);
                    if (seen.Add(path)) models.Add(new LocalModel(id, path, descriptions.GetValueOrDefault(id, "本地模型"), new FileInfo(Path.Combine(path, "model.bin")).Length));
                    return;
                }
                string snapshots = Path.Combine(directory, "snapshots");
                if (Directory.Exists(snapshots))
                {
                    string reference = Path.Combine(directory, "refs", "main");
                    string revision = File.Exists(reference) ? File.ReadAllText(reference).Trim() : "";
                    string? preferred = Directory.GetDirectories(snapshots).FirstOrDefault(d => Path.GetFileName(d) == revision && IsComplete(d))
                        ?? Directory.GetDirectories(snapshots).Where(IsComplete).OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
                    if (preferred is not null) Scan(preferred, depth + 2, id);
                    return;
                }
                foreach (string child in Directory.GetDirectories(directory)) Scan(child, depth + 1, namedModel ? id : inheritedId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { /* One folder being replaced or inaccessible must not hide the rest of the catalog. */ }
        }
        // Enumerate a repository snapshot before replacing the UI collection.
        if (IsComplete(root)) Scan(root, 0);
        foreach (string directory in Directory.GetDirectories(root)) Scan(directory, 1);
        return models.OrderBy(m => m.Bytes).ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static bool IsEnglishOnly(string id) => id.EndsWith(".en", StringComparison.OrdinalIgnoreCase) ||
        new[] { "distil-large-v2", "distil-large-v3", "distil-large-v3.5" }.Contains(id, StringComparer.OrdinalIgnoreCase);
    public static bool IsComplete(string directory)
    {
        try { return new[] { "model.bin", "config.json", "tokenizer.json" }.All(name => File.Exists(Path.Combine(directory, name)) && new FileInfo(Path.Combine(directory, name)).Length > 0); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    public static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return root;
        string full = Path.GetFullPath(root);
        if (!IsComplete(full)) return full;
        var directory = new DirectoryInfo(full);
        if (directory.Parent?.Name == "snapshots") return directory.Parent.Parent?.Parent?.FullName ?? full;
        if (directory.Name.StartsWith("faster-whisper-", StringComparison.OrdinalIgnoreCase)) return directory.Parent?.FullName ?? full;
        return full;
    }
}
