using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Xunit;

namespace FritzTV.Windows.UiTests;

[SupportedOSPlatform("windows")]
[Collection("Windows desktop")]
public sealed partial class NativeOwnedWindowsTests
{
    [Fact]
    public void NonactivatingOwnedWindowMetadataSurvivesMovement()
    {
        nint previous = SetThreadDpiAwarenessContext(-4);
        nint owner = 0;
        nint cover = 0;
        try
        {
            owner = Create(0, 10, 10, 220, 150);
            cover = Create(owner, 40, 50, 120, 60);
            IReadOnlyList<NativeOwnedWindow> windows = NativeOwnedWindows.Read(owner, Environment.ProcessId, includeHidden: true);
            Assert.True(windows.Count > 0, NativeOwnedWindows.Describe(owner) + "; " + NativeOwnedWindows.Describe(cover));
            NativeOwnedWindow first = Assert.Single(windows);
            Assert.Equal(cover, first.Handle);
            Assert.Equal(new Rectangle(40, 50, 120, 60), first.ClientBounds);
            Assert.True(MoveWindow(cover, 60, 70, 120, 60, false));
            Assert.Equal(new Rectangle(60, 70, 120, 60), Assert.Single(NativeOwnedWindows.Read(owner, Environment.ProcessId, includeHidden: true)).ClientBounds);
            Assert.Empty(NativeOwnedWindows.Read(owner, Environment.ProcessId + 1, includeHidden: true));
            if (Environment.UserInteractive)
            {
                _ = ShowWindow(cover, 0);
                Assert.False(NativeOwnedWindows.IsVisible(cover));
                Assert.Empty(NativeOwnedWindows.Read(owner, Environment.ProcessId));
                _ = ShowWindow(cover, 4);
                Assert.True(NativeOwnedWindows.IsVisible(cover));
                Assert.Equal(cover, Assert.Single(NativeOwnedWindows.Read(owner, Environment.ProcessId)).Handle);
            }
            else
            {
                // SSH session 0 has no visible desktop; real visibility is covered by the live helper.
                Assert.False(NativeOwnedWindows.IsVisible(cover));
                Assert.Empty(NativeOwnedWindows.Read(owner, Environment.ProcessId));
            }
        }
        finally
        {
            if (cover != 0)
            {
                _ = DestroyWindow(cover);
            }

            if (owner != 0)
            {
                _ = DestroyWindow(owner);
            }

            if (previous != 0)
            {
                _ = SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    private static nint Create(nint owner, int x, int y, int width, int height)
    {
        nint handle = CreateWindowEx(0x08000080, "STATIC", "FRITZ TV native metadata fixture", 0x90000000,
            x, y, width, height, owner, 0, 0, 0);
        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        // SSH may inherit STARTUPINFO's hidden first-show state. The second call
        // applies the fixture's explicit no-activation visibility request.
        _ = ShowWindow(handle, 4);
        _ = ShowWindow(handle, 4);
        return handle;
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveWindow(nint handle, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint handle, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint handle);
}
