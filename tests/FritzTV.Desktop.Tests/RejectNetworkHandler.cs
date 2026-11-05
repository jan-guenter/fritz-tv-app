namespace FritzTV.Desktop.Tests;

internal sealed class RejectNetworkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("This test must not issue network requests.");
}
