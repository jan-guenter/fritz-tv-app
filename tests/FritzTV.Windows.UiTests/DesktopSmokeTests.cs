using System.Diagnostics;
using System.Runtime.Versioning;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Xunit;

namespace FritzTV.Windows.UiTests;

[SupportedOSPlatform("windows")]
public sealed class DesktopSmokeTests
{
    public static bool IsUiAutomationEnabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FRITZTV_UI_APP"));

    [Fact(SkipUnless = nameof(IsUiAutomationEnabled))]
    public async Task DesktopLoadsAndRejectsInvalidDescriptor()
    {
        string executable = Environment.GetEnvironmentVariable("FRITZTV_UI_APP")!;
        using var application = Application.Launch(executable);
        using var automation = new UIA3Automation();
        try
        {
            Window window = application.GetMainWindow(automation, TimeSpan.FromSeconds(20)) ?? throw new InvalidOperationException("Main window did not appear.");
            Assert.NotNull(window);
            Assert.Equal("FRITZ TV", window.Title);
            TextBox descriptor = window.FindFirstDescendant(condition => condition.ByAutomationId("DescriptorUrl"))?.AsTextBox() ?? throw new InvalidOperationException("Descriptor input was not found.");
            descriptor.Text = "file:///invalid-descriptor";
            Button import = window.FindFirstDescendant(condition => condition.ByAutomationId("ImportDevice"))?.AsButton() ?? throw new InvalidOperationException("Import button was not found.");
            import.Invoke();
            bool validated = false;
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                string status = window.FindFirstDescendant(condition => condition.ByAutomationId("Status"))?.AsLabel()?.Text ?? string.Empty;
                if (status.Contains("HTTP", StringComparison.OrdinalIgnoreCase))
                {
                    validated = true;
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            Assert.True(validated, "Invalid descriptor should produce a visible validation message.");
        }
        finally
        {
            if (!application.HasExited)
            {
                application.Kill();
            }
        }
    }
}
