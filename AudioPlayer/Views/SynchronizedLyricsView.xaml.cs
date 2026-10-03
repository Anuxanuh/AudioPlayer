using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AudioPlayer.Models;
using AudioPlayer.Services;

namespace AudioPlayer.Views;

public partial class SynchronizedLyricsView : UserControl
{
    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(nameof(Lines), typeof(IReadOnlyList<LyricLine>), typeof(SynchronizedLyricsView), new PropertyMetadata(null, LinesChanged));
    public static readonly DependencyProperty CurrentIndexProperty = DependencyProperty.Register(nameof(CurrentIndex), typeof(int), typeof(SynchronizedLyricsView), new PropertyMetadata(-1, CurrentChanged));
    private static readonly DependencyPropertyKey IsFollowingPropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsFollowing), typeof(bool), typeof(SynchronizedLyricsView), new PropertyMetadata(true));
    public static readonly DependencyProperty IsFollowingProperty = IsFollowingPropertyKey.DependencyProperty;
    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.Register("AnimatedOffset", typeof(double), typeof(SynchronizedLyricsView), new PropertyMetadata(0d, (d, e) => ((SynchronizedLyricsView)d).LyricsScroll.ScrollToVerticalOffset((double)e.NewValue)));

    public IReadOnlyList<LyricLine>? Lines { get => (IReadOnlyList<LyricLine>?)GetValue(LinesProperty); set => SetValue(LinesProperty, value); }
    public int CurrentIndex { get => (int)GetValue(CurrentIndexProperty); set => SetValue(CurrentIndexProperty, value); }
    public bool IsFollowing => (bool)GetValue(IsFollowingProperty);
    private readonly DispatcherTimer _resumeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private LyricRow[] _rows = Array.Empty<LyricRow>();
    private bool _centerQueued;
    public event Action<TimeSpan>? SeekRequested;

    public SynchronizedLyricsView()
    {
        InitializeComponent();
        _resumeTimer.Tick += (_, _) =>
        {
            // A held scrollbar thumb is still an active interaction.
            if (IsMouseCaptureWithin || AreAnyTouchesCapturedWithin) return;
            ResumeFollowing();
        };
    }

    private static void LinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (SynchronizedLyricsView)d;
        var previous = e.OldValue as IReadOnlyList<LyricLine>;
        bool streamingUpdate = previous is { Count: > 0 } && view.Lines is { Count: > 0 } lines && lines.Count >= previous.Count && lines[0] == previous[0];
        double offset = view.LyricsScroll.VerticalOffset;
        view.StopAnimation();
        view._rows = view.Lines?.Select(line => new LyricRow(line)).ToArray() ?? Array.Empty<LyricRow>();
        view.LyricItems.ItemsSource = view._rows;
        view.UpdateHighlight();
        if (!streamingUpdate) view.ResumeFollowing(); // Switching songs resets manual browsing.
        else if (view.IsFollowing) view.QueueCenter();
        else view.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (!view.IsFollowing) view.LyricsScroll.ScrollToVerticalOffset(offset); }));
    }
    private static void CurrentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (SynchronizedLyricsView)d;
        view.UpdateHighlight();
        view.QueueCenter();
    }
    private void UpdateHighlight()
    {
        for (int i = 0; i < _rows.Length; i++) _rows[i].IsCurrent = i == CurrentIndex;
    }
    private void QueueCenter()
    {
        if (!IsFollowing || _centerQueued) return;
        _centerQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _centerQueued = false;
            if (IsFollowing && IsVisible) CenterCurrent();
        }));
    }
    private void CenterCurrent()
    {
        if (_rows.Length == 0 || LyricsScroll.ViewportHeight <= 0) return;
        LyricItems.UpdateLayout();
        int index = Math.Clamp(CurrentIndex, 0, _rows.Length - 1);
        if (LyricItems.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement row) return;
        double target = row.TransformToAncestor(ScrollContent).Transform(new Point()).Y + row.ActualHeight / 2 - LyricsScroll.ViewportHeight / 2;
        target = Math.Clamp(target, 0, LyricsScroll.ScrollableHeight);
        double from = LyricsScroll.VerticalOffset;
        StopAnimation();
        BeginAnimation(AnimatedOffsetProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(280))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd });
    }
    private void StopAnimation()
    {
        double offset = LyricsScroll.VerticalOffset;
        // Set the base value before removing an animation to avoid jumping back to its start.
        SetValue(AnimatedOffsetProperty, offset);
        BeginAnimation(AnimatedOffsetProperty, null);
    }
    private void PauseFollowing()
    {
        if (_rows.Length == 0) return;
        StopAnimation();
        SetValue(IsFollowingPropertyKey, false);
        _resumeTimer.Stop();
        _resumeTimer.Start();
    }
    public void ResumeFollowing()
    {
        _resumeTimer.Stop();
        SetValue(IsFollowingPropertyKey, true);
        QueueCenter();
    }
    private void Resume_Click(object sender, RoutedEventArgs e) => ResumeFollowing();
    private void Lyric_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LyricRow row })
        {
            SeekRequested?.Invoke(row.Time);
            ResumeFollowing();
            e.Handled = true;
        }
    }
    private void Lyrics_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        PauseFollowing();
        int lines = SystemParameters.WheelScrollLines;
        double step = lines < 0 ? LyricsScroll.ViewportHeight : lines * 28;
        LyricsScroll.ScrollToVerticalOffset(LyricsScroll.VerticalOffset - e.Delta / 120d * step);
        e.Handled = true;
    }
    private void Lyrics_MouseDown(object sender, MouseButtonEventArgs e) => PauseFollowing();
    private void Lyrics_TouchDown(object sender, TouchEventArgs e) => PauseFollowing();
    private void Lyrics_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) PauseFollowing();
    }
    private void Lyrics_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportHeightChange == 0 && e.ViewportWidthChange == 0) return;
        TopSpace.Height = BottomSpace.Height = Math.Max(0, LyricsScroll.ViewportHeight / 2 - 28);
        QueueCenter();
    }
    private void View_Loaded(object sender, RoutedEventArgs e) => ResumeFollowing();
    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        _resumeTimer.Stop();
        StopAnimation();
    }

    public sealed class LyricRow(LyricLine line) : ObservableObject
    {
        public TimeSpan Time => line.Time;
        public string DisplayText => string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text;
        public string Timestamp => $"{(int)line.Time.TotalMinutes:00}:{line.Time.Seconds:00}";
        private bool _isCurrent;
        public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    }
}
