using System.Runtime.InteropServices;
using System.Text.Json;
using AudioPlayer.Models;

namespace AudioPlayer.Services;

public static class PortableVerification
{
    public static async Task<int> RunAsync(string? sampleAudio)
    {
        string data = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(data);
        var settings = new PlayerSettings();
        LocalEngineLocator.ApplyDefaults(settings);
        try
        {
            var report = await new EnvironmentInspectionService().InspectAsync(settings.PythonPath, CancellationToken.None);
            bool transcription = false;
            if (sampleAudio is not null)
            {
                await new TranscriptionService().RunAsync(settings, Path.GetFullPath(sampleAudio), Path.Combine(data, "portable-check.lrc"), new Progress<RecognitionProgress>(), CancellationToken.None);
                transcription = LrcDocument.Load(Path.Combine(data, "portable-check.lrc")).Lines.Any(l => !string.IsNullOrWhiteSpace(l.Text));
            }
            var root = new PortablePaths();
            bool localPython = !Path.IsPathRooted(root.Store(settings.PythonPath));
            bool localModels = !Path.IsPathRooted(root.Store(settings.ModelsDirectory));
            var result = new { root = AppContext.BaseDirectory, runtime = RuntimeInformation.FrameworkDescription, python = settings.PythonPath, models = settings.ModelsDirectory, modelCount = ModelCatalog.Discover(settings.ModelsDirectory).Count, localPython, localModels, report, transcription };
            File.WriteAllText(Path.Combine(data, "portable-check.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return report.CpuReady && localPython && localModels && (sampleAudio is null || transcription) ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(data, "portable-check.json"), JsonSerializer.Serialize(new { error = ex.ToString() }));
            return 1;
        }
    }
}
