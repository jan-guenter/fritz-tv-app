using Avalonia;
using FritzTV.Desktop.Services;

namespace FritzTV.Desktop;

internal static class Program
{
    internal static SingleInstanceActivation? Activation { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        Activation = SingleInstanceActivation.CreateOrActivateAsync("FritzTV", CancellationToken.None).GetAwaiter().GetResult();
        if (Activation is null)
        {
            return 0;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            if (Application.Current is App app)
            {
                app.FinishExitAsync().GetAwaiter().GetResult();
            }

            Activation.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
