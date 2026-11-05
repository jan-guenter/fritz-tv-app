using Avalonia.Headless;

namespace FritzTV.Desktop.Tests;

public sealed class HeadlessFixture : IAsyncDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

    public ValueTask DisposeAsync() => Session.DisposeAsync();
}
