using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using FritzTV.Desktop.Localization;
using FritzTV.Desktop.Services;
using FritzTV.Desktop.ViewModels;
using FritzTV.Infrastructure.Devices;
using FritzTV.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FritzTV.Desktop;

public sealed partial class App : Application
{
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ConfigureDesktop(desktop, CreateHost());
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IHost CreateHost()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<HttpClient>();
        builder.Services.AddSingleton<SatIpDeviceClient>();
        builder.Services.AddSingleton<SsdpDiscovery>();
        builder.Services.AddSingleton<UiText>();
        builder.Services.AddSingleton(new AppRepository(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FritzTV", "app.db")));
        builder.Services.AddSingleton<LibVlcPlaybackController>();
        builder.Services.AddSingleton<IPlaybackController>(services => services.GetRequiredService<LibVlcPlaybackController>());
        builder.Services.AddSingleton<MainWindowViewModel>();
        return builder.Build();
    }

    private void ConfigureDesktop(IClassicDesktopStyleApplicationLifetime desktop, IHost host)
    {
        var viewModel = host.Services.GetRequiredService<MainWindowViewModel>();
        var player = host.Services.GetRequiredService<LibVlcPlaybackController>();
        var window = new MainWindow(viewModel, player);
        if (!(desktop.Args?.Contains("--background", StringComparer.Ordinal) ?? false))
        {
            desktop.MainWindow = window;
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.Startup += async (_, _) =>
        {
            try
            {
                await viewModel.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
                await host.StartAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                viewModel.ReportStartupError(exception.Message);
            }
        };
        window.Closing += async (_, args) =>
        {
            if (!_quitting)
            {
                args.Cancel = true;
                await viewModel.StopAsync(CancellationToken.None).ConfigureAwait(true);
                window.Hide();
            }
        };
        window.QuitRequested += async (_, _) => await QuitAsync(desktop, host).ConfigureAwait(true);
        TrayIcon tray = CreateTray(desktop, window, viewModel.Text, host);
        TrayIcon.SetIcons(this, new TrayIcons { tray });
        desktop.Exit += (_, _) =>
        {
            tray.Dispose();
            host.Dispose();
        };
    }

    private TrayIcon CreateTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, UiText text, IHost host)
    {
        var show = new NativeMenuItem("FRITZ TV");
        show.Click += (_, _) =>
        {
            desktop.MainWindow = window;
            window.Show();
            window.Activate();
        };
        var quit = new NativeMenuItem(text["Quit"]);
        quit.Click += async (_, _) => await QuitAsync(desktop, host).ConfigureAwait(true);
        return new TrayIcon
        {
            ToolTipText = "FRITZ TV",
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://FritzTV.Desktop/Assets/fritz-tv.ico"))),
            Menu = new NativeMenu { Items = { show, new NativeMenuItemSeparator(), quit } },
            IsVisible = true,
        };
    }

    private async Task QuitAsync(IClassicDesktopStyleApplicationLifetime desktop, IHost host)
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        await host.StopAsync(CancellationToken.None).ConfigureAwait(true);
        desktop.Shutdown();
    }
}
