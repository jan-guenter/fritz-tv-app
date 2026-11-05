using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FritzTV.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    [ObservableProperty] private bool _isFullscreen;

    public Thickness ShellMargin => new(IsFullscreen ? 0 : 22);

    public double ShellRowSpacing => IsFullscreen ? 0 : 16;

    public double ContentColumnSpacing => IsFullscreen ? 0 : 18;

    public double PlayerRowSpacing => IsFullscreen ? 0 : 12;

    public GridLength ChannelColumnWidth => new(IsFullscreen ? 0 : 290);

    public CornerRadius VideoCorners => new(IsFullscreen ? 0 : 12);

    public GridLength VideoRowHeight => DetailsExpanded && !IsFullscreen ? GridLength.Auto : new GridLength(1, GridUnitType.Star);

    public bool ShowAuxiliaryControls => !DetailsExpanded && !IsFullscreen;

    public bool ShowTimeline => HasPlayback && !IsFullscreen;

    public bool CanToggleFullscreen => HasPlayback || IsFullscreen;

    public string FullscreenButtonText => Text[IsFullscreen ? "ExitFullscreen" : "Fullscreen"];

    partial void OnIsFullscreenChanged(bool value)
    {
        if (value)
        {
            DetailsExpanded = false;
        }

        OnPropertyChanged(nameof(ShellMargin));
        OnPropertyChanged(nameof(ShellRowSpacing));
        OnPropertyChanged(nameof(ContentColumnSpacing));
        OnPropertyChanged(nameof(PlayerRowSpacing));
        OnPropertyChanged(nameof(ChannelColumnWidth));
        OnPropertyChanged(nameof(VideoCorners));
        OnPropertyChanged(nameof(VideoRowHeight));
        OnPropertyChanged(nameof(ShowAuxiliaryControls));
        OnPropertyChanged(nameof(ShowTimeline));
        OnPropertyChanged(nameof(FullscreenButtonText));
        OnPropertyChanged(nameof(CanToggleFullscreen));
    }
}
