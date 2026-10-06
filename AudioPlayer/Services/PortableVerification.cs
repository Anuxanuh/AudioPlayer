using System.Runtime.InteropServices;
using System.Text.Json;
using AudioPlayer.Models;

namespace AudioPlayer.Services;

public static class PortableVerification
{
    public static async Task<int> RunAsync(string? sampleAudio, string? pythonOverride = null)
    {
        string data = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(data);
        var settings = new PlayerSettings();
        LocalEngineLocator.ApplyDefaults(settings);
        if (pythonOverride is not null) settings.PythonPath = pythonOverride;
        try
        {
            bool bundledPython = true;
            string manifest = Path.Combine(AppContext.BaseDirectory, "package-manifest.json");
            if (File.Exists(manifest))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                bundledPython = !json.RootElement.TryGetProperty("bundledPython", out var value) || value.GetBoolean();
            }
            bool pythonAvailable = PythonEnvironment.ResolveExecutable(settings.PythonPath) is not null;
            var report = await new EnvironmentInspectionService().InspectAsync(settings.PythonPath, CancellationToken.None);
            bool transcription = false;
            if (sampleAudio is not null)
            {
                await new TranscriptionService().RunAsync(settings, Path.GetFullPath(sampleAudio), Path.Combine(data, "portable-check.lrc"), new Progress<RecognitionProgress>(), CancellationToken.None);
                transcription = LrcDocument.Load(Path.Combine(data, "portable-check.lrc")).Lines.Any(l => !string.IsNullOrWhiteSpace(l.Text));
            }
            var root = new PortablePaths();
            bool localPython = pythonAvailable && Path.IsPathRooted(settings.PythonPath) && !Path.IsPathRooted(root.Store(settings.PythonPath));
            bool localModels = !Path.IsPathRooted(root.Store(settings.ModelsDirectory));
            var result = new { root = AppContext.BaseDirectory, runtime = RuntimeInformation.FrameworkDescription, python = settings.PythonPath, bundledPython, pythonAvailable, models = settings.ModelsDirectory, modelCount = ModelCatalog.Discover(settings.ModelsDirectory).Count, localPython, localModels, report, transcription };
            File.WriteAllText(Path.Combine(data, "portable-check.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            // An external-Python package remains a usable player before the user prepares Python.
            return (!bundledPython || (report.CpuReady && localPython)) && localModels && (sampleAudio is null || transcription) ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(data, "portable-check.json"), JsonSerializer.Serialize(new { error = ex.ToString() }));
            return 1;
        }
    }
}
