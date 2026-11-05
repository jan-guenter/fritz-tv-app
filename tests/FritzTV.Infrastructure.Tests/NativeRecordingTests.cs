using FritzTV.Core.Models;
using FritzTV.Infrastructure.Power;
using FritzTV.Infrastructure.Recording;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class NativeRecordingTests
{
    [Theory]
    [InlineData(ChannelKind.Television)]
    [InlineData(ChannelKind.Radio)]
    [Trait("Category", "NativeMedia")]
    public async Task SyntheticBroadcastRemuxesAndDecodesWithoutReencoding(ChannelKind kind)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("FRITZTV_NATIVE_MEDIA_TESTS"), "1", StringComparison.Ordinal))
        {
            Assert.Skip("Set FRITZTV_NATIVE_MEDIA_TESTS=1 with FFmpeg and MKVToolNix installed.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var runner = new MediaProcessRunner();
            var source = Path.Combine(directory, "synthetic broadcast.ts");
            var output = Path.ChangeExtension(source, kind == ChannelKind.Radio ? ".mka" : ".mkv");
            var generated = await runner.RunAsync("ffmpeg", SyntheticArguments(source, kind), cancellationToken);
            Assert.True(generated.ExitCode == 0, generated.StandardError);
            var channel = new TvChannel("native", Guid.NewGuid(), "Original synthetic fixture", kind, new Uri("rtsp://192.0.2.1/"), 42);
            var result = await new RecordingFinalizer(freeSpaceReserveBytes: 0).FinalizeAsync(source, output, channel, false, cancellationToken);
            Assert.True(File.Exists(result.OutputPath));
            var decoded = await runner.RunAsync("ffmpeg", ["-nostdin", "-hide_banner", "-v", "error", "-i", output, "-f", "null", "-"], cancellationToken);
            Assert.True(decoded.ExitCode == 0, decoded.StandardError);
            Assert.Empty(decoded.StandardError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "NativeMedia")]
    public async Task NativeSleepRequestCanBeAcquiredAndReleased()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("FRITZTV_NATIVE_POWER_TESTS"), "1", StringComparison.Ordinal))
        {
            Assert.Skip("Set FRITZTV_NATIVE_POWER_TESTS=1 in a supported interactive desktop session.");
        }

        await using var inhibition = await new SystemPowerInhibitor().AcquireAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(inhibition);
    }

    private static List<string> SyntheticArguments(string source, ChannelKind kind)
    {
        var arguments = new List<string> { "-nostdin", "-hide_banner", "-v", "error" };
        if (kind == ChannelKind.Television)
        {
            arguments.AddRange(["-f", "lavfi", "-i", "testsrc2=size=160x90:rate=10"]);
        }

        arguments.AddRange(["-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000"]);
        if (kind == ChannelKind.Television)
        {
            arguments.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-threads", "1", "-g", "10", "-bf", "0", "-c:a", "mp2"]);
        }
        else
        {
            arguments.AddRange(["-c:a", "aac", "-mpegts_flags", "latm"]);
        }

        arguments.AddRange(["-t", "2", "-mpegts_service_id", "42", "-f", "mpegts", source]);
        return arguments;
    }
}
