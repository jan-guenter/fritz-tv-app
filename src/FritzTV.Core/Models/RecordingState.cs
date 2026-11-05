namespace FritzTV.Core.Models;

public enum RecordingState
{
    Scheduled,
    Recording,
    Finalizing,
    Completed,
    Partial,
    Failed,
    Missed,
    Conflict,
}

