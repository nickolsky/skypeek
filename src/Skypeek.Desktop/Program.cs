using Avalonia;
using Avalonia.Media;
using Skypeek.Desktop.Platform;

namespace Skypeek.Desktop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // A second start only asks the running instance to show itself (before any UI is created).
        var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            Console.Error.WriteLine("Skypeek is already running; asked it to show its window.");
            SingleInstance.SignalExisting("show");
            return;
        }
        App.StartupInstance = instance;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // A tray app: no Dock icon on macOS (the menu bar icon opens it).
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .With(new X11PlatformOptions { WmClass = "Skypeek" });
        // SKYPEEK_DEBUG=1: Avalonia's warnings (platform, bindings) on stderr for troubleshooting.
        builder = Environment.GetEnvironmentVariable("SKYPEEK_DEBUG") == "1"
            ? builder.LogToTextWriter(Console.Error, Avalonia.Logging.LogEventLevel.Warning)
            : builder.LogToTrace();
        // Linux desktops differ in their default UI font; Inter (bundled) keeps the layout the same everywhere.
        if (OperatingSystem.IsLinux())
            builder = builder.With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" });
        return builder;
    }
}
