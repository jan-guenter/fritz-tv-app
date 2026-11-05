using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using FritzTV.Core.Models;
using FritzTV.Desktop.Localization;
using FritzTV.Desktop.Services;
using FritzTV.Desktop.ViewModels;
using FritzTV.Infrastructure.Devices;
using FritzTV.Infrastructure.Persistence;
using FritzTV.Infrastructure.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FritzTV.Desktop;

public sealed partial class App : Application
{
    private bool _quitting;
    private bool _hostDisposed;
    private bool _osShutdown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Startup += async (_, _) => await StartDesktopAsync(desktop).ConfigureAwait(true);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task<IHost> CreateHostAsync()
    {
        var repository = new AppRepository(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FritzTV", "app.db"));
        await repository.InitializeAsync().ConfigureAwait(true);
        AppSettings settings = await repository.GetSettingsAsync().ConfigureAwait(true);
        settings = settings with
        {
            RtpFirstPort = ReadPort("FRITZTV_RTP_FIRST_PORT", settings.RtpFirstPort),
            RtpLastPort = ReadPort("FRITZTV_RTP_LAST_PORT", settings.RtpLastPort),
        };
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
        builder.Services.AddSingleton<HttpClient>();
        builder.Services.AddSingleton<SatIpDeviceClient>();
        builder.Services.AddSingleton<SsdpDiscovery>();
        builder.Services.AddSingleton<UiText>();
        builder.Services.AddSingleton(repository);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton(_ => new SatIpSessionManager(settings.RtpFirstPort, settings.RtpLastPort));
        builder.Services.AddSingleton<LibVlcPlaybackController>();
        builder.Services.AddSingleton<IPlaybackController>(services => services.GetRequiredService<LibVlcPlaybackController>());
        builder.Services.AddSingleton<MainWindowViewModel>();
        return builder.Build();
    }

    private static int ReadPort(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
        && port is >= 1024 and <= 65535 ? port : fallback;

    private async Task StartDesktopAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            IHost host = await CreateHostAsync().ConfigureAwait(true);
            var viewModel = host.Services.GetRequiredService<MainWindowViewModel>();
            var player = host.Services.GetRequiredService<LibVlcPlaybackController>();
            var window = new MainWindow(viewModel, player);
            ConfigureLifetime(desktop, host, viewModel, window);
            await viewModel.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
            await host.StartAsync(CancellationToken.None).ConfigureAwait(true);
            viewModel.StartUpdates();
            if (!(desktop.Args?.Contains("--background", StringComparer.Ordinal) ?? false))
            {
                desktop.MainWindow = window;
                window.Show();
            }
        }
        catch (Exception exception)
        {
            ShowStartupFailure(desktop, exception.Message);
        }
    }

    private void ConfigureLifetime(IClassicDesktopStyleApplicationLifetime desktop, IHost host, MainWindowViewModel viewModel, MainWindow window)
    {
        desktop.ShutdownRequested += (_, args) =>
        {
            if (!_quitting)
            {
                _osShutdown = true;
                _quitting = true;
            }
        };
        window.Closing += async (_, args) =>
        {
            if (args.CloseReason == WindowCloseReason.OSShutdown)
            {
                _osShutdown = true;
                _quitting = true;
            }
            else if (!_quitting && args.CloseReason == WindowCloseReason.WindowClosing && !args.IsProgrammatic)
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
            if (!_hostDisposed && !_osShutdown)
            {
                Task.Run(async () => await DisposeHostAsync(host).ConfigureAwait(false)).GetAwaiter().GetResult();
            }
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

    private static void ShowStartupFailure(IClassicDesktopStyleApplicationLifetime desktop, string message)
    {
        var quit = new Button { Content = "Quit / Beenden", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        quit.Click += (_, _) => desktop.Shutdown(1);
        var window = new Window
        {
            Title = "FRITZ TV",
            Width = 650,
            Height = 240,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16,
                Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, quit },
            },
        };
        desktop.MainWindow = window;
        window.Show();
    }

    private async Task QuitAsync(IClassicDesktopStyleApplicationLifetime desktop, IHost host)
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        await host.StopAsync(CancellationToken.None).ConfigureAwait(true);
        await DisposeHostAsync(host).ConfigureAwait(true);
        _hostDisposed = true;
        desktop.Shutdown();
    }

    private static async Task DisposeHostAsync(IHost host)
    {
        if (host is IAsyncDisposable asynchronous)
        {
            await asynchronous.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            host.Dispose();
        }
    }
}
