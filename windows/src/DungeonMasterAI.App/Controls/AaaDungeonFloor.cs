using System.Windows;
using System.Windows.Media;

namespace DungeonMasterAI.App.Controls;

/// <summary>
/// Deterministic flagstone dungeon floor used as the substrate beneath the tactical
/// grid. Salvaged from the abandoned r50 battlefield atmosphere experiment, which
/// layered the stonework *over* the grid and fought its cell lines. Here the stone is
/// drawn first, so grid lines, terrain fills, and the <see cref="AaaCombatAtmosphere"/>
/// lighting pass all composite on top of it.
/// </summary>
internal static class AaaDungeonFloor
{
    // Deliberately non-square and unrelated to the grid cell pitch (clamped 22-58 px)
    // so the masonry courses never moire against the tactical squares.
    private const double CourseWidth = 76;
    private const double CourseHeight = 38;

    public static void Render(DrawingContext dc, Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Opaque substrate: same tone the flat battlefield used, so overall
        // brightness is unchanged and combatant tokens keep their contrast.
        var substrate = new SolidColorBrush(Color.FromRgb(20, 23, 28));
        substrate.Freeze();
        dc.DrawRectangle(substrate, null, bounds);

        var wash = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(34, 45, 39, 31), 0),
                new GradientStop(Color.FromArgb(16, 16, 20, 22), 0.48),
                new GradientStop(Color.FromArgb(40, 8, 11, 14), 1)
            }
        };
        wash.Freeze();
        dc.DrawRectangle(wash, null, bounds);

        DrawCourses(dc, bounds);

        DrawCrack(dc, bounds.Width * 0.18, bounds.Height * 0.19, 1.0);
        DrawCrack(dc, bounds.Width * 0.62, bounds.Height * 0.31, 0.85);
        DrawCrack(dc, bounds.Width * 0.74, bounds.Height * 0.73, 0.72);
    }

    private static void DrawCourses(DrawingContext dc, Rect bounds)
    {
        var mortar = new Pen(new SolidColorBrush(Color.FromArgb(46, 110, 103, 89)), 0.8);
        mortar.Freeze();

        var shades = new Brush[3];
        shades[0] = new SolidColorBrush(Color.FromArgb(22, 111, 99, 78));
        shades[1] = new SolidColorBrush(Color.FromArgb(17, 77, 75, 68));
        shades[2] = new SolidColorBrush(Color.FromArgb(14, 129, 112, 82));
        foreach (var shade in shades) shade.Freeze();

        dc.PushClip(new RectangleGeometry(bounds));
        var row = 0;
        for (var y = bounds.Top - CourseHeight; y < bounds.Bottom + CourseHeight; y += CourseHeight)
        {
            // Running bond: alternate rows are offset by half a stone.
            var offset = row % 2 == 0 ? 0 : CourseWidth / 2;
            var column = 0;
            for (var x = bounds.Left - CourseWidth + offset; x < bounds.Right + CourseWidth; x += CourseWidth)
            {
                dc.DrawRectangle(shades[(column + row) % 3], mortar, new Rect(x, y, CourseWidth, CourseHeight));
                column++;
            }

            row++;
        }

        dc.Pop();
    }

    private static void DrawCrack(DrawingContext dc, double x, double y, double scale)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(68, 15, 15, 14)), 1.2);
        pen.Freeze();

        var points = new[]
        {
            new Point(x, y),
            new Point(x + 18 * scale, y + 11 * scale),
            new Point(x + 8 * scale, y + 28 * scale),
            new Point(x + 29 * scale, y + 39 * scale),
            new Point(x + 22 * scale, y + 57 * scale)
        };

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points.Skip(1).ToList(), true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);

        // Two short spurs so the fracture does not read as a single polyline.
        dc.DrawLine(pen, points[1], new Point(points[1].X + 14 * scale, points[1].Y - 8 * scale));
        dc.DrawLine(pen, points[3], new Point(points[3].X + 18 * scale, points[3].Y + 5 * scale));
    }
}
