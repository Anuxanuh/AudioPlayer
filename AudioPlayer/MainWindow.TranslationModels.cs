using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using AudioPlayer.Services;
using Microsoft.Win32;

namespace AudioPlayer;

public partial class MainWindow
{
    private void BrowseTranslationModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择完整的 M2M100 CTranslate2 翻译模型目录" };
        if (dialog.ShowDialog(this) == true) _view.Settings.TranslationModelPath = dialog.FolderName;
    }
    private void TranslationSource_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(LyricTranslation.ModelRepository) { UseShellExecute = true }); }
        catch (Win32Exception ex) { _view.TranslationModelStatus = ex.Message; }
    }
    private async void DownloadTranslationModel_Click(object sender, RoutedEventArgs e)
    { if (!_view.TranslationModelBusy) await (_translationDownloadTask = DownloadTranslationModelAsync()); }
    private async Task DownloadTranslationModelAsync()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _translationDownloadCancellation = cancellation;
        _view.TranslationModelBusy = true; _view.TranslationModelPercent = 0; CancelAutoTranslation();
        try
        {
            string root = string.IsNullOrWhiteSpace(_view.Settings.ModelsDirectory) ? Path.Combine(AppContext.BaseDirectory, "models", "translation") : Path.Combine(_view.Settings.ModelsDirectory, "translation");
            await new OnlineModelService().RunAsync(_view.Settings.PythonPath, root, LyricTranslation.ModelId, data =>
            {
                switch (data.GetProperty("type").GetString())
                {
                    case "progress": _view.TranslationModelPercent = data.GetProperty("percent").GetDouble(); _view.TranslationModelStatus = data.GetProperty("message").GetString()!; break;
                    case "completed": _view.Settings.TranslationModelPath = data.GetProperty("path").GetString()!; _view.TranslationModelPercent = 100; _view.TranslationModelStatus = "模型已就绪，可断网自动翻译。"; break;
                }
            }, cancellation.Token, translation: true);
        }
        catch (OperationCanceledException) { _view.TranslationModelStatus = "下载已取消，下次下载可以续传。"; }
        catch (Exception ex) { _view.TranslationModelStatus = "下载失败：" + ex.Message; }
        finally { _translationDownloadCancellation = null; _view.TranslationModelBusy = false; CancelAutoTranslation(); EnsureAutoTranslation(); }
    }
    private void CancelTranslationDownload_Click(object sender, RoutedEventArgs e) => _translationDownloadCancellation?.Cancel();
}
