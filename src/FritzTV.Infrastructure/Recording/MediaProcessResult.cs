namespace FritzTV.Infrastructure.Recording;

public sealed record MediaProcessResult(int ExitCode, string StandardOutput, string StandardError);
