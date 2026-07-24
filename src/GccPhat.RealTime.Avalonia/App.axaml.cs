using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GccPhat.RealTime.Audio;
using GccPhat.RealTime.Mvvm;

namespace GccPhat.RealTime;

public partial class App : Application
{
    // One shared platform/dispatcher/prompt for every MainWindow the app opens (the "New analysis
    // window" feature spawns more than one) — composition root for the whole process.
    public static IAudioPlatform CurrentPlatform { get; } = new PortAudioPlatform();
    public static IUiDispatcher CurrentDispatcher { get; } = new AvaloniaDispatcher();
    public static IUserPrompt CurrentPrompt { get; } = new AvaloniaUserPrompt();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            desktop.MainWindow = new MainWindow(CurrentPlatform, CurrentDispatcher, CurrentPrompt);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
