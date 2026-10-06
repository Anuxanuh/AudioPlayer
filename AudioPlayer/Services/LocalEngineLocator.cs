using AudioPlayer.Models;

namespace AudioPlayer.Services;

public static class LocalEngineLocator
{
    public static void ApplyDefaults(PlayerSettings settings)
    {
        bool portable = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; directory is not null && depth < (portable ? 1 : 6); depth++, directory = directory.Parent)
        {
            if (settings.PythonPath == "python" || !File.Exists(settings.PythonPath))
            {
                foreach (string relative in new[] { "python/python.exe", "runtime/python/python.exe", ".venv/Scripts/python.exe" })
                {
                    string candidate = Path.Combine(directory.FullName, relative);
                    if (File.Exists(candidate)) { settings.PythonPath = candidate; break; }
                }
            }
            if (string.IsNullOrWhiteSpace(settings.ModelsDirectory) || !Directory.Exists(settings.ModelsDirectory))
            {
                string candidate = Path.Combine(directory.FullName, "models");
                if (Directory.Exists(candidate)) settings.ModelsDirectory = candidate;
            }
            if (File.Exists(settings.PythonPath) && Directory.Exists(settings.ModelsDirectory)) break;
        }
        if (!Directory.Exists(settings.ModelsDirectory) && Directory.Exists(settings.ModelPath))
            settings.ModelsDirectory = Path.GetDirectoryName(settings.ModelPath) ?? "";
        var models = ModelCatalog.Discover(settings.ModelsDirectory);
        if (string.IsNullOrWhiteSpace(settings.TranslationModelPath)) settings.TranslationModelPath = string.IsNullOrWhiteSpace(settings.ModelsDirectory)
            ? LyricTranslation.DefaultModelPath : Path.Combine(settings.ModelsDirectory, "translation", LyricTranslation.ModelId);
        if (!models.Any(m => m.DirectoryPath.Equals(settings.ModelPath, StringComparison.OrdinalIgnoreCase)))
            settings.ModelPath = (models.FirstOrDefault(m => m.Id == settings.ModelId) ?? models.FirstOrDefault(m => m.Id == "tiny") ?? models.FirstOrDefault())?.DirectoryPath ?? "";
    }
}

