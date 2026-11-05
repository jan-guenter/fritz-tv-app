using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FlaUI.Core.Definitions;
using Xunit;

namespace FritzTV.Windows.UiTests;

[SupportedOSPlatform("windows")]
internal static class WarmupCoverAssertions
{
    public static async Task<object> VerifyPausedCoverLifecycleAsync(NativeDesktopSession session, CancellationToken cancellationToken)
    {
        nint owner = (nint)session.Window.Properties.NativeWindowHandle.Value;
        if (owner == 0)
        {
            using var process = Process.GetProcessById(session.Application.ProcessId);
            owner = process.MainWindowHandle;
        }

        Assert.NotEqual(0, owner);
        NativeOwnedWindow? cover = null;
        await NativeDesktopSession.WaitAsync(() =>
        {
            Rectangle surface = session.Find("VideoSurface").BoundingRectangle;
            cover = NativeOwnedWindows.Read(owner, session.Application.ProcessId)
                .FirstOrDefault(window => BoundsMatch(window.Bounds, surface) || BoundsMatch(window.ClientBounds, surface));
            return cover is not null;
        }, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);
        Assert.NotNull(cover);
        nint handle = cover.Handle;
        Rectangle original = session.Window.BoundingRectangle;
        try
        {
            session.Window.Move(original.Left + 8, original.Top + 8);
            await NativeDesktopSession.WaitAsync(() =>
            {
                Rectangle moved = session.Window.BoundingRectangle;
                NativeOwnedWindow? current = FindByHandle(session, owner, handle);
                Rectangle surface = session.Find("VideoSurface").BoundingRectangle;
                return moved.Location != original.Location && current is not null
                    && (BoundsMatch(current.Bounds, surface) || BoundsMatch(current.ClientBounds, surface));
            }, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);

            session.Window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Minimized);
            await WaitHiddenAsync(handle, cancellationToken).ConfigureAwait(true);
            session.Window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
            await NativeDesktopSession.WaitAsync(() => FindByHandle(session, owner, handle) is { } restored
                && (BoundsMatch(restored.Bounds, session.Find("VideoSurface").BoundingRectangle)
                    || BoundsMatch(restored.ClientBounds, session.Find("VideoSurface").BoundingRectangle)),
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

    public static Task AssertHiddenAsync(NativeDesktopSession session, object handle, CancellationToken cancellationToken)
    {
        Assert.False(session.Application.HasExited);
        return WaitHiddenAsync((nint)handle, cancellationToken);
    }

    private static Task WaitHiddenAsync(nint handle, CancellationToken cancellationToken) =>
        NativeDesktopSession.WaitAsync(() => !NativeOwnedWindows.IsVisible(handle), TimeSpan.FromSeconds(5), cancellationToken);

    private static NativeOwnedWindow? FindByHandle(NativeDesktopSession session, nint owner, nint handle) =>
        NativeOwnedWindows.Read(owner, session.Application.ProcessId).FirstOrDefault(window => window.Handle == handle);

    private static bool BoundsMatch(Rectangle actual, Rectangle expected) => expected.Width > 0 && expected.Height > 0
        && Math.Abs(actual.Left - expected.Left) <= 3 && Math.Abs(actual.Top - expected.Top) <= 3
        && Math.Abs(actual.Width - expected.Width) <= 3 && Math.Abs(actual.Height - expected.Height) <= 3;
}
