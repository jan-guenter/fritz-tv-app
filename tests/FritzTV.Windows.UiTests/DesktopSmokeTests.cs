using System.Diagnostics;
using System.Drawing;
using System.Runtime.Versioning;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using Xunit;

namespace FritzTV.Windows.UiTests;

[SupportedOSPlatform("windows")]
[Collection("Windows desktop")]
public sealed class DesktopSmokeTests
{
    public static bool IsUiAutomationEnabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FRITZTV_UI_APP"));

    [Fact(Skip = "Set FRITZTV_UI_APP in an unlocked Windows desktop session.", SkipUnless = nameof(IsUiAutomationEnabled))]
    public async Task DesktopFitsWorkAreaValidatesInputsAndSwitchesLanguage()
    {
        var session = new NativeDesktopSession();
        await using var lifetime = session.ConfigureAwait(true);
        Assert.Equal("FRITZ TV", session.Window.Title);
        Rectangle workArea = NativeWorkArea.Read();
        Assert.True(workArea.Contains(session.Window.BoundingRectangle), "The window must fit the guest work area, including its footer.");
        foreach (string id in new[] { "DescriptorUrl", "ImportDevice", "Play", "Pause", "Stop", "Language", "Quit" })
        {
            AutomationElement element = session.Find(id);
            Assert.False(element.IsOffscreen, id + " must remain visible at the VM display size.");
            Assert.True(session.Window.BoundingRectangle.Contains(element.BoundingRectangle), id + " must be inside the window.");
        }

        if (!string.Equals(session.Find("Play").Name, "Play", StringComparison.Ordinal))
        {
            session.Find("Language").AsToggleButton().Toggle();
            await NativeDesktopSession.WaitAsync(() => string.Equals(session.Find("Play").Name, "Play", StringComparison.Ordinal), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        session.Find("DescriptorUrl").AsTextBox().Text = "file:///invalid-descriptor";
        session.Find("ImportDevice").AsButton().Invoke();
        await NativeDesktopSession.WaitAsync(() => session.Find("Status").AsLabel().Text.Contains("HTTP", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        session.Find("Language").AsToggleButton().Toggle();
        await NativeDesktopSession.WaitAsync(() => string.Equals(session.Find("Play").Name, "Wiedergabe", StringComparison.Ordinal), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        session.Find("Language").AsToggleButton().Toggle();
        await NativeDesktopSession.WaitAsync(() => string.Equals(session.Find("Play").Name, "Play", StringComparison.Ordinal), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        session.Find("Details").Patterns.ExpandCollapse.Pattern.Expand();
        Assert.False(session.Find("Quit").IsOffscreen);
        Assert.True(workArea.Contains(session.Find("Quit").BoundingRectangle));
    }

    [Fact(Skip = "Set FRITZTV_UI_APP in an unlocked Windows desktop session.", SkipUnless = nameof(IsUiAutomationEnabled))]
    public async Task ClosingToTrayAndSecondLaunchActivateTheSameProcess()
    {
        var session = new NativeDesktopSession();
        await using var lifetime = session.ConfigureAwait(true);
        int primary = session.Application.ProcessId;
        session.Window.Close();
        await NativeDesktopSession.WaitAsync(() => session.Window.IsOffscreen, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.False(session.Application.HasExited);
        using var secondary = Application.Launch(new ProcessStartInfo(Environment.GetEnvironmentVariable("FRITZTV_UI_APP")!) { UseShellExecute = false });
        await NativeDesktopSession.WaitAsync(() => secondary.HasExited && !session.Window.IsOffscreen, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(primary, session.Application.ProcessId);
        Assert.False(session.Find("Quit").IsOffscreen);
        session.Find("Quit").AsButton().Invoke();
        await NativeDesktopSession.WaitAsync(() => session.Application.HasExited, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
}
