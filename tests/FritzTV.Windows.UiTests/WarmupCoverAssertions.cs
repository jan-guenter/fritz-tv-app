using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Xunit;

namespace FritzTV.Windows.UiTests;

[SupportedOSPlatform("windows")]
internal static class WarmupCoverAssertions
{
    public static async Task<object> VerifyPausedCoverLifecycleAsync(NativeDesktopSession session, CancellationToken cancellationToken)
    {
        using var automation = new UIA3Automation();
        Window? cover = null;
        await NativeDesktopSession.WaitAsync(() =>
        {
            Rectangle surface = session.Find("VideoSurface").BoundingRectangle;
            cover = session.Application.GetAllTopLevelWindows(automation)
                .FirstOrDefault(window => !window.IsOffscreen && BoundsMatch(window.BoundingRectangle, surface));
            return cover is not null;
        }, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);
        Assert.NotNull(cover);
        object handle = cover.Properties.NativeWindowHandle.Value;
        Rectangle original = session.Window.BoundingRectangle;
        try
        {
            session.Window.Move(original.Left + 8, original.Top + 8);
            await NativeDesktopSession.WaitAsync(() =>
            {
                Rectangle moved = session.Window.BoundingRectangle;
                Window? current = FindByHandle(session, automation, handle);
                return moved.Location != original.Location && current is { IsOffscreen: false }
                    && BoundsMatch(current.BoundingRectangle, session.Find("VideoSurface").BoundingRectangle);
            }, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);

            session.Window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Minimized);
            await WaitHiddenAsync(session, automation, handle, cancellationToken).ConfigureAwait(true);
            session.Window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
            await NativeDesktopSession.WaitAsync(() => FindByHandle(session, automation, handle) is { IsOffscreen: false } restored
                && BoundsMatch(restored.BoundingRectangle, session.Find("VideoSurface").BoundingRectangle),
                TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            try
            {
                session.Window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
                session.Window.Move(original.Left, original.Top);
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException or Win32Exception)
            {
                Trace.TraceWarning("Could not restore the desktop test window after checking its warmup cover: " + exception.Message);
            }
        }

        return handle;
    }

    public static async Task AssertHiddenAsync(NativeDesktopSession session, object handle, CancellationToken cancellationToken)
    {
        using var automation = new UIA3Automation();
        await WaitHiddenAsync(session, automation, handle, cancellationToken).ConfigureAwait(true);
    }

    private static Task WaitHiddenAsync(NativeDesktopSession session, UIA3Automation automation, object handle, CancellationToken cancellationToken) =>
        NativeDesktopSession.WaitAsync(() => FindByHandle(session, automation, handle) is null or { IsOffscreen: true },
            TimeSpan.FromSeconds(5), cancellationToken);

    private static Window? FindByHandle(NativeDesktopSession session, UIA3Automation automation, object handle) =>
        session.Application.GetAllTopLevelWindows(automation).FirstOrDefault(window => window.Properties.NativeWindowHandle.Value.Equals(handle));

    private static bool BoundsMatch(Rectangle actual, Rectangle expected) => expected.Width > 0 && expected.Height > 0
        && Math.Abs(actual.Left - expected.Left) <= 3 && Math.Abs(actual.Top - expected.Top) <= 3
        && Math.Abs(actual.Width - expected.Width) <= 3 && Math.Abs(actual.Height - expected.Height) <= 3;
}
