using System.Windows;
using AudioPlayer.Models;
using AudioPlayer.Services;

namespace AudioPlayer;

public partial class MainWindow
{
    private readonly LyricTranslationService _lyricTranslator;
    private LrcDocument _translatedLyrics = new(Array.Empty<LyricLine>());
    private LrcDocument? _composedOriginal, _composedTranslation;
    private LrcDocument _displayLyrics = new(Array.Empty<LyricLine>());
    private LyricDisplayMode _composedMode;
    private CancellationTokenSource? _autoTranslationCancellation, _translationBatchCancellation, _translationDownloadCancellation;
    private Task? _autoTranslationTask, _translationBatchTask, _translationDownloadTask;
    private string? _autoTranslationAttempt;
    private long _autoTranslationRequest;

    private TranslationOptions TranslationSettings() => new(_view.Settings.PythonPath, _view.Settings.TranslationModelPath, "auto",
        _view.Settings.TranslationTargetLanguage, _view.Settings.TranslationUseCuda);
    private static string OriginalLyricsPath(Track track) => track.LyricsPath is { } custom && File.Exists(custom)
        ? custom : Path.ChangeExtension(track.FilePath, ".lrc");
    private void CancelAutoTranslation()
    {
        _autoTranslationRequest++; _autoTranslationCancellation?.Cancel(); _autoTranslationAttempt = null;
    }
    private void EnsureAutoTranslation()
    {
        if (!_ready || _exiting || _disposed || _current is not { } track) return;
        if (_view.Settings.LyricDisplayMode == LyricDisplayMode.Original)
        { CancelAutoTranslation(); _view.TranslationHint = ""; return; }
        if (_translatedLyrics.Lines.Count > 0)
        { _view.TranslationHint = "已载入 " + _view.Settings.TranslationTargetLanguage + " 译文"; return; }
        if (_view.TranslationBusy) { _view.TranslationHint = "正在批量翻译，完成后自动生成当前歌曲译文。"; return; }
        if (_view.TranslationModelBusy) { _view.TranslationHint = "正在下载翻译模型，完成后自动翻译。"; return; }
        if (!HasLyricsFile(track) || !_lyrics.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Text)))
        { _view.TranslationHint = "等待原文 LRC；载入歌词或完成语音识别后自动翻译。"; return; }
        var options = TranslationSettings();
        string key = track.FilePath + "|" + options.TargetLanguage + "|" + options.Model + "|" + options.UseCuda + "|" + options.Python;
        if (_autoTranslationAttempt == key) return;
        _autoTranslationAttempt = key;
        if (!LyricTranslation.IsModelComplete(options.Model))
        { _view.TranslationHint = "缺少翻译模型，请到设置的“歌词翻译与显示”下载或选择模型。"; return; }
        if (File.Exists(LyricTranslation.OutputPath(track.FilePath, options.TargetLanguage)))
        { _view.TranslationHint = "译文文件为空或无法读取，请在“歌词翻译”页检查或重新生成。"; return; }
        var previous = _autoTranslationTask;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _autoTranslationCancellation = cancellation;
        long request = ++_autoTranslationRequest;
        _view.TranslationHint = "正在自动识别原文语言并翻译…";
        _autoTranslationTask = AutoTranslateAsync(track, _lyrics, options, previous, request, cancellation);
    }
    private async Task AutoTranslateAsync(Track track, LrcDocument original, TranslationOptions options, Task? previous, long request, CancellationTokenSource cancellation)
    {
        bool Current() => !_exiting && !_disposed && request == _autoTranslationRequest && !cancellation.IsCancellationRequested && _current?.FilePath == track.FilePath;
        try
        {
            if (previous is not null) await previous;
            cancellation.Token.ThrowIfCancellationRequested();
            var progress = new UiTranslationProgress(Dispatcher, p => { if (Current() && ReferenceEquals(_autoTranslationCancellation, cancellation)) _view.TranslationHint = $"自动翻译 · {p.Percent:F0}% · {p.Message}"; });
            await _lyricTranslator.RunAsync(options, original, track.FilePath, progress, cancellation.Token);
            if (!Current()) return;
            LoadTranslatedLyrics(track); _view.TranslationHint = "自动翻译完成 · 已保存 " + Path.GetFileName(LyricTranslation.OutputPath(track.FilePath, options.TargetLanguage));
            UpdateLyrics();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error(ex, "Automatic lyric translation failed; audio={Audio}", track.FilePath); if (Current()) _view.TranslationHint = "自动翻译未完成：" + ex.Message; }
        finally
        {
            if (ReferenceEquals(_autoTranslationCancellation, cancellation)) _autoTranslationCancellation = null;
            cancellation.Dispose();
        }
    }
    private void LoadTranslatedLyrics(Track track)
    {
        _translatedLyrics = new(Array.Empty<LyricLine>());
        string path = LyricTranslation.OutputPath(track.FilePath, _view.Settings.TranslationTargetLanguage);
        try { if (File.Exists(path)) _translatedLyrics = LrcDocument.Load(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Log.Warning(ex, "Reading translated lyrics failed; path={Path}", path); _view.Status = "读取译文失败：" + ex.Message; }
    }
    private LrcDocument DisplayLyrics()
    {
        var mode = _view.Settings.LyricDisplayMode;
        if (!ReferenceEquals(_composedOriginal, _lyrics) || !ReferenceEquals(_composedTranslation, _translatedLyrics) || _composedMode != mode)
        {
            _displayLyrics = LyricTranslation.Compose(_lyrics, _translatedLyrics, mode);
            _composedOriginal = _lyrics; _composedTranslation = _translatedLyrics; _composedMode = mode;
        }
        return _displayLyrics;
    }
    private void TranslationSettings_Click(object sender, RoutedEventArgs e)
    { SetLyricsDrawer(false); MainTabs.SelectedItem = SettingsTab; SettingsScroll.ScrollToTop(); }
    private void CancelTranslations() { CancelAutoTranslation(); _translationBatchCancellation?.Cancel(); _translationDownloadCancellation?.Cancel(); }
    private async Task StopTranslationsAsync()
    { CancelTranslations(); await Task.WhenAll(new[] { _autoTranslationTask, _translationBatchTask, _translationDownloadTask }.Where(t => t is not null).Cast<Task>()); }
}
