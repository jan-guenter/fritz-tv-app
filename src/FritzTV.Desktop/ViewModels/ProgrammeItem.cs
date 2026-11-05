using FritzTV.Core.Models;

namespace FritzTV.Desktop.ViewModels;

public sealed record ProgrammeItem(Programme Programme)
{
    public string Title => Programme.Title;

    public string Description => Programme.Description;

    public DateTime LocalStart => Programme.Start.LocalDateTime;

    public DateTime LocalEnd => Programme.End.LocalDateTime;
}
