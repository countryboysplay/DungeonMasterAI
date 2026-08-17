using System.Windows;
using System.Windows.Media;

namespace DungeonMasterAI.App.Controls;

public sealed class AaaCombatAtmosphere : FrameworkElement
{
    public AaaCombatAtmosphere()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = Math.Max(1, ActualWidth);
        var height = Math.Max(1, ActualHeight);
        var bounds = new Rect(0, 0, width, height);

        var vignette = new RadialGradientBrush
        {
            Center = new Point(.5, .48),
            GradientOrigin = new Point(.5, .48),
            RadiusX = .78,
            RadiusY = .76,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), .38),
                new GradientStop(Color.FromArgb(26, 1, 4, 6), .72),
                new GradientStop(Color.FromArgb(125, 0, 2, 4), 1)
            }
        };
        dc.DrawRectangle(vignette, null, bounds);

        DrawTorchGlow(dc, width * .09, height * .22, width * .18, Color.FromArgb(78, 231, 137, 52));
        DrawTorchGlow(dc, width * .93, height * .29, width * .17, Color.FromArgb(82, 236, 139, 48));
        DrawTorchGlow(dc, width * .10, height * .82, width * .15, Color.FromArgb(62, 214, 117, 41));
        DrawTorchGlow(dc, width * .91, height * .83, width * .16, Color.FromArgb(68, 222, 125, 42));

        var wallPen = new Pen(new SolidColorBrush(Color.FromArgb(72, 149, 127, 92)), 1.1);
        wallPen.Freeze();
        var crackPen = new Pen(new SolidColorBrush(Color.FromArgb(52, 174, 157, 127)), .85);
        crackPen.Freeze();

        DrawBrokenWall(dc, wallPen, 0, height * .08, width * .08, height * .78, true);
        DrawBrokenWall(dc, wallPen, width * .92, height * .06, width * .08, height * .82, false);

        var cracks = new[]
        {
            new[] { new Point(.18,.14), new Point(.21,.20), new Point(.19,.27), new Point(.24,.32) },
            new[] { new Point(.78,.10), new Point(.75,.18), new Point(.79,.24), new Point(.76,.31) },
            new[] { new Point(.14,.69), new Point(.19,.65), new Point(.22,.71), new Point(.28,.68) },
            new[] { new Point(.72,.76), new Point(.78,.72), new Point(.82,.78), new Point(.88,.74) }
        };
        foreach (var crack in cracks)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(crack[0].X * width, crack[0].Y * height), false, false);
                context.PolyLineTo(crack.Skip(1).Select(p => new Point(p.X * width, p.Y * height)).ToList(), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, crackPen, geometry);
        }

        var framePen = new Pen(new SolidColorBrush(Color.FromArgb(78, 142, 112, 62)), 1);
        framePen.Freeze();
        dc.DrawRoundedRectangle(null, framePen, new Rect(2.5, 2.5, width - 5, height - 5), 3, 3);
    }

    private static void DrawTorchGlow(DrawingContext dc, double x, double y, double diameter, Color color)
    {
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(color, 0),
                new GradientStop(Color.FromArgb((byte)(color.A / 3), color.R, color.G, color.B), .42),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)
            }
        };
        dc.DrawEllipse(brush, null, new Point(x, y), diameter * .5, diameter * .5);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(145, 255, 191, 92)), null, new Point(x, y), 2.2, 4.2);
    }

    private static void DrawBrokenWall(DrawingContext dc, Pen pen, double x, double y, double width, double height, bool left)
    {
        var points = new List<Point>();
        const int segments = 13;
        for (var i = 0; i <= segments; i++)
        {
            var t = i / (double)segments;
            var edge = left ? x + width : x;
            var wobble = ((i * 37) % 11 - 5) * width * .018;
            points.Add(new Point(edge + wobble, y + t * height));
        }
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points.Skip(1).ToList(), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
