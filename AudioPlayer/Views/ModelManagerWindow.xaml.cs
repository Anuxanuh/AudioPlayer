using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AudioPlayer.Models;
using AudioPlayer.Services;
using Microsoft.Win32;

namespace AudioPlayer.Views;

public partial class ModelManagerWindow : Window
{
    public sealed class OnlineModel : ObservableObject
    {
        public string Id { get; init; } = "";
        public string Repo { get; init; } = "";
        private string _versionLabel = "在线版本待刷新", _sizeLabel = "待查询", _status = "";
        public string VersionLabel { get => _versionLabel; set => Set(ref _versionLabel, value); }
        public string SizeLabel { get => _sizeLabel; set => Set(ref _sizeLabel, value); }
        public string Status { get => _status; set => Set(ref _status, value); }
        public string Detail { get; set; } = "";
    }
    private readonly PlayerSettings _settings;
    private readonly bool _refreshOnLoad;
    private readonly ObservableCollection<OnlineModel> _models = new();
    private CancellationTokenSource? _operation;
    private bool _busy, _closeRequested;
    public event Action? ModelsChanged;
    public ModelManagerWindow(PlayerSettings settings, bool refreshOnLoad = true)
    {
        _settings = settings; _refreshOnLoad = refreshOnLoad;
        InitializeComponent();
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "recognition", "model_catalog.json")));
        foreach (var entry in catalog.RootElement.EnumerateArray()) _models.Add(new OnlineModel { Id = entry.GetProperty("id").GetString()!, Repo = entry.GetProperty("repo").GetString()! });
        OnlineModels.ItemsSource = _models;
        OnlineModels.SelectedIndex = 0;
        UpdateLocal();
    }
    private string Root => string.IsNullOrWhiteSpace(_settings.ModelsDirectory) ? Path.Combine(AppContext.BaseDirectory, "models") : Path.GetFullPath(_settings.ModelsDirectory);
    private bool IsLocal(OnlineModel model) => ModelCatalog.IsComplete(Path.Combine(Root, "faster-whisper-" + model.Id));
    private void UpdateLocal()
    {
        ModelDirectoryText.Text = "模型保存目录：" + Root;
        foreach (var model in _models) model.Status = IsLocal(model) ? "已下载" : "尚未下载";
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        RefreshButton.IsEnabled = FolderButton.IsEnabled = !_busy;
        DownloadButton.IsEnabled = !_busy && OnlineModels.SelectedItem is OnlineModel selected && !IsLocal(selected);
        CancelButton.IsEnabled = _busy;
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) { if (_refreshOnLoad) await RunAsync(null); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(null);
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (OnlineModels.SelectedItem is OnlineModel model) await RunAsync(model.Id);
    }
    private async Task RunAsync(string? downloadId)
    {
        if (_busy) return;
        _busy = true; UpdateButtons(); DownloadProgress.Value = 0;
        DownloadProgress.IsIndeterminate = downloadId is null;
        StatusText.Text = downloadId is null ? "正在读取 Hugging Face 在线版本和文件大小…" : "准备下载 " + downloadId + "…";
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        try
        {
            await new OnlineModelService().RunAsync(_settings.PythonPath, Root, downloadId, data =>
            {
                switch (data.GetProperty("type").GetString())
                {
                    case "catalog":
                        int failures = 0;
                        foreach (var entry in data.GetProperty("models").EnumerateArray())
                        {
                            var model = _models.FirstOrDefault(m => m.Id == entry.GetProperty("id").GetString());
                            if (model is null) continue;
                            string error = entry.GetProperty("error").GetString() ?? "";
                            if (error.Length > 0) { model.Status = IsLocal(model) ? "已下载 / 查询失败" : "在线查询失败"; model.Detail = error; failures++; continue; }
                            model.VersionLabel = "在线版本：" + entry.GetProperty("revision").GetString()![..12];
                            double bytes = entry.GetProperty("bytes").GetDouble();
                            model.SizeLabel = bytes >= 1024 * 1024 * 1024 ? $"{bytes / 1024 / 1024 / 1024:F2} GiB" : $"{bytes / 1024 / 1024:F0} MiB";
                            model.Status = IsLocal(model) ? "已下载" : "可下载"; model.Detail = "https://huggingface.co/" + model.Repo;
                        }
                        StatusText.Text = failures == 0 ? $"在线信息已更新，共 {_models.Count} 个多语言模型。" : $"有 {failures} 个模型查询失败；可重试，已下载模型仍可离线使用。";
                        break;
                    case "progress":
                        DownloadProgress.Value = data.GetProperty("percent").GetDouble();
                        StatusText.Text = data.GetProperty("message").GetString();
                        break;
                    case "completed":
                        DownloadProgress.Value = 100;
                        _settings.ModelsDirectory = Root;
                        StatusText.Text = data.GetProperty("message").GetString();
                        UpdateLocal(); ModelsChanged?.Invoke();
                        break;
                }
            }, cancellation.Token);
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消。未完成文件保留在 .downloads，下次下载可续传；完整模型不受影响。"; }
        catch (Exception ex) { StatusText.Text = "操作失败：" + ex.Message; }
        finally
        {
            _operation = null; _busy = false; DownloadProgress.IsIndeterminate = false; UpdateButtons();
            if (_closeRequested) Close();
        }
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) { if (IsInitialized) UpdateButtons(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择模型下载目录" };
        if (dialog.ShowDialog(this) == true) { _settings.ModelsDirectory = dialog.FolderName; UpdateLocal(); ModelsChanged?.Invoke(); }
    }
    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (OnlineModels.SelectedItem is not OnlineModel model) return;
        try { Process.Start(new ProcessStartInfo("https://huggingface.co/" + model.Repo) { UseShellExecute = true }); }
        catch (Win32Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true; _closeRequested = true; _operation?.Cancel();
    }
}
