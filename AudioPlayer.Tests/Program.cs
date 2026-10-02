using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Input;
using Forms = System.Windows.Forms;
using AudioPlayer;
using AudioPlayer.Models;
using AudioPlayer.Services;
using AudioPlayer.ViewModels;
using AudioPlayer.Views;

internal static class Program
{
    private static int _passed;
    private static string _root = "";
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--instance-client")
        {
            using var instance = new SingleInstanceService(args[1]);
            return instance.IsPrimary ? 3 : instance.ActivateExistingAsync(args.Skip(2).ToArray()).GetAwaiter().GetResult() ? 0 : 4;
        }
        _root = Path.GetFullPath(Path.Combine("artifacts", "tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_root);
        try
        {
            Test("LRC: offsets, repeated timestamps, precision, seek backwards", Lyrics);
            Test("LRC: UTF-8 and GBK Chinese input", Encodings);
            Test("Queue: sequential/reverse boundaries, repeat and single modes", OrderedQueue);
            Test("Queue: shuffle covers every track, previous follows history", ShuffleQueue);
            Test("State: persistence, backup recovery, invalid values", State);
            Test("Portable state: internal paths survive directory relocation", PortableState);
            Test("Models: only complete model folders become selections", Models);
            Test("Covers: embedded artwork, sidecar fallback and unlocked files", Covers);
            if (args.Contains("--render"))
            {
                Test("WPF: official Fluent templates, all tabs and lyric rendering", Render);
                Test("Models: refresh after manual additions, nested snapshots, selection preservation", ModelRefresh);
                Test("Lyrics: full document, wheel browsing, timed resume, backward seek and song reset", SynchronizedLyrics);
            }
            if (args.Contains("--media")) Test("Audio: WAV opens, plays, pauses, seeks and ends", Media);
            int pythonIndex = Array.IndexOf(args, "--python");
            if (pythonIndex >= 0 && pythonIndex + 1 < args.Length)
            {
                Test("Recognition: real subprocess, UTF-8 paths, failure/cancellation keep old LRC", () => ProcessTests(args[pythonIndex + 1]));
                Test("Batch: automatic sibling LRC, skips, conflicts, failures and cancellation", () => BatchProcessTests(args[pythonIndex + 1]));
                Test("Live recognition: partial captions, track changes and existing LRC protection", () => LiveRecognition(args[pythonIndex + 1]));
            }
            int modelIndex = Array.IndexOf(args, "--offline-model");
            int speechIndex = Array.IndexOf(args, "--speech");
            if (modelIndex >= 0 && speechIndex >= 0 && pythonIndex >= 0)
                Test("Recognition: .NET to real offline Whisper model to LRC", () => OfflineModel(args[pythonIndex + 1], args[modelIndex + 1], args[speechIndex + 1]));
            if (pythonIndex >= 0) Test("Environment: local CPU, driver and library inspection with result dialog", () => EnvironmentCheck(args[pythonIndex + 1]));
            if (pythonIndex >= 0 && args.Contains("--online-models")) Test("Online models: live catalog, real verified download, cancellation", () => OnlineModelsTest(args[pythonIndex + 1]));
            if (args.Contains("--native")) Test("Native: tray close keeps playback, lyric lock and tray exit", NativeLifecycle);
            Console.WriteLine($"PASS: {_passed} test groups. Artifacts: {_root}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Test(string name, Action action) { action(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Lyrics()
    {
        var doc = LrcDocument.Parse("[ti:测试]\n[offset:-100]\n[00:02.125][00:04.50]你好\n[00:01.2]开头\n[00:03.00]\ninvalid\n[00:99.0]invalid");
        Assert(doc.Lines.Count == 4, "Multiple timestamps or blank lines were lost");
        Assert(doc.FindLine(TimeSpan.Zero) == -1, "Before first lyric");
        Assert(doc.FindLine(TimeSpan.FromSeconds(2.025)) == 1, "Millisecond precision");
        Assert(doc.FindLine(TimeSpan.FromSeconds(3)) == 2 && doc.Lines[2].Text == "", "Silence line");
        Assert(doc.FindLine(TimeSpan.FromSeconds(8)) == 3, "Last lyric");
        Assert(doc.FindLine(TimeSpan.FromSeconds(1.15)) == 0, "Seek backwards");
        Assert(LrcDocument.Parse("plain text").FindLine(TimeSpan.MaxValue) == -1, "Empty document");
    }
    private static void Encodings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        foreach (Encoding encoding in new[] { new UTF8Encoding(false), Encoding.GetEncoding(936) })
        {
            string path = Path.Combine(_root, encoding.CodePage + ".lrc");
            File.WriteAllText(path, "[00:01.00]歌词测试", encoding);
            Assert(LrcDocument.Load(path).Lines[0].Text == "歌词测试", "Incorrect lyric encoding");
        }
    }
    private static Track[] Tracks(int count) => Enumerable.Range(0, count).Select(i => new Track { FilePath = $"C:/music/{i}.wav" }).ToArray();
    private static void OrderedQueue()
    {
        var q = new PlaybackQueue(); var t = Tracks(3);
        Assert(q.Next(t, null, PlayMode.Sequential, false, false, false) == t[0], "Start forward");
        Assert(q.Next(t, null, PlayMode.Reverse, false, false, false) == t[2], "Start reverse");
        Assert(q.Next(t, t[0], PlayMode.Reverse, false, true, false) is null, "Reverse end");
        Assert(q.Next(t, t[0], PlayMode.Reverse, false, true, true) == t[2], "Reverse repeat");
        Assert(q.Next(t, t[2], PlayMode.Sequential, false, true, false) is null, "Sequential end");
        Assert(q.Next(t, t[2], PlayMode.Sequential, false, false, false) == t[0], "Manual next wraps");
        Assert(q.Next(t, t[1], PlayMode.RepeatOne, false, true, false) == t[1], "Repeat one");
        Assert(q.Next(t, t[1], PlayMode.RepeatOne, false, false, false) == t[2], "Manual skip repeat one");
        Assert(q.Next(t, t[1], PlayMode.Single, false, true, true) is null, "Single must stop");
        Assert(q.Next(Array.Empty<Track>(), null, PlayMode.Shuffle, false, false, true) is null, "Empty queue");
    }
    private static void ShuffleQueue()
    {
        var q = new PlaybackQueue(new Random(42)); var tracks = Tracks(20); Track? current = tracks[0];
        var visited = new List<Track> { current };
        for (int i = 1; i < tracks.Length; i++)
        {
            current = q.Next(tracks, current, PlayMode.Shuffle, false, true, false);
            Assert(current is not null, "Shuffle ended too early"); visited.Add(current!);
        }
        Assert(visited.Distinct().Count() == tracks.Length, "Shuffle repeated before every track");
        Assert(q.Next(tracks, current, PlayMode.Shuffle, false, true, false) is null, "Shuffle must stop after a cycle");
        Assert(q.Next(tracks, current, PlayMode.Shuffle, true, false, false) == visited[^2], "Previous history");
        Assert(q.Next(tracks, visited[^2], PlayMode.Shuffle, false, false, false) == current, "Forward history");
        Assert(q.Next(tracks, current, PlayMode.Shuffle, false, true, true) != current, "Boundary immediate repeat");
        q.Reset();
        var single = tracks.Take(1).ToArray();
        Assert(q.Next(single, single[0], PlayMode.Shuffle, false, true, false) is null, "Single shuffle stop");
        Assert(q.Next(single, single[0], PlayMode.Shuffle, false, true, true) == single[0], "Single shuffle repeat");
    }
    private static void State()
    {
        var store = new StateStore(Path.Combine(_root, "state")); var state = store.Load();
        Assert(state.Playlists.Count == 1 && state.Settings.CloseToTray, "Defaults");
        state.Playlists[0].Tracks.Add(new Track { FilePath = "C:/音乐/歌曲.mp3", LyricsPath = "C:/音乐/歌词.lrc" });
        state.Settings.FontSize = 48; state.Settings.VerticalLyrics = true; store.Save(state);
        state.Settings.FontSize = 52; store.Save(state);
        var loaded = store.Load();
        Assert(loaded.Settings.FontSize == 52 && loaded.Settings.VerticalLyrics && loaded.Playlists[0].Tracks[0].LyricsPath == Path.GetFullPath("C:/音乐/歌词.lrc"), "Round trip and vertical lyric persistence");
        File.WriteAllText(Path.Combine(store.DirectoryPath, "state.json"), "broken");
        Assert(store.Load().Settings.FontSize == 48 && store.LoadWarning is not null, "Backup recovery");
        File.WriteAllText(Path.Combine(store.DirectoryPath, "state.json"), "{\"Settings\":{\"FontSize\":-1,\"Volume\":8,\"Mode\":100},\"Playlists\":[null]}");
        loaded = store.Load();
        Assert(loaded.Settings.FontSize == 16 && loaded.Settings.Volume == 1 && loaded.Playlists.Count == 1 && loaded.Settings.Mode == PlayMode.Sequential, "Normalize invalid values");
    }
    private static void Render()
    {
        var app = new App(startMainWindow: false); app.InitializeComponent();
        var window = new MainWindow(new StateStore(Path.Combine(_root, "ui")), false);
        var content = (FrameworkElement)window.Content;
        var tabs = (TabControl)window.FindName("MainTabs");
        Assert(tabs.TabStripPlacement == Dock.Left && ((Border)window.FindName("LyricsDrawer")).Visibility == Visibility.Collapsed, "Home must show playlists with left navigation and no open lyric panel");
        window.AddRecognitionTargets(new[] { Path.Combine(_root, "第一首.wav"), Path.Combine(_root, "第二首.wav"), Path.Combine(_root, "第一首.wav") });
        Assert(((PlayerViewModel)window.DataContext).RecognitionQueue.Count == 2, "Batch queue must deduplicate selections");
        for (int i = 0; i < tabs.Items.Count; i++)
        {
            tabs.SelectedIndex = i;
            RenderElement(content, 1184, 780, Path.Combine(_root, $"tab-{i}.png"));
        }
        tabs.SelectedIndex = 1;
        RenderElement(content, 984, 720, Path.Combine(_root, "batch-minimum.png"));
        var startBatch = (Button)window.FindName("StartBatchButton");
        var batchBounds = startBatch.TransformToAncestor(content).TransformBounds(new Rect(startBatch.RenderSize));
        Assert(batchBounds.Bottom < 565 && ((ListBox)window.FindName("RecognitionList")).ActualHeight > 40, "Batch controls must remain visible at minimum size");
        tabs.SelectedIndex = 2;
        ((ScrollViewer)window.FindName("SettingsScroll")).ScrollToEnd();
        RenderElement(content, 1184, 780, Path.Combine(_root, "settings-engine.png"));
        tabs.SelectedIndex = 0;
        RenderElement(content, 984, 720, Path.Combine(_root, "minimum-window.png"));
        var model = (PlayerViewModel)window.DataContext;
        var volume = (Slider)window.FindName("VolumeSlider");
        volume.Value = 0.65;
        Slider.IncreaseLarge.Execute(null, volume);
        Assert(Math.Abs(volume.Value - 0.70) < 0.001, "Volume must not jump to maximum on a large step");
        Slider.DecreaseLarge.Execute(null, volume);
        Assert(Math.Abs(volume.Value - 0.65) < 0.001 && volume.IsMoveToPointEnabled, "Volume track click must use the clicked location, keyboard steps remain small");
        model.SelectedPlaylist.Tracks.Add(new Track { FilePath = Path.Combine(_root, "中文音频 样例.wav") });
        model.RefreshCount();
        RenderElement(content, 1184, 780, Path.Combine(_root, "playlist.png"));
        model.Cover = new CoverArtService().Load(Path.Combine(_root, "covers", "embedded.wav")).Cover;
        model.IsPlaying = true;
        RenderElement(content, 1184, 780, Path.Combine(_root, "cover-and-pause.png"));
        Private<DispatcherTimer>(window, "_clock").Stop();
        model.LyricLines = SampleLyrics();
        model.CurrentLyricIndex = 12;
        model.CurrentTitle = "风经过这座小岛";
        model.CurrentInfo = "声屿 · 歌词布局演示";
        model.Cover = null;
        window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000; window.Top = -20000;
        window.Show();
        var coverButton = (Button)window.FindName("CoverButton");
        var drawer = (Border)window.FindName("LyricsDrawer");
        coverButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var fullLyrics = (SynchronizedLyricsView)window.FindName("FullLyrics");
        fullLyrics.ResumeFollowing();
        Delay(450);
        RenderElement(content, (int)(content.ActualWidth + content.Margin.Left + content.Margin.Right), (int)(content.ActualHeight + content.Margin.Top + content.Margin.Bottom), Path.Combine(_root, "full-lyrics.png"));
        Assert(drawer.Visibility == Visibility.Visible && !tabs.IsEnabled && Math.Abs(((TranslateTransform)drawer.RenderTransform).Y) < 1, "Cover click must open the drawer, covering navigation without disturbing the playback bar");
        window.Width = 1000; window.Height = 760;
        Delay(450);
        RenderElement(content, (int)(content.ActualWidth + content.Margin.Left + content.Margin.Right), (int)(content.ActualHeight + content.Margin.Top + content.Margin.Bottom), Path.Combine(_root, "full-lyrics-minimum.png"));
        Assert(content.ActualWidth < 1000 && ((FrameworkElement)window.FindName("ListeningCard")).ActualWidth > 600, "Minimum window must still reserve most width for cover and lyrics");
        ((Button)window.FindName("CloseDrawerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Delay(350);
        Assert(drawer.Visibility == Visibility.Collapsed && tabs.IsEnabled && model.IsPlaying, "Closing drawer must restore navigation and retain playback state");
        for (int i = 0; i < 3; i++) coverButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Delay(350);
        Assert(drawer.Visibility == Visibility.Visible && Math.Abs(((TranslateTransform)drawer.RenderTransform).Y) < 1, "Rapid drawer toggles must finish in the latest requested state");
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(escape); Delay(350);
        Assert(escape.Handled && drawer.Visibility == Visibility.Collapsed && tabs.IsEnabled, "Escape must close the drawer");
        model.Settings.FontSize = 48;
        var lyrics = new LyricsWindow(model.Settings);
        lyrics.SetText("愿每一个声音，都有处停留");
        RenderElement((FrameworkElement)lyrics.Content, 920, 180, Path.Combine(_root, "lyrics.png"));
        model.Settings.VerticalLyrics = true;
        lyrics.ApplySettings();
        const string verticalText = "风🌊经过这座小岛让每一个声音都有处停留";
        lyrics.SetText(verticalText);
        var columns = (StackPanel)lyrics.FindName("VerticalColumns");
        Assert(columns.Children.Count > 1 && string.Concat(columns.Children.Cast<TextBlock>().Select(t => t.Text.Replace("\n", ""))) == verticalText, "Vertical lyrics must wrap columns without splitting or losing Unicode text");
        Assert(lyrics.Height > lyrics.Width && ((FrameworkElement)lyrics.FindName("LyricText")).Visibility == Visibility.Collapsed, "Vertical lyrics must use a portrait overlay");
        RenderElement((FrameworkElement)lyrics.Content, (int)lyrics.Width, (int)lyrics.Height, Path.Combine(_root, "lyrics-vertical.png"));
        model.Settings.VerticalLyrics = false; lyrics.ApplySettings();
        Assert(lyrics.Width > lyrics.Height && ((FrameworkElement)lyrics.FindName("VerticalView")).Visibility == Visibility.Collapsed, "Switching back must restore horizontal lyrics");
        var manager = new ModelManagerWindow(model.Settings, refreshOnLoad: false);
        RenderElement((FrameworkElement)manager.Content, 884, 640, Path.Combine(_root, "online-models.png"));
        Assert(((ListView)manager.FindName("OnlineModels")).Items.Count == 8, "Online catalog must only contain the eight multilingual models");
        manager.Close(); lyrics.Close(); window.Close();
    }

    private static IReadOnlyList<LyricLine> SampleLyrics() => Enumerable.Range(0, 48)
        .Select(i => new LyricLine(TimeSpan.FromSeconds(i * 5), new[] { "风经过这座小岛", "带来远处潮汐的声音", "把日常轻轻放下", "让旋律陪我们走一段", "每一朵浪花都记得", "那首还没有唱完的歌" }[i % 6])).ToArray();

    private static void Delay(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        Pump(() => watch.ElapsedMilliseconds >= milliseconds, milliseconds + 1000);
    }

    private static void SynchronizedLyrics()
    {
        var view = new SynchronizedLyricsView { Lines = SampleLyrics(), CurrentIndex = 12 };
        var host = new Window { Content = view, Width = 440, Height = 520, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        host.Show();
        var scroll = (ScrollViewer)view.FindName("LyricsScroll");
        var items = (ItemsControl)view.FindName("LyricItems");
        double CenterError(int index)
        {
            var row = (FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(index);
            return Math.Abs(row.TransformToAncestor((FrameworkElement)view.FindName("ScrollContent")).Transform(new Point()).Y + row.ActualHeight / 2 - scroll.VerticalOffset - scroll.ViewportHeight / 2);
        }
        void Wheel(int delta) => scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        try
        {
            Pump(() => scroll.VerticalOffset > 0 && CenterError(12) < 2);
            Assert(items.Items.Count == 48, "The full lyric document must be available");
            double centered = scroll.VerticalOffset;
            Wheel(120); Delay(80);
            Assert(!view.IsFollowing && scroll.VerticalOffset < centered, "Wheel up must pause following and browse earlier lyrics");
            double previous = scroll.VerticalOffset;
            Wheel(-120); Delay(80);
            Assert(scroll.VerticalOffset > previous, "Wheel down must browse later lyrics");
            double manual = scroll.VerticalOffset;
            view.Lines = SampleLyrics().Append(new LyricLine(TimeSpan.FromSeconds(250), "新识别的片段")).ToArray(); Delay(100);
            Assert(!view.IsFollowing && Math.Abs(scroll.VerticalOffset - manual) < 2 && items.Items.Count == 49, "Streaming new lyrics must retain manual scroll position");
            view.CurrentIndex = 25; Delay(500);
            Assert(Math.Abs(scroll.VerticalOffset - manual) < 2, "Playback must not interrupt manual browsing");
            Assert(((SynchronizedLyricsView.LyricRow)items.Items[25]).IsCurrent && !((SynchronizedLyricsView.LyricRow)items.Items[12]).IsCurrent, "Highlight must continue to track playback while browsing");
            Delay(2100); Wheel(120); Delay(3000);
            Assert(!view.IsFollowing, "Each new wheel input must restart the five-second idle interval");
            Pump(() => view.IsFollowing && CenterError(25) < 2, 4000);
            view.CurrentIndex = 4;
            Pump(() => CenterError(4) < 2);
            Wheel(120); Delay(80);
            view.ResumeFollowing();
            Pump(() => view.IsFollowing && CenterError(4) < 2);
            Wheel(120);
            view.Lines = SampleLyrics().Take(3).ToArray(); view.CurrentIndex = 0;
            Pump(() => view.IsFollowing && CenterError(0) < 2);
            Assert(items.Items.Count == 3, "Changing songs must replace, not append, lyric rows");
            view.Lines = Array.Empty<LyricLine>(); view.CurrentIndex = -1;
            Delay(100);
            Assert(items.Items.Count == 0, "A song without lyrics must clear old rows");
        }
        finally { host.Close(); }
    }

    private static void PortableState()
    {
        string original = Path.Combine(_root, "portable-original"), moved = Path.Combine(_root, "portable-moved");
        Directory.CreateDirectory(original);
        var store = new StateStore(Path.Combine(original, "data"), original);
        var state = store.Load();
        state.Settings.PythonPath = Path.Combine(original, "python", "python.exe");
        state.Settings.ModelsDirectory = Path.Combine(original, "models");
        state.Settings.ModelPath = Path.Combine(original, "models", "faster-whisper-tiny");
        state.Playlists[0].Tracks.Add(new Track { FilePath = Path.Combine(original, "Music", "歌曲.wav"), LyricsPath = Path.Combine(original, "Music", "歌曲.lrc") });
        store.Save(state);
        string json = File.ReadAllText(Path.Combine(original, "data", "state.json"));
        Assert(json.Contains("python/python.exe") && !json.Contains(original.Replace("\\", "\\\\")), "Saved internal paths must be relative");
        Assert(Path.IsPathRooted(state.Settings.PythonPath), "Saving changed live settings");
        Directory.Move(original, moved); // Both paths are generated under this test's private artifact directory.
        var loaded = new StateStore(Path.Combine(moved, "data"), moved).Load();
        Assert(loaded.Settings.PythonPath == Path.Combine(moved, "python", "python.exe"), "Python did not relocate");
        Assert(loaded.Playlists[0].Tracks[0].FilePath == Path.Combine(moved, "Music", "歌曲.wav"), "Music did not relocate");
        Assert(loaded.Playlists[0].Tracks[0].LyricsPath == Path.Combine(moved, "Music", "歌曲.lrc"), "Lyrics did not relocate");
        Assert(loaded.Settings.ModelsDirectory == Path.Combine(moved, "models"), "Models root did not relocate");
    }

    private static void Models()
    {
        string root = Path.Combine(_root, "model-catalog");
        foreach (string id in new[] { "tiny", "small", "incomplete", "tiny.en", "distil-large-v3" })
        {
            string directory = Path.Combine(root, "faster-whisper-" + id); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "model.bin"), "model");
            if (id == "incomplete") continue;
            File.WriteAllText(Path.Combine(directory, "config.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "tokenizer.json"), "{}");
        }
        var models = ModelCatalog.Discover(root);
        Assert(models.Count == 2 && models.Any(m => m.Id == "tiny"), "Incomplete and English-only models should not be selectable");
        Assert(models.All(m => Path.IsPathRooted(m.DirectoryPath)), "Runtime model paths must be absolute");
    }

    private static byte[] CoverPng()
    {
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, new byte[] { 90, 120, 200, 0, 90, 120, 200, 0, 90, 120, 200, 0, 90, 120, 200, 0 }, 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    private static void ModelRefresh()
    {
        string root = Path.Combine(_root, "refresh-models");
        string Complete(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(root, relative)); Directory.CreateDirectory(path);
            foreach (string file in new[] { "model.bin", "config.json", "tokenizer.json" }) File.WriteAllText(Path.Combine(path, file), "{}");
            return path;
        }
        foreach (string id in new[] { "tiny", "base", "small", "medium", "large-v1", "large-v2", "large-v3", "large-v3-turbo" }) Complete("faster-whisper-" + id);
        var store = new StateStore(Path.Combine(_root, "refresh-ui")); var state = store.Load();
        state.Settings.ModelsDirectory = root; state.Settings.ModelPath = Path.Combine(root, "faster-whisper-small"); state.Settings.ModelId = "small";
        store.Save(state);
        var window = new MainWindow(store, false);
        var view = (PlayerViewModel)window.DataContext;
        var picker = (ComboBox)window.FindName("ModelPicker");
        void Refresh() { Invoke(window, "RefreshModels_Click", window, new RoutedEventArgs()); Delay(30); }
        Delay(50);
        Assert(picker.Items.Count == 8 && picker.SelectedItem is LocalModel { Id: "small" }, $"Initial eight models and selection: items={picker.Items.Count}, models={view.Models.Count}, selected={picker.SelectedItem}, path={view.Settings.ModelPath}, root={view.Settings.ModelsDirectory}");
        string snapshot = Complete("models--Example--faster-whisper-custom/snapshots/commit1");
        Complete("models--Example--faster-whisper-custom/snapshots/commit2");
        string refs = Path.Combine(root, "models--Example--faster-whisper-custom", "refs"); Directory.CreateDirectory(refs); File.WriteAllText(Path.Combine(refs, "main"), "commit1");
        Complete(".downloads/faster-whisper-hidden"); Complete("faster-whisper-base.en");
        for (int i = 0; i < 3; i++) Refresh();
        Assert(picker.Items.Count == 9 && view.Settings.ModelId == "small" && ((LocalModel)picker.SelectedItem).Id == "small", "Refreshing after manual additions must keep all models and the selected one");
        Assert(view.Models.Single(m => m.Id == "custom").DirectoryPath == snapshot, "Hugging Face cache should choose refs/main and not duplicate snapshots");
        string temporarilyMoved = Path.Combine(root, "faster-whisper-base", "tokenizer.json");
        File.Move(temporarilyMoved, temporarilyMoved + ".pending"); Refresh();
        Assert(picker.Items.Count == 8 && view.Models.Any(m => m.Id == "large-v3"), "An incomplete folder must not hide other models");
        File.Move(temporarilyMoved + ".pending", temporarilyMoved); Refresh();
        Assert(picker.Items.Count == 9, "Completing a manually updated model must restore it");
        picker.SelectedItem = view.Models.Single(m => m.Id == "medium"); Refresh();
        Assert(view.Settings.ModelId == "medium" && ((LocalModel)picker.SelectedItem).Id == "medium" && picker.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedValueProperty) is not null, "User selection and binding must survive repeated refreshes");
        view.Settings.ModelsDirectory = Path.Combine(root, "faster-whisper-tiny"); Refresh();
        Assert(picker.Items.Count == 9 && view.Settings.ModelsDirectory == root, "A single model folder as repository must normalize to its sibling model root");
        window.Close();
    }
    private static void Covers()
    {
        string folder = Path.Combine(_root, "covers"); Directory.CreateDirectory(folder);
        string embedded = Path.Combine(folder, "embedded.wav"); WriteWave(embedded);
        using (var file = TagLib.File.Create(embedded))
        {
            file.Tag.Pictures = new[] { new TagLib.Picture(new TagLib.ByteVector(CoverPng())) { Type = TagLib.PictureType.FrontCover, MimeType = "image/png" } };
            file.Tag.Performers = new[] { "封面测试" }; file.Tag.Album = "测试专辑"; file.Save();
        }
        var service = new CoverArtService();
        var result = service.Load(embedded);
        Assert(result.Cover is { IsFrozen: true } && result.Artist == "封面测试", "Embedded cover and metadata");
        using (File.Open(embedded, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        string sidecar = Path.Combine(folder, "sidecar.wav"); WriteWave(sidecar);
        File.WriteAllBytes(Path.Combine(folder, "cover.png"), CoverPng());
        Assert(service.Load(sidecar).Cover is not null, "Folder cover fallback");
        File.WriteAllText(Path.Combine(folder, "cover.png"), "broken image");
        Assert(service.Load(sidecar).Cover is null, "Corrupt artwork should not stop playback");
    }
    private static void RenderElement(FrameworkElement element, int width, int height, string path)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        Pump(() => true, 100);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(243, 246, 242)), null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void Pump(Func<bool> condition, int timeoutMs = 10000)
    {
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (condition() || watch.ElapsedMilliseconds >= timeoutMs) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        Assert(condition(), "Timed out waiting for dispatcher / media event");
    }
    private static void WriteWave(string path)
    {
        const int sampleRate = 16000, seconds = 3, dataSize = sampleRate * seconds * 2;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + dataSize); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataSize);
        for (int i = 0; i < sampleRate * seconds; i++) writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * 2000));
    }
    private static void Media()
    {
        string path = Path.Combine(_root, "中文测试.wav"); WriteWave(path);
        using var audio = new AudioService { Volume = 0 }; string? error = null; bool ended = false;
        audio.Failed += message => error = message; audio.Ended += () => ended = true;
        audio.Open(path); Pump(() => audio.IsReady || error is not null);
        Assert(error is null && audio.IsPlaying && Math.Abs(audio.Duration.TotalSeconds - 3) < 0.1, "WAV failed: " + error);
        audio.Toggle(); Assert(!audio.IsPlaying, "Pause");
        audio.Seek(1.5); Assert(Math.Abs(audio.Position.TotalSeconds - 1.5) < 0.2, "Seek");
        audio.Toggle(); Pump(() => ended, 5000);
        Assert(!audio.IsPlaying, "Ended flag");
        audio.Toggle();
        Assert(audio.IsPlaying && audio.Position < TimeSpan.FromSeconds(0.5), "Replay after ended");
        audio.Toggle(); audio.Seek(0); audio.PlaybackRate = 0.5;
        Assert(!audio.IsPlaying, "Changing playback rate while paused must stay paused");
        // MediaPlayer applies seek/rate commands asynchronously; measure a settled interval.
        Delay(200); audio.Toggle(); Delay(200); double begin = audio.Position.TotalSeconds;
        Delay(500); double slow = audio.Position.TotalSeconds - begin; audio.Toggle();
        audio.Seek(0); audio.PlaybackRate = 2; Delay(200); audio.Toggle(); Delay(200); begin = audio.Position.TotalSeconds;
        Delay(500); double fast = audio.Position.TotalSeconds - begin; audio.Toggle();
        Assert(slow > 0.1 && fast > slow * 2 && fast < 1.8, $"Playback speed must change actual audio time (0.5x={slow:F2}, 2x={fast:F2})");
        audio.Open(path); Pump(() => audio.IsReady);
        Assert(audio.PlaybackRate == 2, "Speed must persist when opening another audio");
    }
    private static void ProcessTests(string python)
    {
        string worker = Path.Combine(_root, "process fixture.py");
        File.WriteAllText(worker, """
import argparse, json, os, pathlib, time, sys
p=argparse.ArgumentParser()
p.add_argument('--model'); p.add_argument('--device'); p.add_argument('--audio'); p.add_argument('--output'); p.add_argument('--language'); p.add_argument('--check', action='store_true')
a=p.parse_args()
assert os.environ['HF_HUB_OFFLINE']=='1'
print(json.dumps({'percent': 10, 'message': 'started'}), flush=True)
if a.language=='slow':
    pathlib.Path(a.model, 'started').write_text(str(os.getpid()))
    time.sleep(30)
if a.language=='fail':
    print('expected diagnostic', file=sys.stderr)
    sys.exit(3)
if a.output: pathlib.Path(a.output).write_text('[00:00.00]中文歌词', encoding='utf-8')
""");
        var settings = new PlayerSettings { PythonPath = python, ModelPath = _root };
        var service = new TranscriptionService(worker);
        var progress = new Progress<RecognitionProgress>();
        string output = Path.Combine(_root, "输出 歌词.lrc");
        void Await(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        Await(service.RunAsync(settings, "音频.wav", output, progress, CancellationToken.None));
        Assert(File.ReadAllText(output).Contains("中文歌词"), "UTF-8 subprocess protocol");
        File.WriteAllText(output, "keep existing"); settings.Language = "fail";
        try { Await(service.RunAsync(settings, "音频.wav", output, progress, CancellationToken.None)); throw new Exception("Expected process failure"); }
        catch (InvalidOperationException ex) { Assert(ex.Message.Contains("expected diagnostic"), "Failure diagnostic lost"); }
        Assert(File.ReadAllText(output) == "keep existing", "Failure overwrote old lyrics");
        settings.Language = "slow";
        using var cancellation = new CancellationTokenSource();
        var running = service.RunAsync(settings, "音频.wav", output, progress, cancellation.Token);
        Pump(() => File.Exists(Path.Combine(_root, "started")));
        int pid = int.Parse(File.ReadAllText(Path.Combine(_root, "started")));
        cancellation.Cancel();
        try { Await(running); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        Assert(File.ReadAllText(output) == "keep existing", "Cancellation overwrote old lyrics");
        Assert(!Directory.EnumerateFiles(_root, "*.lrc.tmp").Any(), "Temporary lyrics not cleaned up");
        try { using var process = Process.GetProcessById(pid); Assert(process.HasExited, "Worker still running"); } catch (ArgumentException) { }
    }

    private static void BatchProcessTests(string python)
    {
        string folder = Path.Combine(_root, "batch 中文"); Directory.CreateDirectory(folder);
        string worker = Path.Combine(folder, "worker.py");
        File.WriteAllText(worker, """
import argparse,json,pathlib,time
p=argparse.ArgumentParser()
p.add_argument('--model');p.add_argument('--batch');p.add_argument('--device');p.add_argument('--language');p.add_argument('--vad',action='store_true');p.add_argument('--workers',type=int);p.add_argument('--pause-file')
a=p.parse_args()
root=pathlib.Path(a.model)
assert a.workers >= 1
with (root/'starts').open('a') as f: f.write('loaded\n')
for job in json.loads(pathlib.Path(a.batch).read_text(encoding='utf-8')):
    name=pathlib.Path(job['audio']).stem
    def send(state,message): print(json.dumps({'index':job['index'],'state':state,'percent':50,'message':message}),flush=True)
    send('running',name)
    if a.pause_file and pathlib.Path(a.pause_file).exists():
        send('paused','paused')
        while pathlib.Path(a.pause_file).exists(): time.sleep(0.05)
        send('running','resumed')
    if name=='bad':
        send('failed','expected bad audio')
        continue
    pathlib.Path(job['temporary']).write_text('[00:00.00]批量测试',encoding='utf-8')
    if name=='slow':
        (root/'slow-started').write_text('started')
        time.sleep(30)
    send('completed','done')
""");
        string Audio(string name) { string path = Path.Combine(folder, name); File.WriteAllText(path, "fixture"); return path; }
        string first = Audio("first.wav"), collision = Audio("first.mp3"), existing = Audio("existing.wav"), bad = Audio("bad.wav"), last = Audio("last.wav");
        File.WriteAllText(Path.ChangeExtension(existing, ".lrc"), "keep old");
        var settings = new PlayerSettings { PythonPath = Path.GetFullPath(python), ModelPath = folder };
        var events = new List<BatchRecognitionProgress>();
        var service = new BatchTranscriptionService(worker);
        var task = service.RunAsync(settings, new[] { first, collision, existing, bad, last }, false, events.Add, CancellationToken.None);
        Pump(() => task.IsCompleted); var result = task.GetAwaiter().GetResult();
        Assert(result == new BatchRecognitionResult(2, 2, 1), "Batch must continue after failure and skip existing/conflicting outputs");
        Assert(File.ReadAllText(Path.ChangeExtension(first, ".lrc")).Contains("批量测试") && File.Exists(Path.ChangeExtension(last, ".lrc")), "Sibling same-name LRC was not generated");
        Assert(File.ReadAllText(Path.ChangeExtension(existing, ".lrc")) == "keep old", "Default batch must preserve existing LRC");
        Assert(File.ReadAllLines(Path.Combine(folder, "starts")).Length == 1, "Batch launched more than one worker");
        string done = Audio("completed-before-cancel.wav"), slow = Audio("slow.wav"), pending = Audio("pending.wav");
        File.WriteAllText(Path.ChangeExtension(slow, ".lrc"), "original slow lyrics");
        using var cancellation = new CancellationTokenSource();
        var cancelled = service.RunAsync(settings, new[] { done, slow, pending }, true, events.Add, cancellation.Token);
        Pump(() => File.Exists(Path.Combine(folder, "slow-started")) && File.Exists(Path.ChangeExtension(done, ".lrc")));
        cancellation.Cancel(); Pump(() => cancelled.IsCompleted);
        try { cancelled.GetAwaiter().GetResult(); throw new Exception("Expected batch cancellation"); } catch (OperationCanceledException) { }
        Assert(File.Exists(Path.ChangeExtension(done, ".lrc")) && !File.Exists(Path.ChangeExtension(pending, ".lrc")), "Cancellation must retain completed items and stop pending ones");
        Assert(File.ReadAllText(Path.ChangeExtension(slow, ".lrc")) == "original slow lyrics", "Cancelled overwrite must not replace original lyrics");
        Assert(!Directory.EnumerateFiles(folder, "*.lrc.tmp").Any(), "Batch temporary files leaked");
        var replace = service.RunAsync(settings, new[] { existing }, true, events.Add, CancellationToken.None);
        Pump(() => replace.IsCompleted); Assert(replace.GetAwaiter().GetResult().Completed == 1 && File.ReadAllText(Path.ChangeExtension(existing, ".lrc")) != "keep old", "Explicit overwrite should replace only after success");
        using var pause = new RecognitionPauseControl();
        pause.Pause(); events.Clear();
        string pausedAudio = Audio("pause-resume.wav");
        int starts = File.ReadAllLines(Path.Combine(folder, "starts")).Length;
        var paused = service.RunAsync(settings, new[] { pausedAudio }, false, events.Add, CancellationToken.None, pause);
        Pump(() => events.Any(e => e.State == RecognitionState.Paused));
        Delay(200);
        Assert(!paused.IsCompleted && !File.Exists(Path.ChangeExtension(pausedAudio, ".lrc")), "Paused process must retain pending work");
        pause.Resume(); Pump(() => paused.IsCompleted);
        Assert(paused.GetAwaiter().GetResult().Completed == 1 && File.ReadAllLines(Path.Combine(folder, "starts")).Length == starts + 1, "Resume must finish in the same worker process");
        pause.Pause(); events.Clear();
        using var pausedCancel = new CancellationTokenSource();
        var pausedCancelled = service.RunAsync(settings, new[] { pausedAudio }, true, events.Add, pausedCancel.Token, pause);
        Pump(() => events.Any(e => e.State == RecognitionState.Paused)); pausedCancel.Cancel(); Pump(() => pausedCancelled.IsCompleted);
        try { pausedCancelled.GetAwaiter().GetResult(); throw new Exception("Expected cancellation while paused"); } catch (OperationCanceledException) { }
        Assert(File.ReadAllText(Path.ChangeExtension(pausedAudio, ".lrc")).Contains("批量测试"), "Cancelling a paused overwrite must preserve prior LRC");
    }

    private static void OfflineModel(string python, string model, string speech)
    {
        var settings = new PlayerSettings { PythonPath = Path.GetFullPath(python), ModelPath = Path.GetFullPath(model), Language = "en" };
        string output = Path.Combine(_root, "真实离线识别.lrc");
        var segments = new List<RecognizedSegment>(); bool partialBeforeFile = false;
        var task = new TranscriptionService().RunAsync(settings, Path.GetFullPath(speech), output, new Progress<RecognitionProgress>(), CancellationToken.None,
            segment => { segments.Add(segment); partialBeforeFile |= !File.Exists(output); });
        Pump(() => task.IsCompleted, 30000); task.GetAwaiter().GetResult();
        var lrc = LrcDocument.Load(output);
        Assert(lrc.Lines.Any(line => line.Text.Contains("local audio player", StringComparison.OrdinalIgnoreCase)), "Real model did not produce the expected speech");
        Assert(lrc.Lines.Count(line => line.Text.Length > 0) >= 2, "Expected timed segments");
        Assert(partialBeforeFile && segments.Count >= 2 && segments.All(s => s.End >= s.Start), "Real worker must stream timestamped captions before publishing the complete LRC");
        string batchFolder = Path.Combine(_root, "real-batch"); Directory.CreateDirectory(batchFolder);
        string first = Path.Combine(batchFolder, "语音一.wav"), second = Path.Combine(batchFolder, "语音二.wav");
        File.Copy(Path.GetFullPath(speech), first); File.Copy(Path.GetFullPath(speech), second);
        var batch = new BatchTranscriptionService().RunAsync(settings, new[] { first, second }, false, _ => { }, CancellationToken.None);
        Pump(() => batch.IsCompleted, 45000);
        Assert(batch.GetAwaiter().GetResult().Completed == 2, "Real batch inference did not complete both files");
        Assert(LrcDocument.Load(Path.ChangeExtension(second, ".lrc")).Lines.Any(l => l.Text.Contains("local audio player", StringComparison.OrdinalIgnoreCase)), "Real batch output missing expected transcription");
    }

    private static void LiveRecognition(string python)
    {
        string folder = Path.Combine(_root, "live 中文"); Directory.CreateDirectory(folder);
        string models = Path.Combine(folder, "models"), model = Path.Combine(models, "faster-whisper-tiny"); Directory.CreateDirectory(model);
        foreach (string file in new[] { "model.bin", "config.json", "tokenizer.json" }) File.WriteAllText(Path.Combine(model, file), "{}");
        string worker = Path.Combine(folder, "live.py");
        File.WriteAllText(worker, """
import argparse,json,pathlib,time
p=argparse.ArgumentParser()
p.add_argument('--model');p.add_argument('--device');p.add_argument('--audio');p.add_argument('--output');p.add_argument('--language');p.add_argument('--stream',action='store_true')
a=p.parse_args(); root=pathlib.Path(a.model); name=pathlib.Path(a.audio).stem
assert a.stream
(root/(name+'.started')).write_text('started')
print(json.dumps({'type':'segment','start':0,'end':0.8,'text':name+' partial'}),flush=True)
deadline=time.monotonic()+20
while not (root/(name+'.continue')).exists() and time.monotonic()<deadline: time.sleep(0.05)
pathlib.Path(a.output).write_text('[00:00.00]'+name+' complete\n[00:00.80]\n',encoding='utf-8')
""");
        var store = new StateStore(Path.Combine(folder, "state")); var state = store.Load();
        state.Settings.PythonPath = Path.GetFullPath(python); state.Settings.ModelsDirectory = models; state.Settings.ModelPath = model;
        state.Settings.Mode = PlayMode.Single; state.Settings.RepeatPlaylist = false; state.Settings.Volume = 0; store.Save(state);
        var window = new MainWindow(store, false, new TranscriptionService(worker)) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        window.Show(); Delay(100);
        var view = (PlayerViewModel)window.DataContext;
        var audio = Private<AudioService>(window, "_audio");
        Track Add(string name) { string path = Path.Combine(folder, name + ".wav"); WriteWave(path); var track = new Track { FilePath = path }; view.SelectedPlaylist.Tracks.Add(track); return track; }
        var first = Add("first"); var second = Add("second"); var existing = Add("existing"); var external = Add("external");
        File.WriteAllText(Path.ChangeExtension(existing.FilePath, ".lrc"), "[00:00.00]existing lyrics");
        void Play(Track track) { Invoke(window, "PlayTrack", track); Pump(() => audio.IsReady); audio.Toggle(); }
        void Continue(Track track) => File.WriteAllText(Path.Combine(model, Path.GetFileNameWithoutExtension(track.FilePath) + ".continue"), "continue");
        void AwaitLive() { var task = Private<Task>(window, "_liveTask"); Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        try
        {
            Play(first); Delay(100);
            Assert(!File.Exists(Path.Combine(model, "first.started")), "Automatic recognition must default to disabled");
            view.Settings.AutoRecognizePlaying = true;
            Pump(() => view.LyricLines.Any(l => l.Text == "first partial"));
            Assert(!File.Exists(Path.ChangeExtension(first.FilePath, ".lrc")), "Partial subtitles must display before final LRC exists");
            var tabs = (TabControl)window.FindName("MainTabs");
            tabs.SelectedIndex = 1; Delay(50); tabs.SelectedIndex = 0; Delay(50);
            Assert(audio.IsReady && view.LyricLines.Any(l => l.Text == "first partial"), "Navigation must retain the current audio and live captions");
            var previous = Private<Task>(window, "_liveTask");
            Play(second);
            Pump(() => view.LyricLines.Any(l => l.Text == "second partial"));
            Assert(previous.IsCompleted && view.LyricLines.All(l => !l.Text.Contains("first")) && !File.Exists(Path.ChangeExtension(first.FilePath, ".lrc")), "Switching songs must cancel old recognition and discard stale segments");
            Continue(second); AwaitLive();
            Assert(File.ReadAllText(Path.ChangeExtension(second.FilePath, ".lrc")).Contains("second complete") && view.LyricLines.Any(l => l.Text == "second complete"), "Completion must publish and load the sibling LRC");
            Play(existing); Delay(150);
            Assert(view.LyricLines.Any(l => l.Text == "existing lyrics") && !File.Exists(Path.Combine(model, "existing.started")), "Existing LRC must skip automatic recognition");
            Play(external); Pump(() => view.LyricLines.Any(l => l.Text == "external partial"));
            File.WriteAllText(Path.ChangeExtension(external.FilePath, ".lrc"), "[00:00.00]manually added");
            Continue(external); AwaitLive();
            Assert(File.ReadAllText(Path.ChangeExtension(external.FilePath, ".lrc")).Contains("manually added") && view.LyricLines.Any(l => l.Text == "manually added"), "LRC supplied while recognition runs must be preserved and loaded");
            Assert(!Directory.EnumerateFiles(folder, "*.lrc.tmp").Any(), "Live recognition must clean temporary files");
        }
        finally { window.Close(); if (Private<Task?>(window, "_liveTask") is { } task) Pump(() => task.IsCompleted); }
    }

    private static void EnvironmentCheck(string python)
    {
        var task = new EnvironmentInspectionService().InspectAsync(Path.GetFullPath(python), CancellationToken.None);
        Pump(() => task.IsCompleted, 50000);
        var report = task.GetAwaiter().GetResult();
        Assert(report.CpuReady && report.Items.Any(i => i.Name == "CUDA 驱动") && report.Items.Any(i => i.Name == "cuDNN 9"), "Environment report missing required diagnostics");
        if (Application.Current is not null)
        {
            var window = new EnvironmentWindow(report);
            Assert(((System.Windows.Documents.Hyperlink)window.FindName("CublasDownloadLink")).NavigateUri.Host == "developer.nvidia.com", "Missing explicit official cuBLAS download link");
            RenderElement((FrameworkElement)window.Content, 784, 680, Path.Combine(_root, "environment.png"));
            window.Close();
        }
    }

    private static void OnlineModelsTest(string python)
    {
        string root = Path.Combine(_root, "online-download");
        var service = new OnlineModelService();
        int count = 0, errors = 0;
        var list = service.RunAsync(Path.GetFullPath(python), root, null, data =>
        {
            if (data.GetProperty("type").GetString() != "catalog") return;
            foreach (var model in data.GetProperty("models").EnumerateArray())
            {
                count++;
                if (model.GetProperty("error").GetString() != "") errors++;
                else Assert(model.GetProperty("bytes").GetInt64() > 0, "Missing online file size");
            }
        }, CancellationToken.None);
        Pump(() => list.IsCompleted, 90000); list.GetAwaiter().GetResult();
        Assert(count == 8 && errors == 0, "Live model catalog queries failed");
        bool completed = false;
        var download = service.RunAsync(Path.GetFullPath(python), root, "tiny", data => completed |= data.GetProperty("type").GetString() == "completed", CancellationToken.None);
        Pump(() => download.IsCompleted, 180000); download.GetAwaiter().GetResult();
        Assert(completed && ModelCatalog.Discover(root).Count == 1 && File.Exists(Path.Combine(root, "faster-whisper-tiny", "download-manifest.json")), "Downloaded model was not verified and discoverable");
        using var cancellation = new CancellationTokenSource();
        var cancelled = service.RunAsync(Path.GetFullPath(python), root, "base", data => { if (data.GetProperty("type").GetString() == "progress") cancellation.Cancel(); }, cancellation.Token);
        Pump(() => cancelled.IsCompleted, 20000);
        try { cancelled.GetAwaiter().GetResult(); throw new Exception("Expected download cancellation"); } catch (OperationCanceledException) { }
        Assert(ModelCatalog.Discover(root).Count == 1, "Cancelled model must not appear in the selector");
    }

    private static T Private<T>(object instance, string field) => (T)instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Invoke(object instance, string method, params object[] args) => instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
    private static void NativeLifecycle()
    {
        if (Application.Current is null) { var app = new App(startMainWindow: false); app.InitializeComponent(); }
        var store = new StateStore(Path.Combine(_root, "native"));
        var window = new MainWindow(store, true) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        var model = (PlayerViewModel)window.DataContext;
        model.Settings.Volume = 0;
        window.Show();
        string wave = Path.Combine(_root, "native.wav"); WriteWave(wave);
        Task import = window.ImportPathsAsync(new[] { wave, wave });
        Pump(() => import.IsCompleted); import.GetAwaiter().GetResult();
        Assert(model.SelectedPlaylist.Tracks.Count == 1, "Import duplicate handling");
        var tray = Private<Forms.NotifyIcon>(window, "_tray");
        var audio = Private<AudioService>(window, "_audio");
        ((Forms.ToolStripMenuItem)tray.ContextMenuStrip!.Items[1]).PerformClick();
        Pump(() => audio.IsReady);
        typeof(MainWindow).GetField("_trayHintShown", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        window.Close();
        Assert(!window.IsVisible && tray.Visible && audio.IsPlaying, "Close must keep playback and tray alive");
        model.Settings.DesktopLyrics = true;
        var lyrics = Private<LyricsWindow>(window, "_lyricsWindow");
        model.Settings.LockLyrics = true;
        long style = GetWindowLongPtr(new WindowInteropHelper(lyrics).Handle, -20).ToInt64();
        Assert((style & 0x20) != 0 && (style & 0x08000000) != 0, "Lyrics must be click-through and non-activating");
        model.Settings.LockLyrics = false;
        Assert((GetWindowLongPtr(new WindowInteropHelper(lyrics).Handle, -20).ToInt64() & 0x20) == 0, "Lyrics must unlock");
        model.Settings.VerticalLyrics = true;
        Assert(lyrics.Height > lyrics.Width, "Vertical mode must update a visible desktop overlay");
        model.Settings.LockLyrics = true;
        Assert((GetWindowLongPtr(new WindowInteropHelper(lyrics).Handle, -20).ToInt64() & 0x20) != 0, "Vertical mode must preserve click-through locking");
        model.Settings.DesktopLyrics = false;
        Assert(!lyrics.IsVisible, "Hide desktop lyrics");
        string instanceKey = "ShengYu.Tests." + Guid.NewGuid().ToString("N");
        using (var singleton = new SingleInstanceService(instanceKey))
        {
            Assert(singleton.IsPrimary, "First instance must own mutex");
            singleton.StartListening(window.ActivateFromLaunch);
            string imported = Path.Combine(_root, "secondary-launch.wav"); WriteWave(imported);
            for (int i = 0; i < 2; i++)
            {
                window.Hide();
                if (i == 1) window.WindowState = WindowState.Minimized;
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
                foreach (string argument in new[] { Assembly.GetExecutingAssembly().Location, "--instance-client", instanceKey, imported }) start.ArgumentList.Add(argument);
                using var child = Process.Start(start)!;
                Pump(() => child.HasExited, 20000);
                Assert(child.ExitCode == 0 && window.IsVisible && window.WindowState != WindowState.Minimized, "Second process must exit and restore hidden/minimized first window");
            }
            Assert(model.SelectedPlaylist.Tracks.Count == 2, "Second launch must pass files once to existing player");
        }
        using (var replacement = new SingleInstanceService(instanceKey)) Assert(replacement.IsPrimary, "Mutex must release so the app can restart");
        ((Forms.ToolStripMenuItem)tray.ContextMenuStrip.Items[0]).PerformClick();
        Assert(window.IsVisible, "Tray restores main window");
        ((Forms.ToolStripMenuItem)tray.ContextMenuStrip.Items[^1]).PerformClick();
        Assert(!tray.Visible && !audio.IsPlaying, "Tray exit must release icon and playback");
        Assert(File.Exists(Path.Combine(store.DirectoryPath, "state.json")), "Exit persisted state");
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
}
