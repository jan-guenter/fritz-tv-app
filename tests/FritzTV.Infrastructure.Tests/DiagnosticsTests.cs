using System.Text.Json;
using FritzTV.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class DiagnosticsTests
{
    private static readonly Action<ILogger, string, Exception?> WriteInformation = LoggerMessage.Define<string>(LogLevel.Information, new EventId(12, "Synthetic"), "{Message}");

    [Fact]
    public async Task FlushPersistsUtcStructuredErrorsAndRedactsCommonCredentialForms()
    {
        string path = Path.Combine(Path.GetTempPath(), "fritztv-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var provider = new RotatingFileLoggerProvider(new RotatingFileLoggerOptions { DirectoryPath = path });
            ILogger logger = provider.CreateLogger("Synthetic");
            WriteInformation(logger, "rtsp://name:hidden@192.0.2.1/test?token=hidden2 Authorization: Bearer hidden3 password=hidden4 Basic hidden6 X-Api-Key: hidden7", new IOException("api_key=hidden5"));
            await provider.FlushAsync(TestContext.Current.CancellationToken);
            string line = await ReadSharedAsync(Path.Combine(path, "fritz-tv.log"), TestContext.Current.CancellationToken);
            Assert.DoesNotContain("hidden", line, StringComparison.Ordinal);
            Assert.Contains("REDACTED", line, StringComparison.Ordinal);
            using var record = JsonDocument.Parse(line);
            Assert.Equal("Synthetic", record.RootElement.GetProperty("Category").GetString());
            Assert.Equal(12, record.RootElement.GetProperty("EventId").GetInt32());
            Assert.Equal(TimeSpan.Zero, record.RootElement.GetProperty("Utc").GetDateTimeOffset().Offset);
            Assert.Contains("IOException", record.RootElement.GetProperty("Exception").GetString(), StringComparison.Ordinal);
            Assert.Null(provider.LastError);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task RotationBoundsDiskUsageAndRetainsTheNewestEntries()
    {
        string path = Path.Combine(Path.GetTempPath(), "fritztv-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var provider = new RotatingFileLoggerProvider(new RotatingFileLoggerOptions { DirectoryPath = path, MaxFileBytes = 4096, RetainedFileCount = 3, QueueCapacity = 8 }))
            {
                ILogger logger = provider.CreateLogger("Rotation");
                for (int index = 0; index < 100; index++)
                {
                    WriteInformation(logger, index.ToString(System.Globalization.CultureInfo.InvariantCulture) + new string('x', 150), null);
                    await provider.FlushAsync(TestContext.Current.CancellationToken);
                }
            }

            string[] files = Directory.GetFiles(path);
            Assert.Equal(3, files.Length);
            Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 4096));
            string current = await File.ReadAllTextAsync(Path.Combine(path, "fritz-tv.log"), TestContext.Current.CancellationToken);
            Assert.Contains("99xxx", current, StringComparison.Ordinal);
            foreach (string line in current.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var record = JsonDocument.Parse(line);
                Assert.Equal("Information", record.RootElement.GetProperty("Level").GetString());
            }
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task InaccessibleLogDirectoryReportsFailureWithoutThrowingFromTheCaller()
    {
        string path = Path.Combine(Path.GetTempPath(), "fritztv-logs-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, "Synthetic file prevents directory creation.", TestContext.Current.CancellationToken);
        try
        {
            await using var provider = new RotatingFileLoggerProvider(new RotatingFileLoggerOptions { DirectoryPath = path });
            WriteInformation(provider.CreateLogger("Failure"), "The logging call remains bounded.", null);
            await Assert.ThrowsAnyAsync<IOException>(() => provider.FlushAsync(TestContext.Current.CancellationToken));
            Assert.NotNull(provider.LastError);
            Assert.True(provider.DroppedMessages >= 1);
        }
        finally
        {
            File.Delete(path);
        }
    }
    [Fact]
    public async Task SaturationDropsMessagesAndShutdownDrainsAcceptedEntries()
    {
        string path = Path.Combine(Path.GetTempPath(), "fritztv-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new RotatingFileLoggerProvider(new RotatingFileLoggerOptions { DirectoryPath = path, QueueCapacity = 1 });
            ILogger logger = provider.CreateLogger("Saturation");
            Parallel.For(0, 10000, index => WriteInformation(logger, index.ToString(System.Globalization.CultureInfo.InvariantCulture), null));
            await provider.DisposeAsync();
            long accepted = File.ReadLines(Path.Combine(path, "fritz-tv.log")).LongCount();
            Assert.True(provider.DroppedMessages > 0);
            Assert.Equal(10000, accepted + provider.DroppedMessages);
            Assert.Null(provider.LastError);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task<string> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(source);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

}
