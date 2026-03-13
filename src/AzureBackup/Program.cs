using Avalonia;
using System;
using System.IO;
using System.Threading.Tasks;

namespace AzureBackup;

sealed class Program
{
    /// <summary>
    /// Global crash-safe logger accessible from anywhere for last-resort logging.
    /// </summary>
    internal static CrashSafeLogger? Logger { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Initialize file logger first so it catches everything
        Logger = new CrashSafeLogger();
        Logger.Log($"Starting AzureBackup (args: [{string.Join(", ", args)}])");

        // Global unhandled exception handlers
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Logger.LogException(ex, "AppDomain.UnhandledException (fatal=" + e.IsTerminating + ")");
            }
            else
            {
                Logger.Log($"AppDomain.UnhandledException: non-Exception object: {e.ExceptionObject}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.LogException(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved(); // Prevent crash from unobserved task exceptions
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Logger.LogException(ex, "Main entry point");
            throw; // Re-throw so the OS knows the process crashed
        }
        finally
        {
            Logger.Log("Application exiting");
            Logger.Dispose();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
