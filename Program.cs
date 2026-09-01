using Avalonia;
using ClaudeLog.Core;

namespace ClaudeLog;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant
    // code before AppMain is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        if (Cli.IsHeadless(args)) return Cli.Run(args);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("unhandled exception", e.ExceptionObject as Exception ?? new Exception("unknown"));

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("fatal", ex);
            return 1;
        }

        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
