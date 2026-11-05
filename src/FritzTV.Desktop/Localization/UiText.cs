using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace FritzTV.Desktop.Localization;

public sealed class UiText : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new("FritzTV.Desktop.Resources.Strings", typeof(UiText).Assembly);
    private CultureInfo _culture = string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "de", StringComparison.Ordinal)
        ? CultureInfo.GetCultureInfo("de")
        : CultureInfo.GetCultureInfo("en");

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Resources.GetString(key, _culture) ?? key;

    public bool IsGerman => string.Equals(_culture.TwoLetterISOLanguageName, "de", StringComparison.Ordinal);

    public void SetLanguage(bool german)
    {
        _culture = CultureInfo.GetCultureInfo(german ? "de" : "en");
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsGerman)));
    }
}
