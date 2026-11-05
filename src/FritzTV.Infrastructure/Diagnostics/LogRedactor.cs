using System.Text.RegularExpressions;

namespace FritzTV.Infrastructure.Diagnostics;

internal static partial class LogRedactor
{
    public static string Redact(string value) => Credentials().Replace(Secrets().Replace(Bearer().Replace(value, "${prefix}[REDACTED]"), "${prefix}[REDACTED]"), "${prefix}[REDACTED]@");

    [GeneratedRegex(@"(?i)(?<prefix>\b(?:bearer|basic)\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 100)]
    private static partial Regex Bearer();

    [GeneratedRegex("(?i)(?<prefix>(?:[?&\\s,;{\\\"']|^)(?:access[_-]?token|refresh[_-]?token|token|(?:x[-_])?api[_-]?key|password|authorization|client[_-]?secret)[\\\"']?\\s*[:=]\\s*[\\\"']?)[^\\s&\\\"',;}]+", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 100)]
    private static partial Regex Secrets();

    [GeneratedRegex(@"(?i)(?<prefix>https?://|rtsp://)[^/\s@:]+:[^/\s@]+@", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 100)]
    private static partial Regex Credentials();
}
