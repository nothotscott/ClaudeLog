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
        // Before Cli.Run, because this one needs Avalonia and Core deliberately doesn't reference it.
        if (args.Contains("--startup")) return Startup();
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

    /// <summary>
    /// Boots the UI as far as it can go without showing a window, then exits.
    ///
    /// This is what CI runs against the packaged binary. `--selftest` proves the logic but never
    /// touches Avalonia, so a single-file bundle that failed to unpack its native libraries, or a
    /// window whose XAML no longer loads, would sail through every other check and only break on
    /// the machine that downloaded it. Setup initializes the platform; constructing MainWindow
    /// loads the whole window's XAML, styles and templates.
    /// </summary>
    private static int Startup()
    {
        Cli.AttachToParentConsole();

        try
        {
            BuildAvaloniaApp().SetupWithoutStarting();

            var window = new Views.MainWindow();
            var dialog = new Views.TextPrompt();

            Console.WriteLine($"ok  platform started, {window.GetType().Name} " +
                              $"({window.Width:0}x{window.Height:0}) and {dialog.GetType().Name} loaded");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"startup failed: {ex}");
            return 1;
        }
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
