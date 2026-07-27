using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TrackEditor;

/// <summary>
/// Maps an Expander's <c>IsExpanded</c> to a <see cref="GridLength"/>: expanded rows take the remaining
/// space (star) so their content (a list or grid) fills; collapsed rows shrink to <c>Auto</c> so only the
/// expander header remains. Lets the left-panel Tracks/Points sections collapse like the Statistics panel.
/// </summary>
public sealed class BoolToRowHeightConverter : IValueConverter
{
    private static readonly GridLength Star = new(1, GridUnitType.Star);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Star : GridLength.Auto;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
