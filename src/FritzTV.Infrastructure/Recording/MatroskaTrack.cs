namespace FritzTV.Infrastructure.Recording;

public sealed record MatroskaTrack(int Id, string Type, string Codec, int? ProgrammeNumber);
