using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
        Opened += (_, _) => FitWorkArea();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty && DataContext is MainWindowViewModel viewModel)
            {
                viewModel.IsFullscreen = WindowState == WindowState.FullScreen;
            }
        };
        TimelineSlider.AddHandler(PointerPressedEvent, OnTimelinePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        TimelineSlider.AddHandler(PointerReleasedEvent, OnTimelineReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && WindowState == WindowState.FullScreen)
            {
                WindowState = WindowState.Normal;
                args.Handled = true;
            }
        };
    }

    public MainWindow(MainWindowViewModel viewModel, LibVlcPlaybackController player)
        : this()
    {
        DataContext = viewModel;
        _player = player;
        _player.NativePlayerCreated += OnNativePlayerCreated;
        _player.NativeOutputChanged += OnNativeOutputChanged;
        Closed += (_, _) =>
        {
            _player.NativePlayerCreated -= OnNativePlayerCreated;
            _player.NativeOutputChanged -= OnNativeOutputChanged;
        };
    }

    public event EventHandler? QuitRequested;

    public void DetachPlayer()
    {
        if (_videoView is not null)
        {
            _videoView.MediaPlayer = null;
        }
    }

    private void FitWorkArea()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen)
        {
            return;
        }

        PixelRect area = screen.WorkingArea;
        double scale = screen.Scaling;
        double availableWidth = Math.Max(300, area.Width / scale - 32);
        double availableHeight = Math.Max(300, area.Height / scale - 64);
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Clamp(Width, MinWidth, availableWidth);
        Height = Math.Clamp(Height, MinHeight, availableHeight);
        Position = new PixelPoint(area.X + Math.Max(0, (area.Width - (int)(Width * scale)) / 2), area.Y + (int)(16 * scale));
    }

    private void OnQuitClick(object? sender, RoutedEventArgs args) => QuitRequested?.Invoke(this, EventArgs.Empty);

    private void OnNativePlayerCreated(object? sender, EventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            AttachVideo();
        }
        else
        {
            Dispatcher.UIThread.Post(AttachVideo);
        }
    }

    private void AttachVideo()
    {
        _videoView ??= new VideoView();
        _videoView.MediaPlayer = _player?.NativePlayer;
        _videoView.IsVisible = true;
        VideoHost.Content = _videoView;
        UpdateLayout();
    }

    private void OnNativeOutputChanged(object? sender, EventArgs args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_videoView is not null)
            {
                _videoView.IsVisible = _player?.IsOutputReady ?? false;
                _player?.ApplyOutputState();
            }
        });
    }

    private void OnTimelinePressed(object? sender, PointerPressedEventArgs args)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.BeginTimelineDrag();
        }
    }

    private void OnTimelineReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SeekCommand.Execute(null);
        }
    }

    private async void OnTimelineKeyUp(object? sender, KeyEventArgs args)
    {
        if (args.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.SeekCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    private void OnGuideNowClick(object? sender, RoutedEventArgs args)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ShowGuideToday();
            OnDetailsExpanded(sender, args);
        }
    }

    private void OnDetailsExpanded(object? sender, RoutedEventArgs args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                GuideTimelineScroller.Offset = new Vector(viewModel.GuideNowOffset, 0);
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnDetailsTabChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (sender is TabControl { SelectedIndex: 0 } && ReferenceEquals(sender, args.Source))
        {
            OnDetailsExpanded(sender, args);
        }
    }

    private void OnProgrammeTimelineClick(object? sender, RoutedEventArgs args)
    {
        if (sender is Control { DataContext: ProgrammeItem programme } && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SelectedProgramme = programme;
        }
    }

    private void OnFullscreenClick(object? sender, RoutedEventArgs args) => WindowState = WindowState == WindowState.FullScreen
        ? WindowState.Normal
        : WindowState.FullScreen;
}
