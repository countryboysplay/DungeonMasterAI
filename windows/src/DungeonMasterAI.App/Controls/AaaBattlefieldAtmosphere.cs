using System.Windows;
using System.Windows.Media;

namespace DungeonMasterAI.App.Controls;

public sealed class AaaBattlefieldAtmosphere : FrameworkElement
{
    public AaaBattlefieldAtmosphere()
    {
        IsHitTestVisible = false;
        Opacity = 0.78;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);

        var wash = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(36, 45, 39, 31), 0),
                new GradientStop(Color.FromArgb(18, 16, 20, 22), 0.48),
                new GradientStop(Color.FromArgb(42, 8, 11, 14), 1)
            }
        };
        dc.DrawRectangle(wash, null, bounds);

        const double tile = 54;
        var mortar = new Pen(new SolidColorBrush(Color.FromArgb(58, 110, 103, 89)), 0.8);
        for (double y = 0; y < ActualHeight + tile; y += tile)
        {
            var row = (int)(y / tile);
            var offset = row % 2 == 0 ? 0 : tile / 2;
            for (double x = -tile + offset; x < ActualWidth + tile; x += tile)
            {
                var shade = ((int)((x + tile) / tile) + row) % 3;
                var fill = new SolidColorBrush(shade switch
                {
                    0 => Color.FromArgb(25, 111, 99, 78),
                    1 => Color.FromArgb(19, 77, 75, 68),
                    _ => Color.FromArgb(16, 129, 112, 82)
                });
                dc.DrawRectangle(fill, mortar, new Rect(x, y, tile, tile));
            }
        }

        DrawCrack(dc, ActualWidth * 0.18, ActualHeight * 0.19, 1.0);
        DrawCrack(dc, ActualWidth * 0.62, ActualHeight * 0.31, 0.85);
        DrawCrack(dc, ActualWidth * 0.74, ActualHeight * 0.73, 0.72);
        DrawTorchGlow(dc, ActualWidth * 0.08, ActualHeight * 0.28, Math.Min(ActualWidth, ActualHeight) * 0.22);
        DrawTorchGlow(dc, ActualWidth * 0.91, ActualHeight * 0.63, Math.Min(ActualWidth, ActualHeight) * 0.25);
        DrawTorchGlow(dc, ActualWidth * 0.33, ActualHeight * 0.92, Math.Min(ActualWidth, ActualHeight) * 0.18);

        var topVignette = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(118, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.34),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.72),
                new GradientStop(Color.FromArgb(96, 0, 0, 0), 1)
            }
        };
        dc.DrawRectangle(topVignette, null, bounds);
    }

    private static void DrawTorchGlow(DrawingContext dc, double x, double y, double radius)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(74, 236, 143, 57), 0),
                new GradientStop(Color.FromArgb(34, 189, 92, 35), 0.38),
                new GradientStop(Color.FromArgb(0, 96, 48, 20), 1)
            }
        };
        dc.DrawEllipse(brush, null, new Point(x, y), radius, radius * 0.78);
    }

    private static void DrawCrack(DrawingContext dc, double x, double y, double scale)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(76, 15, 15, 14)), 1.2);
        var points = new[]
        {
            new Point(x, y),
            new Point(x + 18 * scale, y + 11 * scale),
            new Point(x + 8 * scale, y + 28 * scale),
            new Point(x + 29 * scale, y + 39 * scale),
            new Point(x + 22 * scale, y + 57 * scale)
        };
        for (var i = 0; i < points.Length - 1; i++)
            dc.DrawLine(pen, points[i], points[i + 1]);
        dc.DrawLine(pen, points[1], new Point(points[1].X + 14 * scale, points[1].Y - 8 * scale));
        dc.DrawLine(pen, points[3], new Point(points[3].X + 18 * scale, points[3].Y + 5 * scale));
    }
}
