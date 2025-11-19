using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace OverlayApp.Converters;

public class RarityColorConverter : IValueConverter
{
    private static readonly SolidColorBrush CommonBrush = new(Color.FromRgb(176, 176, 176)); // Grey
    private static readonly SolidColorBrush UncommonBrush = new(Color.FromRgb(85, 255, 85)); // Green
    private static readonly SolidColorBrush RareBrush = new(Color.FromRgb(0, 170, 255)); // Blue
    private static readonly SolidColorBrush EpicBrush = new(Color.FromRgb(170, 0, 255)); // Purple
    private static readonly SolidColorBrush LegendaryBrush = new(Color.FromRgb(255, 165, 0)); // Orange
    private static readonly SolidColorBrush DefaultBrush = new(Colors.Transparent);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string rarity)
        {
            return DefaultBrush;
        }

        return rarity.ToLowerInvariant() switch
        {
            "common" => CommonBrush,
            "uncommon" => UncommonBrush,
            "rare" => RareBrush,
            "epic" => EpicBrush,
            "legendary" => LegendaryBrush,
            _ => DefaultBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
