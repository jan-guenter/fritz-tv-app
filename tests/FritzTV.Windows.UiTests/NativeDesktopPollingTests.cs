using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Xunit;

namespace FritzTV.Windows.UiTests;

[SupportedOSPlatform("windows")]
[SuppressMessage("Usage", "CA2201:Do not raise reserved exception types", Justification = "This fixture injects the exact COM HRESULTs emitted by Windows UIA to verify transient-provider recovery and failure propagation.")]
public sealed class NativeDesktopPollingTests
{
    [Fact]
    public async Task RebuiltAccessibilityTreeCanRecoverWithinTheOriginalDeadline()
    {
        int attempts = 0;
        await NativeDesktopSession.WaitAsync(() =>
        {
            if (++attempts == 1)
            {
                throw new COMException("Synthetic provider rebuild.", unchecked((int)0x80040201));
            }

            return true;
        }, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task PersistentProviderOutageFailsAtTheOriginalDeadline()
    {
        int attempts = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => NativeDesktopSession.WaitAsync(() =>
        {
            attempts++;
            throw new COMException("Synthetic persistent provider outage.", unchecked((int)0x80040201));
        }, TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.InRange(attempts, 1, 3);
    }

    [Fact]
    public async Task UnrelatedComFailureRemainsVisibleImmediately()
    {
        var expected = new COMException("Synthetic unrelated COM failure.", unchecked((int)0x80070005));
        int attempts = 0;
        COMException actual = await Assert.ThrowsAsync<COMException>(() => NativeDesktopSession.WaitAsync(() =>
        {
            attempts++;
            throw expected;
        }, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Same(expected, actual);
        Assert.Equal(1, attempts);
    }
}
