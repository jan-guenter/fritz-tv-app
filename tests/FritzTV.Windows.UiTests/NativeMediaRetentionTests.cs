using Xunit;

namespace FritzTV.Windows.UiTests;

public sealed class NativeMediaRetentionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DefaultAndCiRunsNeverCopyTransportMedia(bool enabled, bool isCi)
    {
        string root = CreateRoot();
        try
        {
            string profile = Path.Combine(root, "profile");
            Directory.CreateDirectory(profile);
            File.WriteAllBytes(Path.Combine(profile, "capture.ts"), [0x47]);
            string artifacts = Path.Combine(root, "private-artifacts");
            Assert.Equal(0, NativeMediaRetention.Copy(profile, artifacts, enabled, isCi));
            Assert.False(Directory.Exists(artifacts));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExplicitLocalRetentionCopiesTheIsolatedProfileWithRelativeDirectories()
    {
        string root = CreateRoot();
        try
        {
            string profile = Path.Combine(root, "profile");
            string recordings = Path.Combine(profile, "recordings");
            Directory.CreateDirectory(recordings);
            File.WriteAllBytes(Path.Combine(recordings, "capture.ts"), [0x47, 0x11]);
            File.WriteAllText(Path.Combine(recordings, "capture.mkv"), "synthetic ignored container");
            File.WriteAllText(Path.Combine(profile, "settings.json"), "synthetic ignored settings");
            string artifacts = Path.Combine(root, "private-artifacts");
            Assert.Equal(3, NativeMediaRetention.Copy(profile, artifacts, enabled: true, isCi: false));
            string[] retained = Directory.GetFiles(artifacts, "*", SearchOption.AllDirectories);
            Assert.Equal(3, retained.Length);
            string capture = Path.Combine(artifacts, "profile", "profile", "recordings", "capture.ts");
            Assert.Contains(capture, retained, StringComparer.Ordinal);
            Assert.Equal(new byte[] { 0x47, 0x11 }, File.ReadAllBytes(capture));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DestinationInsideProfileIsRejectedBeforeCopying()
    {
        string root = CreateRoot();
        try
        {
            string profile = Path.Combine(root, "profile");
            Directory.CreateDirectory(profile);
            Assert.Throws<InvalidOperationException>(() => NativeMediaRetention.Copy(profile, Path.Combine(profile, "artifacts"), enabled: true, isCi: false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "fritztv-media-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
