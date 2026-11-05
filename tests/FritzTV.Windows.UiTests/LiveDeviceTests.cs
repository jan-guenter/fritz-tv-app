using System.Runtime.Versioning;
using FlaUI.Core.AutomationElements;
using Xunit;

namespace FritzTV.Windows.UiTests;

[SupportedOSPlatform("windows")]
[Collection("Windows desktop")]
public sealed class LiveDeviceTests
{
    public static bool IsLiveDeviceEnabled => DesktopSmokeTests.IsUiAutomationEnabled
        && Uri.TryCreate(Environment.GetEnvironmentVariable("FRITZTV_LIVE_DEVICE"), UriKind.Absolute, out Uri? uri)
        && uri.Scheme is "http" or "https";

    [Fact(Skip = "Set FRITZTV_UI_APP and FRITZTV_LIVE_DEVICE in an unlocked Windows desktop session.", SkipUnless = nameof(IsLiveDeviceEnabled))]
    public async Task LiveChannelDecodesAndRecordingContinuesAfterViewingStops()
    {
        var session = new NativeDesktopSession();
        await using var lifetime = session.ConfigureAwait(true);
        session.Find("DescriptorUrl").AsTextBox().Text = Environment.GetEnvironmentVariable("FRITZTV_LIVE_DEVICE")!;
        session.Find("ImportDevice").AsButton().Invoke();
        ListBox channels = session.Find("Channels").AsListBox();
        await NativeDesktopSession.WaitAsync(() => channels.Items.Length > 0, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
        string channelName = Environment.GetEnvironmentVariable("FRITZTV_LIVE_CHANNEL") ?? "ZDF HD";
        session.Find("ChannelSearch").AsTextBox().Text = channelName;
        await NativeDesktopSession.WaitAsync(() => channels.Items.Any(item => string.Equals(item.Name, channelName, StringComparison.Ordinal)), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
        channels.Items.Single(item => string.Equals(item.Name, channelName, StringComparison.Ordinal)).Select();
        session.Find("Play").AsButton().Invoke();
        try
        {
            await NativeDesktopSession.WaitAsync(() => session.Window.FindFirstDescendant(condition => condition.ByAutomationId("Timeline")) is { } timeline && timeline.AsSlider().Value > 3, TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            LiveAutomationDiagnostics.Write(session);
            throw;
        }
        session.Find("Pause").AsButton().Invoke();
        await NativeDesktopSession.WaitAsync(() => session.Find("Pause").Name is "Resume" or "Fortsetzen", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        double paused = session.Find("Timeline").AsSlider().Value;
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.InRange(session.Find("Timeline").AsSlider().Value, paused - 0.5, paused + 0.5);
        session.Find("Rewind").AsButton().Invoke();
        await NativeDesktopSession.WaitAsync(() => session.Find("Timeline").AsSlider().Value < paused - 0.5, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
        double rewound = session.Find("Timeline").AsSlider().Value;
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.InRange(session.Find("Timeline").AsSlider().Value, rewound - 0.5, rewound + 0.5);
        session.Find("Pause").AsButton().Invoke();
        await NativeDesktopSession.WaitAsync(() => session.Find("Timeline").AsSlider().Value > rewound + 0.5, TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
        session.Find("GoLive").AsButton().Invoke();
        await VerifyIndependentRecordingAsync(session, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    private static async Task VerifyIndependentRecordingAsync(NativeDesktopSession session, CancellationToken cancellationToken)
    {
        session.Find("Details").Patterns.ExpandCollapse.Pattern.Expand();
        session.Find("RecordingsTab").Patterns.SelectionItem.Pattern.Select();
        session.Find("RecordInstant").AsButton().Invoke();
        string directory = Path.Combine(session.DataDirectory, "recordings");
        await NativeDesktopSession.WaitAsync(() => Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.ts").Any(), TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(true);
        string source = Directory.EnumerateFiles(directory, "*.ts").Single();
        session.Find("Stop").AsButton().Invoke();
        long before = new FileInfo(source).Length;
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(true);
        Assert.True(new FileInfo(source).Length > before, "Closing viewing must leave independent recording capture active.");
        session.Find("Recordings").AsListBox().Items[0].Select();
        session.Find("StopRecording").AsButton().Invoke();
        await NativeDesktopSession.WaitAsync(() => Directory.EnumerateFiles(directory, "*.mkv").Any() || Directory.EnumerateFiles(directory, "*.mka").Any(), TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(true);
    }
}
