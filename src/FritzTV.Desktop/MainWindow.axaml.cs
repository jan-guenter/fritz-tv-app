using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FritzTV.Desktop.Services;
using FritzTV.Desktop.ViewModels;
using LibVLCSharp.Avalonia;

namespace FritzTV.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly LibVlcPlaybackController? _player;
    private VideoView? _videoView;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel, LibVlcPlaybackController player)
        : this()
    {
        DataContext = viewModel;
        _player = player;
        _player.NativePlayerCreated += OnNativePlayerCreated;
        Closed += (_, _) => _player.NativePlayerCreated -= OnNativePlayerCreated;
    }

    public event EventHandler? QuitRequested;

    private void OnQuitClick(object? sender, RoutedEventArgs args) => QuitRequested?.Invoke(this, EventArgs.Empty);

    private void OnNativePlayerCreated(object? sender, EventArgs args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _videoView ??= new VideoView();
            _videoView.MediaPlayer = _player?.NativePlayer;
            VideoHost.Content = _videoView;
        });
    }

    private void OnFullscreenClick(object? sender, RoutedEventArgs args) => WindowState = WindowState == WindowState.FullScreen
        ? WindowState.Normal
        : WindowState.FullScreen;
}
