using System.Windows;
using AudioPlayer.Models;
using AudioPlayer.Services;
using Microsoft.Win32;

namespace AudioPlayer;

public partial class MainWindow
{
    public void AddTranslationTargets(IEnumerable<Track> tracks)
    {
        if (_view.TranslationBusy) return;
        var known = _view.TranslationQueue.Select(i => i.Track.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        { if (known.Add(Path.GetFullPath(track.FilePath))) _view.TranslationQueue.Add(new(track)); }
        _view.TranslationStatus = $"队列中 {_view.TranslationQueue.Count} 项 · 原文语言自动识别，目标语言在设置中更改。";
    }
    private void ChooseTranslation_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "添加需要翻译的音频或 LRC", Filter = "音频或歌词|*.lrc;*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac;*.aiff;*.aif|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) AddTranslationTargets(dialog.FileNames.Select(path => new Track { FilePath = path, LyricsPath = Path.GetExtension(path).Equals(".lrc", StringComparison.OrdinalIgnoreCase) ? path : null }));
    }
    private void UsePlaylistTranslation_Click(object sender, RoutedEventArgs e) => AddTranslationTargets(_view.SelectedPlaylist.Tracks);
    private void UseSelectedTranslation_Click(object sender, RoutedEventArgs e)
    { if ((_view.SelectedTrack ?? _current) is { } track) AddTranslationTargets(new[] { track }); }
    private void RemoveTranslation_Click(object sender, RoutedEventArgs e)
    { if (!_view.TranslationBusy) foreach (var item in TranslationList.SelectedItems.Cast<TranslationItem>().ToArray()) _view.TranslationQueue.Remove(item); }
    private void ClearTranslation_Click(object sender, RoutedEventArgs e) { if (!_view.TranslationBusy) _view.TranslationQueue.Clear(); }
    private async void StartTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (_view.TranslationBusy) return;
        if (_view.TranslationQueue.Count == 0) { _view.TranslationStatus = "请先添加音频或 LRC。"; return; }
        await (_translationBatchTask = TranslateBatchAsync());
    }
    private async Task TranslateBatchAsync()
    {
        var options = TranslationSettings(); var items = _view.TranslationQueue.ToArray(); bool overwrite = _view.OverwriteTranslation;
        _view.TranslationBusy = true; _view.TranslationPercent = 0; CancelAutoTranslation();
        foreach (var item in items) { item.State = RecognitionState.Pending; item.Percent = 0; item.Status = "等待翻译"; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _translationBatchCancellation = cancellation;
        int completed = 0, failed = 0, skipped = 0;
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (_autoTranslationTask is not null) await _autoTranslationTask;
            for (int index = 0; index < items.Length; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested(); var item = items[index];
                try
                {
                    string source = OriginalLyricsPath(item.Track), output = LyricTranslation.OutputPath(item.Track.FilePath, options.TargetLanguage);
                    if (!outputs.Add(output) || (!overwrite && File.Exists(output))) { item.State = RecognitionState.Skipped; item.Status = "已跳过：译文已存在或输出路径重复"; skipped++; }
                    else
                    {
                        if (Path.GetFullPath(source).Equals(output, StringComparison.OrdinalIgnoreCase)) throw new IOException("原文与译文路径相同，无法覆盖翻译来源。");
                        if (!File.Exists(source)) throw new IOException("缺少原文 LRC，请先完成语音识别或载入歌词。");
                        item.State = RecognitionState.Running; item.Status = "正在识别语言并翻译…";
                        var progress = new UiTranslationProgress(Dispatcher, p =>
                        {
                            if (item.State != RecognitionState.Running || cancellation.IsCancellationRequested) return;
                            item.Percent = p.Percent; item.Status = p.Message; _view.TranslationPercent = items.Average(i => i.Percent);
                        });
                        await _lyricTranslator.RunAsync(options, LrcDocument.Load(source), item.Track.FilePath, progress, cancellation.Token, overwrite);
                        item.State = RecognitionState.Completed; item.Status = "已保存 " + Path.GetFileName(output); completed++;
                        if (_current is { } track && output.Equals(LyricTranslation.OutputPath(track.FilePath, _view.Settings.TranslationTargetLanguage), StringComparison.OrdinalIgnoreCase))
                        { LoadTranslatedLyrics(track); UpdateLyrics(); }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { item.State = RecognitionState.Failed; item.Status = ex.Message; failed++; Log.Warning(ex, "Batch lyric translation item failed; path={Path}", item.Track.FilePath); }
                item.Percent = 100; _view.TranslationPercent = items.Average(i => i.Percent);
                _view.TranslationStatus = $"已处理 {index + 1}/{items.Length} · 成功 {completed}，跳过 {skipped}，失败 {failed}";
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var item in items.Where(i => i.State is RecognitionState.Pending or RecognitionState.Running)) { item.State = RecognitionState.Cancelled; item.Status = "已取消"; }
            _view.TranslationStatus = $"已取消，保留 {completed} 个已完成的译文。";
        }
        finally { _translationBatchCancellation = null; _view.TranslationBusy = false; EnsureAutoTranslation(); }
    }
    private void CancelTranslation_Click(object sender, RoutedEventArgs e) => _translationBatchCancellation?.Cancel();
}
