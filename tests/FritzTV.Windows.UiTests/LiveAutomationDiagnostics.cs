using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Exceptions;

namespace FritzTV.Windows.UiTests;

[SupportedOSPlatform("windows")]
internal static class LiveAutomationDiagnostics
{
    public static void Write(NativeDesktopSession session) => Console.WriteLine(Capture(session));

    public static string Capture(NativeDesktopSession session)
    {
        var located = new Dictionary<string, object?>(StringComparer.Ordinal);
        var walked = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string id in new[] { "Timeline", "Status", "Play", "Quit", "ReceptionDiagnostics" })
        {
            located[id] = Read(() => session.Window.FindFirstDescendant(condition => condition.ByAutomationId(id)) is { } element
                ? Describe(element, depth: 0) : null);
            walked[id] = Read(() => session.TryFind(id) is { } element ? Describe(element, depth: 0) : null);
        }

        return JsonSerializer.Serialize(new
        {
            Type = "LiveAutomationDiagnostics",
            Utc = DateTimeOffset.UtcNow,
            Root = Describe(session.Window, depth: 0),
            FindById = located,
            ControlViewById = walked,
            ControlView = Read(() => Walk(session.Window)),
        });
    }

    private static List<Dictionary<string, object?>> Walk(AutomationElement root)
    {
        var walker = root.Automation.TreeWalkerFactory.GetControlViewWalker();
        var pending = new Queue<(AutomationElement Element, int Depth)>();
        var nodes = new List<Dictionary<string, object?>>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0 && nodes.Count < 100)
        {
            var current = pending.Dequeue();
            nodes.Add(Describe(current.Element, current.Depth));
            AutomationElement? child = walker.GetFirstChild(current.Element);
            while (child is not null && nodes.Count + pending.Count < 100)
            {
                pending.Enqueue((child, current.Depth + 1));
                child = walker.GetNextSibling(child);
            }
        }

        return nodes;
    }

    private static Dictionary<string, object?> Describe(AutomationElement element, int depth)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Depth"] = depth,
            ["Name"] = Read(() => element.Name),
            ["AutomationId"] = Read(() => element.AutomationId),
            ["ControlType"] = Read(() => element.ControlType.ToString()),
            ["ClassName"] = Read(() => element.ClassName),
            ["Hwnd"] = Read(() => element.Properties.NativeWindowHandle.Value.ToString(CultureInfo.InvariantCulture)),
            ["ProcessId"] = Read(() => element.Properties.ProcessId.Value),
            ["Bounds"] = Read(() => element.BoundingRectangle.ToString()),
            ["IsOffscreen"] = Read(() => element.IsOffscreen),
        };
        if (Read(() => element.Patterns.RangeValue.IsSupported) is true)
        {
            result["RangeValue"] = Read(() => element.Patterns.RangeValue.Pattern.Value.Value);
            result["RangeMinimum"] = Read(() => element.Patterns.RangeValue.Pattern.Minimum.Value);
            result["RangeMaximum"] = Read(() => element.Patterns.RangeValue.Pattern.Maximum.Value);
        }

        return result;
    }

    private static object? Read(Func<object?> getter)
    {
        try
        {
            object? value = getter();
            return value is string text && text.Length > 512 ? text[..512] : value;
        }
        catch (Exception exception) when (exception is COMException or FlaUIException or InvalidOperationException or TimeoutException)
        {
            string error = exception.GetType().Name + ": " + exception.Message;
            return error.Length > 512 ? error[..512] : error;
        }
    }
}
