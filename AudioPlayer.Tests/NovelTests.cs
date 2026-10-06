using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AudioPlayer;
using AudioPlayer.Models;
using AudioPlayer.Plugin.Abstractions;
using AudioPlayer.Plugin.Novel;
using AudioPlayer.Services;
using AudioPlayer.ViewModels;

internal static partial class Program
{
    private const string NovelText = """
        第一卷 风起
        第一章 序曲
        古老的城门缓缓打开少年带着满身晨露来到门前。
        第二章 山谷
        山谷里的白色雾气慢慢散去石桥下传来清澈的水声。
        第三章 来信
        一封远方送来的书信揭开了多年以前失踪的秘密。
        第二卷 远行
        第四章 渡河
        老船夫撑起竹篙带着旅人在黄昏时分穿过宽阔河面。
        第五章 失踪
        队伍在黑色森林入口突然失去方向只留下断裂罗盘。
        第六章 重逢
        明亮灯火照着久别重逢的朋友大家终于安心坐了下来。
        """;
    private static (NovelBook Book, PlaybackSnapshot Snapshot) NovelFixture(string root)
    {
        Directory.CreateDirectory(root); string source = Path.Combine(root, "小说.txt"); File.WriteAllText(source, NovelText, new UTF8Encoding(false));
        var book = NovelParser.ParseFile(source, new()); var tracks = new List<PlaylistAudio>();
        var captions = new[]
        {
            "[00:00.00]本期特别赞助欢迎大家关注支持\n[00:00.20]第二集 山谷\n[00:00.40]" + book.Chapters[1].Body + "\n[00:00.90]\n[00:01.00]第三集 来信\n[00:01.30]" + book.Chapters[2].Body + "\n[00:02.70]",
            "[00:00.00]" + book.Chapters[2].Body + "\n[00:01.00]第四集 渡河\n[00:01.40]" + book.Chapters[3].Body + "\n[00:02.60]",
            "[00:00.00]第六集 重逢\n[00:00.50]" + book.Chapters[5].Body + "\n[00:02.50]"
        };
        for (int i = 0; i < captions.Length; i++)
        {
            string path = Path.Combine(root, "音频" + (i + 1) + ".wav"); WriteWave(path); File.WriteAllText(Path.ChangeExtension(path, ".lrc"), captions[i]); tracks.Add(new(Guid.NewGuid(), path, "音频" + (i + 1)));
        }
        return (book, new(Guid.NewGuid(), "小说列表", tracks, tracks[0].Id, tracks[0].FilePath, 0.5, 3, true));
    }
    private static void NovelCore()
    {
        var root = Path.Combine(_root, "novel-core"); var fixture = NovelFixture(root); var book = fixture.Book;
        Assert(book.Chapters.Count == 6 && book.Chapters[3].Volume == "第二卷 远行" && book.Chapters[3].VolumeNumber == "二", "Volume and chapter parsing");
        Assert(NovelParser.ParseNumber("一百零三") == 103 && NovelParser.ParseNumber("两千零一") == 2001 && NovelParser.ParseNumber("Ⅻ") == 12 && NovelParser.ParseNumber("二〇二四") == 2024, "Chinese, Arabic and Roman numerals");
        Assert(NovelParser.ParseNumber("一亿两千万") == 120000000 && NovelParser.ParseNumber("九十九亿") is null, "Chinese large units and overflow");
        var custom = NovelParser.Parse("卷IV 星辰\nChapter I - Begin\nignore this\n正文内容\nChapter II - End\n最后正文", new() { ChapterPattern = @"^Chapter (?<number>{number}) - (?<title>.*)$", NumberFormat = "罗马数字", IgnorePattern = "^ignore" });
        Assert(custom.Chapters.Count == 2 && custom.Chapters[0].VolumeNumber == "IV" && !custom.Chapters[0].Body.Contains("ignore"), "Custom regex and ignored lines");
        var special = NovelParser.Parse("序章\n这是序章内容\n番外一 归来\n番外的内容\n后记\n感谢读者", new());
        Assert(special.Chapters.Count == 3 && new ChapterTextIndex(special).Match("序章")?.Chapter == 0, "Special chapter title anchors");
        NovelParser.Group(book, true, 2); Assert(book.Chapters[0].Group == "第一卷 风起", "Group must use original volume title");
        NovelParser.Group(book, false, 2); Assert(book.Chapters[2].Group == "第 3—4 章", "Fixed-size grouping");
        var unvolumed = NovelParser.Parse("第一章 启程\n一些正文\n第二章 山间\n另一些正文", new()); NovelParser.Group(unvolumed, true, 1);
        Assert(unvolumed.Chapters[1].Group == "第 2—2 章", "Volume fallback");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        foreach (var encoding in new[] { Encoding.GetEncoding(936), Encoding.Unicode, new UTF8Encoding(false) })
        {
            string path = Path.Combine(root, "编码" + encoding.CodePage + ".txt"); File.WriteAllText(path, NovelText, encoding);
            Assert(NovelParser.ParseFile(path, new()).Chapters.Count == 6, "Novel text encoding " + encoding.WebName);
        }
        var index = new ChapterTextIndex(book);
        Assert(index.Match("第二集")?.Chapter == 1 && index.Match(book.Chapters[1].Body)?.Confidence == 1, "Title-only narration and exact body anchors");
        Assert(index.Match("第二集", titleOnly: true)?.Chapter == 1 && index.Match(book.Chapters[1].Body, titleOnly: true) is null, "Opening title-anchor pass is independent from body fallback");
        Assert(index.Match(book.Chapters[1].Body.Replace("白色", "白白"))?.Chapter == 1, "Fuzzy ASR body matching");
        Assert(index.Match("本节目由某品牌特别赞助谢谢收听") is null, "Advertisements must not be forced into a chapter");
        var repeated = NovelParser.Parse("第一卷 起点\n第一章 风声\n漫长的旅途终于走到了尽头故人站在码头静静等候。\n第二卷 回归\n第一章 月色\n孩子们在明亮教室读书老师拿出全新的地图。", new());
        var repeatedIndex = new ChapterTextIndex(repeated);
        Assert(repeatedIndex.Match("第一集") is null && repeatedIndex.Match("第一集 月色")?.Chapter == 1, "Duplicate chapter numbers require evidence");
        var map = AlignmentEngine.Generate(book, fixture.Snapshot);
        Assert(map.Segments.Any(s => s.ChapterId == book.Chapters[1].Id) && map.Segments.All(s => s.ChapterId != book.Chapters[0].Id), "First available audio can start in the middle of a novel");
        Assert(map.Segments.Where(s => s.ChapterId == book.Chapters[2].Id).Select(s => s.TrackId).Distinct().Count() == 2, "Chapter spanning multiple audio files");
        Assert(map.Gaps.Any(g => g.FirstOrder == 4 && g.LastOrder == 4 && g.Status == "可能缺失"), "Infer missing chapter without inventing deleted file names");
        var lookup = new MapLookup(map);
        Assert(lookup.Locate(fixture.Snapshot.CurrentFilePath, 0.1) is null && lookup.Locate(fixture.Snapshot.CurrentFilePath, 1.5)?.ChapterId == book.Chapters[2].Id, "Unknown introduction and forward seek");
        Assert(lookup.Locate(fixture.Snapshot.CurrentFilePath, 0.5)?.ChapterId == book.Chapters[1].Id && lookup.Locate(fixture.Snapshot.CurrentFilePath, 0.95) is null, "Backward seek and explicit silence");
        var removed = fixture.Snapshot with { Tracks = fixture.Snapshot.Tracks.Skip(1).ToArray() };
        Assert(AlignmentEngine.Reconcile(map, removed) && map.Segments.Where(s => s.TrackId == fixture.Snapshot.Tracks[0].Id).All(s => s.Missing), "Historical cache must mark playlist removals even if files still exist");
        Assert(map.Segments.Any(s => s.ChapterId == book.Chapters[2].Id && !s.Missing), "Partially missing multi-file chapter remains playable");
        var store = new NovelStore(Path.Combine(root, "data"), root); string cache = store.SaveCache(map, "");
        var loaded = store.LoadCache(cache, map.PlaylistId); Assert(loaded.Audio.Count == 3 && loaded.Segments.Count == map.Segments.Count, "Readable cache round trip");
        Assert(!File.ReadAllText(store.Resolve(cache)).Contains(root.Replace("\\", "\\\\")), "Internal paths must be stored relative for portability");
        map.Segments[0].Evidence = "用户手动校对"; store.SaveMap(cache, map);
        Assert(store.LoadCache(cache, map.PlaylistId, true).Segments[0].Evidence != "用户手动校对", "Previous mapping is retained for rollback");
        var invalid = NovelStore.Clone(map); invalid.Segments[0].End = invalid.Segments[0].Start;
        try { store.SaveMap(cache, invalid); throw new Exception("Expected validation failure"); } catch (InvalidDataException) { }
        Assert(store.LoadCache(cache, map.PlaylistId).Segments[0].End > 0, "Invalid edits must not replace good cache");
        var tracking = new TrackingState(); tracking.Reset("A");
        Assert(!tracking.Update(true, "A") && tracking.Update(true, "B") && !tracking.Update(false, "C"), "Cache-only tracking and no repeated same-chapter location");
        tracking.Reset("A"); Assert(tracking.Update(true, "B"), "AutoLocate off must not disable cache tracking");
        var unmatched = fixture.Snapshot with { Tracks = new[] { fixture.Snapshot.Tracks[0] } };
        File.WriteAllText(Path.ChangeExtension(unmatched.Tracks[0].FilePath, ".lrc"), "[00:00.00]没有任何章节内容这里都是背景广告\n[00:02.00]");
        try { AlignmentEngine.Generate(book, unmatched); throw new Exception("Expected missing start anchor"); } catch (StartAnchorException) { }
        Assert(AlignmentEngine.Generate(book, unmatched, manualStart: 2).Segments[0].ChapterId == book.Chapters[2].Id, "Exceptional manual start");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { AlignmentEngine.Generate(book, fixture.Snapshot, token: cancelled.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        var large = new NovelBook { Chapters = Enumerable.Range(0, 3000).Select(i => new Chapter { Order = i, Number = (i + 1).ToString(), Title = $"第{i + 1}章", Body = "独特线索" + Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(i))) + "在此章节中展开。" }).ToList() };
        var watch = Stopwatch.StartNew(); var largeIndex = new ChapterTextIndex(large);
        Assert(largeIndex.Match(large.Chapters[2789].Body)?.Chapter == 2789 && watch.Elapsed < TimeSpan.FromSeconds(15), "Thousands of chapter index and query performance");
        Console.WriteLine($"Novel 3000-chapter index/query: {watch.ElapsedMilliseconds} ms");
        store.ClearCache(cache, map.PlaylistId); Assert(!File.Exists(store.Resolve(cache)) && !File.Exists(store.Resolve(cache) + ".bak"), "Clear only this cache and backup");
    }

    private sealed class FakeNovelHost(PlaybackSnapshot snapshot) : IPlaybackHost
    {
        public PlaybackSnapshot Snapshot = snapshot;
        public (Guid Track, double Seconds)? Played;
        public PlaybackSnapshot GetSnapshot() => Snapshot;
        public Task PlayAsync(Guid playlistId, Guid trackId, double seconds, CancellationToken token) { token.ThrowIfCancellationRequested(); Played = (trackId, seconds); return Task.CompletedTask; }
    }
    private static void NovelUi()
    {
        if (Application.Current is null) { var app = new App(false); app.InitializeComponent(); }
        string root = Path.Combine(_root, "novel-ui"); var fixture = NovelFixture(root); var host = new FakeNovelHost(fixture.Snapshot);
        var context = new PluginContext(root, Path.GetFullPath("Plugins/Novel"), Path.Combine(root, "data"), () => "python", (_, _) => { }, CancellationToken.None) { Playback = host };
        using var page = new NovelPage(context);
        var window = new MainWindow(new StateStore(Path.Combine(root, "host-state")), false) { Width = 1000, Height = 760, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        var mainTabs = (TabControl)window.FindName("MainTabs"); mainTabs.Items.Add(new TabItem { Header = "小说章节", Content = page }); mainTabs.SelectedIndex = mainTabs.Items.Count - 1;
        window.Show(); Pump(() => !Private<bool>(page, "_busy"));
        void Await(Task task) { Pump(() => task.IsCompleted, 15000); task.GetAwaiter().GetResult(); }
        void Click(string method) { bool dispatched = false; window.Dispatcher.BeginInvoke(new Action(() => { Invoke(page, method, page, new RoutedEventArgs()); dispatched = true; })); Pump(() => dispatched && !Private<bool>(page, "_busy"), 15000); }
        try
        {
            Private<NovelSettings>(page, "_settings").AutoLocate = false;
            Await(window.Dispatcher.InvokeAsync(() => (Task)InvokeResult(page, "SelectBookAsync", fixture.Book.SourcePath, CancellationToken.None)).Task.Unwrap());
            Assert(Private<NovelMap?>(page, "_map") is null, "AutoLocate off must not compute mappings after parsing");
            Click("Generate_Click"); Assert(Private<bool>(page, "_hasCache"), "Generate must persist and enable tracking");
            Assert(Private<NovelBook>(page, "_book").Chapters.All(c => !c.IsCurrent), "Generating cache must not force initial location with AutoLocate off");
            host.Snapshot = host.Snapshot with { PositionSeconds = 1.5 }; Delay(450);
            Assert(Private<NovelBook>(page, "_book").Chapters[2].IsCurrent, "Cache follows chapter boundary even with AutoLocate off");
            Click("PlayChapter_Click"); Assert(host.Played?.Track == fixture.Snapshot.Tracks[0].Id && Math.Abs(host.Played.Value.Seconds - 1) < 0.01, "Chapter click plays earliest available segment");
            var edits = Private<System.Collections.ObjectModel.ObservableCollection<ChapterSegment>>(page, "_segmentEdits");
            string correctedChapter = Private<Chapter>(page, "_selected").Id;
            double originalStart = edits[0].Start; edits[0].Start += 0.05;
            Click("SaveSegments_Click"); Assert(Math.Abs(Private<NovelMap>(page, "_map").Segments.First(s => s.ChapterId == correctedChapter).Start - originalStart - 0.05) < 0.001, "Manual segment corrections must be applied and cached");
            Click("Rollback_Click"); Assert(Math.Abs(Private<NovelMap>(page, "_map").Segments.First(s => s.ChapterId == correctedChapter).Start - originalStart) < 0.001, "Rollback must restore the previous segment times");
            var preview = Private<NovelBook>(page, "_preview"); preview.Chapters[0].Volume = "校对后的第一卷";
            Click("ApplyPreview_Click"); Assert(!Private<bool>(page, "_hasCache") && Private<NovelBook>(page, "_book").Chapters[0].Group == "校对后的第一卷", "Corrected volume must update grouping and invalidate mapping");
            Assert(Private<Chapter?>(page, "_selected") is null && ((DataGrid)page.FindName("SegmentsGrid")).Items.Count == 0, "Invalidated mapping must not leave stale chapter playback details");
            var grid = (DataGrid)page.FindName("PreviewGrid"); grid.SelectedIndex = 1;
            ((TextBox)page.FindName("BodyEditor")).CaretIndex = 10;
            Click("Split_Click"); Assert(Private<NovelBook>(page, "_preview").Chapters.Count == 7, "Split preview at caret");
            Click("UndoPreview_Click"); Assert(Private<NovelBook>(page, "_preview").Chapters.Count == 6, "Undo unapplied split");
            Click("Locate_Click"); Assert(!Private<bool>(page, "_hasCache"), "Manual temporary location does not enable tracking");
            string? before = Private<NovelBook>(page, "_book").Chapters.FirstOrDefault(c => c.IsCurrent)?.Id;
            host.Snapshot = host.Snapshot with { CurrentTrackId = fixture.Snapshot.Tracks[2].Id, CurrentFilePath = fixture.Snapshot.Tracks[2].FilePath, PositionSeconds = 0.6 }; Delay(450);
            Assert(Private<NovelBook>(page, "_book").Chapters.FirstOrDefault(c => c.IsCurrent)?.Id == before, "No cache must not auto-track across audio files");
            host.Snapshot = host.Snapshot with { PlaylistId = Guid.NewGuid(), PlaylistName = "未绑定列表" }; Delay(450);
            Assert(Private<NovelBook?>(page, "_book") is null, "Unbound playlist must not reuse another novel");
            host.Snapshot = fixture.Snapshot; Delay(600); Pump(() => !Private<bool>(page, "_busy"));
            Assert(Private<NovelBook>(page, "_book").Chapters[0].Volume == "校对后的第一卷", "Playlist binding reloads persisted corrections");
            Private<NovelSettings>(page, "_settings").AutoLocate = true;
            Await(window.Dispatcher.InvokeAsync(() => (Task)InvokeResult(page, "LoadBoundAsync", CancellationToken.None)).Task.Unwrap());
            Assert(!Private<bool>(page, "_hasCache") && Private<NovelBook>(page, "_book").Chapters[1].IsCurrent, "AutoLocate on without cache locates once after parse");
            Click("Generate_Click");
            Await(window.Dispatcher.InvokeAsync(() => (Task)InvokeResult(page, "LoadBoundAsync", CancellationToken.None)).Task.Unwrap());
            Assert(Private<bool>(page, "_hasCache") && Private<NovelBook>(page, "_book").Chapters[1].IsCurrent, "AutoLocate on with cache locates after parse");
            ((TabControl)page.FindName("Tabs")).SelectedIndex = 0;
            var book = Private<NovelBook>(page, "_book"); Invoke(page, "Highlight", book.Chapters[1]);
            window.UpdateLayout(); RenderElement((FrameworkElement)window.Content, (int)((FrameworkElement)window.Content).ActualWidth, (int)((FrameworkElement)window.Content).ActualHeight, Path.Combine(_root, "novel-plugin.png"));
            for (int i = 1; i < 4; i++)
            {
                ((TabControl)page.FindName("Tabs")).SelectedIndex = i; window.UpdateLayout();
                RenderElement((FrameworkElement)window.Content, (int)((FrameworkElement)window.Content).ActualWidth, (int)((FrameworkElement)window.Content).ActualHeight, Path.Combine(_root, "novel-tab-" + i + ".png"));
            }
            Await(page.StopAsync());
        }
        finally { window.Close(); }
        string plugins = Path.Combine(root, "dynamic-plugins"), novel = Path.Combine(plugins, "novel"); Directory.CreateDirectory(novel);
        foreach (string file in Directory.GetFiles("Plugins/Novel/bin/Release/net10.0-windows")) File.Copy(file, Path.Combine(novel, Path.GetFileName(file)));
        using var disabled = new PluginManager(); disabled.Start(plugins, Path.Combine(root, "disabled-data"), new Dictionary<string, bool>(), () => "python", () => { }, host);
        Assert(disabled.Entries.Single().Page is null, "Novel plugin defaults off");
        using var enabled = new PluginManager(); enabled.Start(plugins, Path.Combine(root, "dynamic-data"), new Dictionary<string, bool> { ["novel"] = true }, () => "python", () => { }, host);
        var entry = enabled.Entries.Single(); Assert(entry.Page is not null, "Dynamic XAML plugin load: " + entry.Status);
        var stop = enabled.StopAsync(); Pump(() => stop.IsCompleted); stop.GetAwaiter().GetResult();
    }
    private static object InvokeResult(object instance, string method, params object[] args) => instance.GetType().GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(instance, args)!;
    private static void NovelHost()
    {
        if (Application.Current is null) { var app = new App(false); app.InitializeComponent(); }
        var fixture = NovelFixture(Path.Combine(_root, "novel-host")); var store = new StateStore(Path.Combine(_root, "novel-host-state"));
        var window = new MainWindow(store, false) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        window.Show();
        try
        {
            var view = (PlayerViewModel)window.DataContext; view.Settings.Volume = 0;
            var playlist = view.SelectedPlaylist; foreach (var t in fixture.Snapshot.Tracks) playlist.Tracks.Add(new Track { Id = t.Id, FilePath = t.FilePath });
            var host = new MainWindow.PluginPlaybackHost(window); var snapshot = host.GetSnapshot(); Assert(snapshot.Tracks.Select(t => t.Id).SequenceEqual(fixture.Snapshot.Tracks.Select(t => t.Id)), "Host exposes immutable ordered playlist");
            var play = window.Dispatcher.InvokeAsync(() => host.PlayAsync(playlist.Id, playlist.Tracks[1].Id, 1.2, CancellationToken.None)).Task.Unwrap(); Pump(() => play.IsCompleted); play.GetAwaiter().GetResult();
            Assert(host.GetSnapshot().CurrentTrackId == playlist.Tracks[1].Id && host.GetSnapshot().IsPlaying && host.GetSnapshot().PositionSeconds >= 1.1, "Host switch and seek must await MediaOpened");
            double position = host.GetSnapshot().PositionSeconds; Delay(250);
            Assert(host.GetSnapshot().PositionSeconds > position + 0.1, "Chapter playback must actually advance after the initial seek");
            Private<AudioService>(window, "_audio").Toggle();
            play = window.Dispatcher.InvokeAsync(() => host.PlayAsync(playlist.Id, playlist.Tracks[1].Id, 0.4, CancellationToken.None)).Task.Unwrap(); Pump(() => play.IsCompleted); play.GetAwaiter().GetResult();
            Assert(host.GetSnapshot().IsPlaying && host.GetSnapshot().PositionSeconds < 1, "Same-track chapter jump resumes paused playback");
            play = window.Dispatcher.InvokeAsync(() =>
            {
                var pending = host.PlayAsync(playlist.Id, playlist.Tracks[0].Id, 1.2, CancellationToken.None);
                Private<AudioService>(window, "_audio").Toggle("pause-before-chapter-opened");
                return pending;
            }).Task.Unwrap();
            Pump(() => play.IsCompleted); play.GetAwaiter().GetResult();
            Assert(!host.GetSnapshot().IsPlaying && host.GetSnapshot().PositionSeconds >= 1.1, "A user pause during chapter opening must override the original play request");
            var missing = host.PlayAsync(playlist.Id, Guid.NewGuid(), 0, CancellationToken.None);
            try { missing.GetAwaiter().GetResult(); throw new Exception("Expected missing track rejection"); } catch (InvalidOperationException) { }
        }
        finally { window.Close(); }
    }
}
