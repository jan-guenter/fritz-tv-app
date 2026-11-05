using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FritzTV.Windows.UiTests;

[SupportedOSPlatform("windows")]
internal static partial class NativeOwnedWindows
{
    public static unsafe IReadOnlyList<NativeOwnedWindow> Read(nint owner, int processId, bool includeHidden = false)
    {
        var state = new Enumeration(owner, processId, includeHidden);
        GCHandle context = GCHandle.Alloc(state);
        nint previous = SetThreadDpiAwarenessContext(-4);
        try
        {
            _ = EnumWindows(&Collect, GCHandle.ToIntPtr(context));
            if (state.Error is { } error)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }

            return state.Windows;
        }
        finally
        {
            if (previous != 0)
            {
                _ = SetThreadDpiAwarenessContext(previous);
            }

            context.Free();
        }
    }

    public static bool IsVisible(nint handle) => IsWindowVisible(handle) && !IsIconic(handle);

    public static string Describe(nint handle)
    {
        _ = GetWindowThreadProcessId(handle, out uint processId);
        return FormattableString.Invariant($"HWND={handle} owner={GetWindow(handle, 4)} pid={processId} visible={IsWindowVisible(handle)} iconic={IsIconic(handle)}");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    [SuppressMessage("Design", "CA1031", Justification = "Managed callback errors are captured and rethrown after EnumWindows returns.")]
    private static int Collect(nint handle, nint context)
    {
        var state = (Enumeration)GCHandle.FromIntPtr(context).Target!;
        try
        {
            if (++state.Visited > 2048)
            {
                return 0;
            }

            _ = GetWindowThreadProcessId(handle, out uint processId);
            if (processId == state.ProcessId && GetWindow(handle, 4) == state.Owner && (state.IncludeHidden || IsVisible(handle)) && GetWindowRect(handle, out NativeRectangle rectangle))
            {
                Rectangle outer = rectangle.ToRectangle();
                Rectangle client = outer;
                var origin = new NativePoint();
                if (GetClientRect(handle, out NativeRectangle size) && ClientToScreen(handle, ref origin))
                {
                    client = new Rectangle(origin.X, origin.Y, size.Right - size.Left, size.Bottom - size.Top);
                }

                state.Windows.Add(new NativeOwnedWindow(handle, outer, client));
            }

            return 1;
        }
        catch (Exception exception)
        {
            state.Error = exception;
            return 0;
        }
    }

    private sealed class Enumeration(nint owner, int processId, bool includeHidden)
    {
        public nint Owner { get; } = owner;
        public int ProcessId { get; } = processId;
        public bool IncludeHidden { get; } = includeHidden;
        public int Visited { get; set; }
        public Exception? Error { get; set; }
        public List<NativeOwnedWindow> Windows { get; } = [];
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint context);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint handle, uint command);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint handle, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint handle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint handle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint handle, out NativeRectangle rectangle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint handle, out NativeRectangle rectangle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint handle, ref NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}

