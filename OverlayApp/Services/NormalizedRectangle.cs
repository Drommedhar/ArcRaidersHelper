using System;
using System.Drawing;

namespace OverlayApp.Services;

internal readonly struct NormalizedRectangle
{
    public NormalizedRectangle(double left, double top, double width, double height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public double Left { get; }
    public double Top { get; }
    public double Width { get; }
    public double Height { get; }

    public Rectangle ToPixelRectangle(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return Rectangle.Empty;
        }

        var left = (int)Math.Round(Left * pixelWidth, MidpointRounding.AwayFromZero);
        var top = (int)Math.Round(Top * pixelHeight, MidpointRounding.AwayFromZero);
        var width = (int)Math.Round(Width * pixelWidth, MidpointRounding.AwayFromZero);
        var height = (int)Math.Round(Height * pixelHeight, MidpointRounding.AwayFromZero);

        var rect = new Rectangle(left, top, width, height);
        var bounds = new Rectangle(0, 0, pixelWidth, pixelHeight);
        return Rectangle.Intersect(rect, bounds);
    }
}
