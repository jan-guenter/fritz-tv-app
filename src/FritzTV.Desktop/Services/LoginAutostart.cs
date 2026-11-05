using System.Text;
using Microsoft.Win32;

namespace FritzTV.Desktop.Services;

public static class LoginAutostart
{
    public static async Task ApplyAsync(bool enabled, CancellationToken cancellationToken)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Application executable path is unavailable.");
        if (OperatingSystem.IsWindows())
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled)
            {
                key.SetValue("FritzTV", "\"" + executable + "\" --background", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue("FritzTV", throwOnMissingValue: false);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart");
            string path = Path.Combine(directory, "fritz-tv.desktop");
            if (enabled)
            {
                Directory.CreateDirectory(directory);
                string escaped = executable.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal);
                string tryExecutable = executable.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
                string content = "[Desktop Entry]\nType=Application\nName=FRITZ TV\nTryExec=" + tryExecutable + "\nExec=\"" + escaped + "\" --background\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";
                await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                File.Delete(path);
            }
        }
    }
}
