namespace FritzTV.Windows.UiTests;

internal static class NativeMediaRetention
{
    public static int Copy(string dataDirectory, string? artifactsDirectory, bool enabled, bool isCi)
    {
        if (!enabled || isCi || !Directory.Exists(dataDirectory))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(artifactsDirectory) || !Path.IsPathFullyQualified(artifactsDirectory))
        {
            throw new InvalidOperationException("Media retention requires FRITZTV_UI_ARTIFACTS to name an absolute private directory outside the isolated profile.");
        }

        string relativeDestination = Path.GetRelativePath(dataDirectory, artifactsDirectory);
        if (!Path.IsPathFullyQualified(relativeDestination) && !string.Equals(relativeDestination, "..", StringComparison.Ordinal)
            && !relativeDestination.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Retained media must be outside the profile that test cleanup deletes.");
        }

        string destination = Path.Combine(artifactsDirectory, Path.GetFileName(dataDirectory), "profile");
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        int copied = 0;
        foreach (string source in Directory.EnumerateFiles(dataDirectory, "*", options))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(dataDirectory, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            copied++;
        }

        return copied;
    }
}
