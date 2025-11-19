using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace OverlayApp.Converters;

public class ImagePathConverter : IValueConverter
{
    private static readonly string RepoPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArcRaidersHelper", "arcdata", "repo");

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string input || string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        // If input is a URL, extract the filename
        var filename = input;
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            filename = Path.GetFileName(uri.LocalPath);
        }

        // Check common locations
        var pathsToCheck = new[]
        {
            Path.Combine(RepoPath, "images", "items", filename),
            Path.Combine(RepoPath, "items", "images", filename),
            Path.Combine(RepoPath, "images", "workshop", filename),
            Path.Combine(RepoPath, "images", filename),
            Path.Combine(RepoPath, "items", filename)
        };

        foreach (var path in pathsToCheck)
        {
            if (File.Exists(path))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(path);
                    bitmap.EndInit();
                    return bitmap;
                }
                catch
                {
                    // Ignore load errors
                }
            }
        }

        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
