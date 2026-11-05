using System.Drawing;

namespace FritzTV.Windows.UiTests;

internal sealed record NativeOwnedWindow(nint Handle, Rectangle Bounds, Rectangle ClientBounds);
