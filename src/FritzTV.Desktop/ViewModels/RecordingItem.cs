using System.Globalization;
using FritzTV.Core.Models;
using FritzTV.Desktop.Localization;

namespace FritzTV.Desktop.ViewModels;

public sealed class RecordingItem(RecordingInstance recording, UiText text)
{
    public RecordingInstance Recording { get; } = recording;

    public string Title => Recording.Title;

    public string State => text["RecordingState" + Recording.State];

    public string Time => Recording.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + " – "
        + (Recording.End == DateTimeOffset.MaxValue ? text["UntilStopped"] : Recording.End.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

    public bool CanStop => Recording.State == RecordingState.Recording;

    public bool CanExtend => Recording.End != DateTimeOffset.MaxValue && Recording.State is RecordingState.Scheduled or RecordingState.Conflict or RecordingState.Recording;

    public bool CanCancel => Recording.State is RecordingState.Scheduled or RecordingState.Conflict;

    public bool CanRetry => Recording.State is RecordingState.Partial or RecordingState.Failed or RecordingState.Finalizing;

    public bool CanPlay => File.Exists(Recording.OutputPath) || File.Exists(Recording.SourcePath);

    public string Diagnostic
    {
        get
        {
            string? error = Recording.Error;
            string? warning = Recording.Warning;
            if (string.IsNullOrWhiteSpace(error))
            {
                return warning ?? string.Empty;
            }

            return string.IsNullOrWhiteSpace(warning) ? error : error + Environment.NewLine + warning;
        }
    }
}
