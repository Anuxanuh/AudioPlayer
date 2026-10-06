using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AudioPlayer;
using AudioPlayer.Models;
using AudioPlayer.Services;
using AudioPlayer.ViewModels;
using AudioPlayer.Views;

internal static partial class Program
{
    private static void TranslationCore()
    {
        var original = LrcDocument.Parse("[offset:-25]\n[00:01.234][00:03.456]Hello\n[00:02.000]\n[00:03.456]World");
        var translated = new LrcDocument(original.Lines.Select(l => new LyricLine(l.Time, l.Text == "" ? "" : l.Text == "Hello" ? "你好" : "世界")).ToArray());
        string serialized = LyricTranslation.Serialize(translated, "zh");
        Assert(LrcDocument.Parse(serialized).Lines.SequenceEqual(translated.Lines), "Translation LRC must preserve effective millisecond timestamps, repeats and silence");
        for (int ms = 0; ms < 1000; ms++)
        {
            var precise = LrcDocument.Parse($"[offset:12]\n[00:01.{ms:000}]line");
            Assert(precise.Lines[0].Time.Ticks == (1012 + ms) * TimeSpan.TicksPerMillisecond
                && LrcDocument.Parse(LyricTranslation.Serialize(precise, "zh")).Lines[0].Time == precise.Lines[0].Time,
                "Every millisecond must remain exact after offset + LRC roundtrip");
        }
        var both = LyricTranslation.Compose(original, translated, LyricDisplayMode.Bilingual);
        Assert(both.Lines.Count == 4 && both.Lines[0].DisplayText == "Hello\n你好" && both.Lines[1].DisplayText == "" && both.Lines[3].DisplayText == "World\n世界", "Bilingual pairing must use timestamp plus occurrence");
        Assert(ReferenceEquals(LyricTranslation.Compose(original, translated, LyricDisplayMode.Original), original), "Original mode must not mutate the source");
        Assert(ReferenceEquals(LyricTranslation.Compose(original, translated, LyricDisplayMode.Translation), translated), "Translation-only mode must contain no source text");
        var empty = new LrcDocument(Array.Empty<LyricLine>());
        Assert(LyricTranslation.Compose(original, empty, LyricDisplayMode.Translation).Lines.Count == 0, "Missing translation must not masquerade as translated text");
        Assert(LyricTranslation.OutputPath(Path.Combine(_root, "歌曲.name.MP3"), "ja").EndsWith("歌曲.name.ja.lrc"), "Language sidecar naming");
        bool badLanguage = false;
        try { LyricTranslation.OutputPath("song.mp3", "../escape"); } catch (ArgumentException) { badLanguage = true; }
        Assert(badLanguage, "Reject unsafe/unknown language codes");
        string oldRoot = Path.Combine(_root, "translation-before"), newRoot = Path.Combine(_root, "translation-after");
        var store = new StateStore(Path.Combine(oldRoot, "data"), oldRoot); var state = store.Load();
        state.Settings.TranslationModelPath = Path.Combine(oldRoot, "models", "translation", "model");
        state.Settings.TranslationTargetLanguage = "en";
        state.Settings.LyricDisplayMode = LyricDisplayMode.Bilingual; state.Settings.TranslationUseCuda = true;
        store.Save(state); MoveTestDirectory(oldRoot, newRoot);
        var settings = new StateStore(Path.Combine(newRoot, "data"), newRoot).Load().Settings;
        Assert(settings.TranslationModelPath.StartsWith(newRoot) && settings.TranslationTargetLanguage == "en" && settings.TranslationUseCuda && settings.LyricDisplayMode == LyricDisplayMode.Bilingual, "Portable translation preferences must survive relocation");
    }

    private static void TranslationProcesses(string python)
    {
        string folder = Path.Combine(_root, "翻译进程"); Directory.CreateDirectory(folder);
        string model = Path.Combine(folder, "model"); Directory.CreateDirectory(model);
        foreach (string file in new[] { "model.bin", "config.json", "sentencepiece.bpe.model", "shared_vocabulary.txt" }) File.WriteAllText(Path.Combine(model, file), "test");
        string script = Path.Combine(folder, "fake.py");
        File.WriteAllText(script, """
import json,sys,os,time
from pathlib import Path
assert sys.flags.utf8_mode == 1 and sys.flags.isolated == 1
assert os.environ['HF_HUB_OFFLINE'] == os.environ['TRANSFORMERS_OFFLINE'] == '1'
lines=json.load(sys.stdin)
mode=Path(__file__).with_suffix('.mode').read_text()
if mode=='slow':
    print(json.dumps({'type':'progress','percent':1,'message':'started'}),flush=True)
    time.sleep(30)
for i,line in enumerate(lines):
    if mode=='incomplete' and i==len(lines)-1: break
    print(json.dumps({'type':'line','index':i,'text':'译文：'+line if line.strip() else ''},ensure_ascii=False),flush=True)
if mode=='fail': sys.exit(3)
""", new UTF8Encoding(false));
        var doc = LrcDocument.Parse("[offset:23]\n[00:01.125]中文与 English\n[00:02.75]\n[00:03.333]Last line");
        string audio = Path.Combine(folder, "歌曲.测试.mp3"); string output = LyricTranslation.OutputPath(audio, "zh");
        var options = new TranslationOptions(python, model, "en", "zh", false);
        var service = new LyricTranslationService(script);
        Task<string> Run(string mode, CancellationToken token = default, IProgress<TranslationProgress>? progress = null)
        {
            File.WriteAllText(Path.ChangeExtension(script, ".mode"), mode);
            return Task.Run(() => service.RunAsync(options, doc, audio, progress, token, overwrite: true));
        }
        Run("ok").GetAwaiter().GetResult();
        var result = LrcDocument.Load(output);
        Assert(result.Lines.Select(l => l.Time).SequenceEqual(doc.Lines.Select(l => l.Time)) && result.Lines[0].Text == "译文：中文与 English" && result.Lines[1].Text == "", "Real UTF-8 IPC and host LRC output");
        byte[] before = File.ReadAllBytes(output);
        foreach (string mode in new[] { "fail", "incomplete" })
        {
            bool failed = false; try { Run(mode).GetAwaiter().GetResult(); } catch (Exception) { failed = true; }
            Assert(failed && before.SequenceEqual(File.ReadAllBytes(output)), "Failure must preserve existing translation: " + mode);
        }
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Run("slow", cancel.Token, new ImmediateTranslationProgress(_ => started.TrySetResult()));
        Assert(started.Task.Wait(10000), "Cancellation test worker did not start"); cancel.Cancel();
        bool cancelled = false; try { task.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled && before.SequenceEqual(File.ReadAllBytes(output)) && !Directory.EnumerateFiles(folder, "*.tmp").Any(), "Cancellation must keep old translation and remove temporary output");
        bool conflict = false;
        try { service.RunAsync(options, doc, audio, null, CancellationToken.None).GetAwaiter().GetResult(); } catch (IOException) { conflict = true; }
        Assert(conflict, "Existing translation must require explicit overwrite");
        TranslationAutomaticAndBatch(python, model, script);
    }
    private sealed class ImmediateTranslationProgress(Action<TranslationProgress> callback) : IProgress<TranslationProgress>
    { public void Report(TranslationProgress value) => callback(value); }

    private static void TranslationAutomaticAndBatch(string python, string model, string script)
    {
        if (Application.Current is null) { var app = new App(startMainWindow: false); app.InitializeComponent(); }
        string root = Path.Combine(_root, "automatic-translation"); Directory.CreateDirectory(root);
        string modePath = Path.ChangeExtension(script, ".mode"); File.WriteAllText(modePath, "ok");
        Track Create(string name, bool lrc = true)
        {
            string path = Path.Combine(root, name + ".wav"); WriteWave(path);
            if (lrc) File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[00:00.123]This is a test song\n[00:02.333]The stars shine for you");
            return new Track { FilePath = path };
        }
        var first = Create("first"); var second = Create("second"); var missing = Create("missing", false);
        var store = new StateStore(Path.Combine(root, "data")); var state = store.Load();
        state.Settings.PythonPath = python; state.Settings.TranslationModelPath = model; state.Settings.Volume = 0; store.Save(state);
        var window = new MainWindow(store, false, lyricTranslator: new LyricTranslationService(script))
        { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        window.Show(); var view = (PlayerViewModel)window.DataContext;
        void Ui(Action action) { var work = window.Dispatcher.InvokeAsync(action); Pump(() => work.Task.IsCompleted); work.Task.GetAwaiter().GetResult(); }
        void AutoDone() { var task = Private<Task>(window, "_autoTranslationTask"); Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        try
        {
            Ui(() => Invoke(window, "PlayTrack", first));
            Assert(!File.Exists(LyricTranslation.OutputPath(first.FilePath, "zh")), "Original display must not trigger translation");
            Ui(() => view.Settings.LyricDisplayMode = LyricDisplayMode.Bilingual); AutoDone();
            Assert(File.Exists(LyricTranslation.OutputPath(first.FilePath, "zh")) && view.LyricLines[0].Translation.StartsWith("译文："), "Selecting bilingual must automatically translate and display without a button");
            var prior = Private<Task>(window, "_autoTranslationTask");
            Ui(() => view.Settings.LyricDisplayMode = LyricDisplayMode.Translation);
            Assert(ReferenceEquals(prior, Private<Task>(window, "_autoTranslationTask")), "Existing sidecar must be reused");
            File.WriteAllText(modePath, "slow"); Ui(() => view.Settings.TranslationTargetLanguage = "ja");
            Pump(() => view.TranslationHint.Contains("started"));
            var old = Private<Task>(window, "_autoTranslationTask");
            Ui(() => view.Settings.LyricDisplayMode = LyricDisplayMode.Original); Pump(() => old.IsCompleted);
            Assert(!File.Exists(LyricTranslation.OutputPath(first.FilePath, "ja")), "Original-only cancels pending automatic translation");
            Ui(() => { view.Settings.LyricDisplayMode = LyricDisplayMode.Bilingual; });
            Pump(() => view.TranslationHint.Contains("started")); old = Private<Task>(window, "_autoTranslationTask");
            File.WriteAllText(modePath, "ok"); Ui(() => Invoke(window, "PlayTrack", second)); AutoDone();
            Assert(old.IsCompleted && !File.Exists(LyricTranslation.OutputPath(first.FilePath, "ja")) && File.Exists(LyricTranslation.OutputPath(second.FilePath, "ja")), "Track switch must cancel previous output and translate the new song");
            Ui(() => Invoke(window, "PlayTrack", missing));
            Assert(view.TranslationHint.Contains("等待原文"), "Missing original must wait for recognition rather than running a translation worker");
            File.WriteAllText(Path.ChangeExtension(missing.FilePath, ".lrc"), "[00:00]Recognized lyrics");
            Ui(() => Invoke(window, "AttachGeneratedLyrics", missing.FilePath, Path.ChangeExtension(missing.FilePath, ".lrc"))); AutoDone();
            Assert(File.Exists(LyricTranslation.OutputPath(missing.FilePath, "ja")), "Recognition completion must trigger pending automatic translation");
            Ui(() => { view.Settings.LyricDisplayMode = LyricDisplayMode.Original; view.Settings.TranslationTargetLanguage = "zh"; });
            var broken = Create("broken", false);
            Ui(() => { window.AddTranslationTargets(new[] { first, second, first, broken }); ((Button)window.FindName("StartTranslationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
            var batch = Private<Task>(window, "_translationBatchTask"); Pump(() => batch.IsCompleted); batch.GetAwaiter().GetResult();
            Assert(view.TranslationQueue.Count == 3 && view.TranslationQueue[0].State == RecognitionState.Skipped && view.TranslationQueue[1].State == RecognitionState.Completed && view.TranslationQueue[2].State == RecognitionState.Failed, "Manual batch must deduplicate, skip existing, continue after missing-source failures and save translated lyrics");
            File.WriteAllText(modePath, "slow");
            Ui(() => { view.OverwriteTranslation = true; ((Button)window.FindName("StartTranslationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
            Pump(() => view.TranslationQueue[0].Status == "started");
            Ui(() => Invoke(window, "CancelTranslation_Click", window, new RoutedEventArgs()));
            batch = Private<Task>(window, "_translationBatchTask"); Pump(() => batch.IsCompleted);
            Assert(view.TranslationQueue[0].State == RecognitionState.Cancelled && !view.TranslationBusy, "Manual batch cancellation must release UI and keep saved files");
        }
        finally
        {
            var task = window.Dispatcher.InvokeAsync(() => (Task)InvokeResult(window, "StopTranslationsAsync")).Task.Unwrap(); Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); window.Close();
        }
    }

    private static void TranslationUi()
    {
        if (Application.Current is null) { var app = new App(startMainWindow: false); app.InitializeComponent(); }
        string folder = Path.Combine(_root, "translation-ui"); Directory.CreateDirectory(folder);
        string audio = Path.Combine(folder, "Beyond the sea.wav"); WriteWave(audio);
        File.WriteAllText(Path.ChangeExtension(audio, ".lrc"), "[00:00.000]The wind carries your voice\n[00:01.125]Across the sea\n[00:02.200]I wait under the stars");
        File.WriteAllText(LyricTranslation.OutputPath(audio, "zh"), "[00:00.000]风带来了你的声音\n[00:01.125]越过海洋\n[00:02.200]我在星空下等待");
        var store = new StateStore(Path.Combine(folder, "data")); var state = store.Load(); state.Settings.TranslationModelPath = Path.Combine(folder, "no-model"); store.Save(state);
        var window = new MainWindow(store, false) { Width = 1000, Height = 760, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        var view = (PlayerViewModel)window.DataContext; view.Settings.Volume = 0;
        var track = new Track { FilePath = audio }; view.SelectedPlaylist.Tracks.Add(track);
        window.Show(); Invoke(window, "PlayTrack", track);
        var player = Private<AudioService>(window, "_audio"); Pump(() => player.IsReady);
        player.Toggle(); Private<DispatcherTimer>(window, "_clock").Stop();
        view.Settings.LyricDisplayMode = LyricDisplayMode.Bilingual;
        Assert(view.LyricLines[0].DisplayText.Contains("\n风带来了") && view.CurrentLyric.Contains("风带来了"), "Host and desktop text must compose bilingual lines");
        ((Button)window.FindName("CoverButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Delay(350);
        RenderElement((FrameworkElement)window.Content, 984, 720, Path.Combine(_root, "translation-bilingual.png"));
        var full = (SynchronizedLyricsView)window.FindName("FullLyrics"); ClickLyric(full, 1);
        Assert(Math.Abs(player.Position.TotalSeconds - 1.125) < .1 && !player.IsPlaying, "Bilingual click seeking must preserve timestamp and pause state");
        view.Settings.LyricDisplayMode = LyricDisplayMode.Translation;
        Assert(view.LyricLines[0].Text == "风带来了你的声音" && !view.LyricLines[0].DisplayText.Contains("The wind"), "Translation only");
        view.Settings.TranslationTargetLanguage = "ja";
        Assert(view.LyricLines.Count == 0 && view.TranslationHint.Contains("缺少翻译模型"), "Changing target language must drop old translation and explain missing models without auto-downloading");
        view.Settings.LyricDisplayMode = LyricDisplayMode.Bilingual;
        Assert(view.LyricLines[0].Text.StartsWith("The wind") && view.LyricLines[0].Translation == "", "Missing translation bilingual fallback");
        view.Settings.TranslationTargetLanguage = "zh";
        Invoke(window, "SetLyricsDrawer", false); Delay(320);
        ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("TranslationTab");
        window.AddTranslationTargets(new[] { track });
        RenderElement((FrameworkElement)window.Content, 984, 720, Path.Combine(_root, "translation-batch.png"));
        ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("SettingsTab");
        RenderElement((FrameworkElement)window.Content, 984, 720, Path.Combine(_root, "translation-settings.png"));
        var desktop = new LyricsWindow(view.Settings); view.Settings.FontSize = 80; desktop.ApplySettings(); desktop.SetText("The wind carries your voice\n风带来了你的声音");
        RenderElement((FrameworkElement)desktop.Content, 920, 180, Path.Combine(_root, "translation-desktop.png"));
        view.Settings.VerticalLyrics = true; desktop.ApplySettings(); desktop.SetText("海を越えて\n越过海洋");
        var columns = (StackPanel)desktop.FindName("VerticalColumns");
        Assert(columns.Children.Count >= 2 && ((TextBlock)columns.Children[0]).Text.StartsWith("海"), "Bilingual vertical paragraphs must occupy separate columns");
        RenderElement((FrameworkElement)desktop.Content, (int)desktop.Width, (int)desktop.Height, Path.Combine(_root, "translation-vertical.png")); desktop.Close();
        string second = Path.Combine(folder, "other.wav"); WriteWave(second); File.WriteAllText(Path.ChangeExtension(second, ".lrc"), "[00:00]Other song");
        Invoke(window, "PlayTrack", new Track { FilePath = second });
        Assert(view.LyricLines.Single().Text == "Other song" && view.LyricLines.Single().Translation == "", "Switching songs must clear previous translation"); window.Close();
    }

    private static void TranslationRealModel(string python, string model)
    {
        string audio = Path.Combine(_root, "真实离线翻译.mp3");
        var original = LrcDocument.Parse("[offset:12]\n[00:01.123]The wind carries your voice across the sea.\n[00:03.500]I will wait for you under the stars.\n[00:04.000]\n[00:05.123]The wind carries your voice across the sea.");
        string path = Task.Run(() => new LyricTranslationService().RunAsync(new(python, Path.GetFullPath(model), "auto", "zh", false), original, audio, null, CancellationToken.None)).GetAwaiter().GetResult();
        var result = LrcDocument.Load(path);
        Assert(result.Lines.Select(l => l.Time).SequenceEqual(original.Lines.Select(l => l.Time)) && result.Lines[0].Text.Any(c => c >= '\u4e00' && c <= '\u9fff') && result.Lines[2].Text == "" && result.Lines[3].Text == result.Lines[0].Text, "Real local model must produce Chinese and preserve the full timeline");
        Assert(LyricTranslation.Compose(original, result, LyricDisplayMode.Bilingual).Lines.Count == original.Lines.Count, "Real translation sidecars must align without extra duplicate rows");
        Console.WriteLine("Offline translation sample: " + result.Lines[0].Text);
    }
}
