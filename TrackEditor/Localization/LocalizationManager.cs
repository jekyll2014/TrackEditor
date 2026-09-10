using System.Windows;

namespace TrackEditor.Localization;

public static class LocalizationManager
{
    public static void Apply(string language)
    {
        string path = language switch
        {
            "ru" => "Localization/Strings.ru.xaml",
            "lt" => "Localization/Strings.lt.xaml",
            _    => "Localization/Strings.en.xaml",
        };

        var uri = new Uri($"pack://application:,,,/TrackEditor;component/{path}");
        var dict = new ResourceDictionary { Source = uri };

        var merged = Application.Current.Resources.MergedDictionaries;
        var old = merged.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("Localization/Strings.") == true);
        if (old != null) merged.Remove(old);
        merged.Add(dict);
    }
}
