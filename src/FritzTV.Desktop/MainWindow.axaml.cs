using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FritzTV.Desktop.Services;
using FritzTV.Desktop.ViewModels;
using LibVLCSharp.Avalonia;

namespace FritzTV.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly LibVlcPlaybackController? _player;
    private VideoView? _videoView;
    private Window? _warmupCover;

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => FitWorkArea();
        LayoutUpdated += (_, _) => PositionWarmupCover();
        PositionChanged += (_, _) => PositionWarmupCover();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty && DataContext is MainWindowViewModel viewModel)
            {
                viewModel.IsFullscreen = WindowState == WindowState.FullScreen;
            }

            if (args.Property == IsVisibleProperty || args.Property == WindowStateProperty)
            {
                UpdateNativeOutput();
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
            _warmupCover?.Close();
        };
    }

    public event EventHandler? QuitRequested;

    public void DetachPlayer()
    {
        _warmupCover?.Hide();
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
        Avalonia.Automation.AutomationProperties.SetAutomationId(_videoView, "VideoSurface");
        _videoView.MediaPlayer = _player?.NativePlayer;
        VideoHost.Content = _videoView;
        UpdateLayout();
    }

    private void OnNativeOutputChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateNativeOutput();
        }
        else
        {
            Dispatcher.UIThread.Post(UpdateNativeOutput);
        }
    }

    private void UpdateNativeOutput()
    {
        if (_videoView is null)
        {
            return;
        }

        // Never hide NativeControlHost while VLC owns its HWND/X11 window. A separate
        // opaque cover also works on X11 without a transparent-window compositor.
        if (_player?.IsOutputReady == true || _player?.GetSnapshot() is null || !IsVisible || WindowState == WindowState.Minimized)
        {
            _warmupCover?.Hide();
        }
        else
        {
            _warmupCover ??= new Window
            {
                WindowDecorations = WindowDecorations.None,
                Background = Brushes.Black,
                CanResize = false,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            PositionWarmupCover();
            if (!_warmupCover.IsVisible)
            {
                _warmupCover.Show(this);
            }
        }

        _player?.ApplyOutputState();
    }

    private void PositionWarmupCover()
    {
        if (_warmupCover is not null && _videoView is not null)
        {
            _warmupCover.Width = Math.Max(1, _videoView.Bounds.Width);
            _warmupCover.Height = Math.Max(1, _videoView.Bounds.Height);
            _warmupCover.Position = _videoView.PointToScreen(default);
        }
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
