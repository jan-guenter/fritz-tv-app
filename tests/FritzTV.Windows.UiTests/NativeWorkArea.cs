using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FritzTV.Windows.UiTests;

[SupportedOSPlatform("windows")]
internal static partial class NativeWorkArea
{
    public static Rectangle Read()
    {
        if (!ReadWorkArea(0x0030, 0, out NativeRectangle rectangle, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadWorkArea(uint action, uint parameter, out NativeRectangle rectangle, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }
}
