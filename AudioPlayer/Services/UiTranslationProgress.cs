using System.Windows.Threading;

namespace AudioPlayer.Services;

public sealed class UiTranslationProgress(Dispatcher dispatcher, Action<TranslationProgress> callback) : IProgress<TranslationProgress>
{
    public void Report(TranslationProgress value)
    {
        if (!dispatcher.HasShutdownStarted) _ = dispatcher.BeginInvoke(new Action(() => callback(value)));
    }
}
