using System.Windows;

namespace TrackEditor.Localization;

public static class Loc
{
    public static string Get(string key) =>
        Application.Current.Resources[key] as string ?? $"[{key}]";

    public static string Get(string key, params object[] args) =>
        string.Format(Get(key), args);
}
