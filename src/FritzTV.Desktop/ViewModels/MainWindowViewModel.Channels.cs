using CommunityToolkit.Mvvm.Input;
using FritzTV.Core.Models;

namespace FritzTV.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    [RelayCommand]
    private Task MoveChannelUpAsync(CancellationToken cancellationToken) => MoveChannelAsync(-1, cancellationToken);

    [RelayCommand]
    private Task MoveChannelDownAsync(CancellationToken cancellationToken) => MoveChannelAsync(1, cancellationToken);

    private async Task MoveChannelAsync(int direction, CancellationToken cancellationToken)
    {
        TvChannel? selected = SelectedChannel;
        int visibleIndex = selected is null ? -1 : Channels.IndexOf(selected);
        int next = visibleIndex + direction;
        if (selected is null || visibleIndex < 0 || next < 0 || next >= Channels.Count)
        {
            return;
        }

        TvChannel neighbor = Channels[next];
        _allChannels.RemoveAll(channel => string.Equals(channel.Id, selected.Id, StringComparison.Ordinal));
        int index = _allChannels.FindIndex(channel => string.Equals(channel.Id, neighbor.Id, StringComparison.Ordinal));
        _allChannels.Insert(index + (direction > 0 ? 1 : 0), selected);
        try
        {
            if (_data is not null)
            {
                await _data.SetChannelOrderAsync(_allChannels.Select(channel => channel.Id), cancellationToken).ConfigureAwait(true);
                IReadOnlyList<TvChannel> channels = await _data.GetChannelsAsync(cancellationToken).ConfigureAwait(true);
                _allChannels.Clear();
                _allChannels.AddRange(channels);
            }

            FilterChannels();
            SelectedChannel = _allChannels.FirstOrDefault(channel => string.Equals(channel.Id, selected.Id, StringComparison.Ordinal));
            StatusText = Text["ChannelOrderSaved"];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText = Text["ChannelOrderFailed"] + " " + exception.Message;
        }
    }
}
