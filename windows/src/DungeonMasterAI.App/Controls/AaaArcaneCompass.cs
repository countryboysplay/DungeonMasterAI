using System.Windows;
using System.Windows.Media;

namespace DungeonMasterAI.App.Controls;

public sealed class AaaArcaneCompass : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 8) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size * .43;
        var gold = new SolidColorBrush(Color.FromArgb(145, 154, 119, 58));
        var softGold = new SolidColorBrush(Color.FromArgb(82, 126, 99, 52));
        var blue = new SolidColorBrush(Color.FromRgb(58, 151, 222));
        var blueGlow = new SolidColorBrush(Color.FromArgb(58, 74, 163, 230));
        gold.Freeze(); softGold.Freeze(); blue.Freeze(); blueGlow.Freeze();

        dc.DrawEllipse(null, new Pen(softGold, 1), center, radius, radius);
        dc.DrawEllipse(null, new Pen(gold, 1), center, radius * .72, radius * .72);
        dc.DrawEllipse(null, new Pen(softGold, .8), center, radius * .48, radius * .48);

        for (var i = 0; i < 16; i++)
        {
            var angle = i * Math.PI / 8 - Math.PI / 2;
            var major = i % 4 == 0;
            var inner = radius * (major ? .50 : .68);
            var outer = radius * (major ? .96 : .88);
            var p1 = new Point(center.X + Math.Cos(angle) * inner, center.Y + Math.Sin(angle) * inner);
            var p2 = new Point(center.X + Math.Cos(angle) * outer, center.Y + Math.Sin(angle) * outer);
            dc.DrawLine(new Pen(major ? gold : softGold, major ? 1.25 : .75), p1, p2);
        }

        DrawDiamondRay(dc, center, radius * .76, 0, gold);
        DrawDiamondRay(dc, center, radius * .76, Math.PI / 2, gold);

        dc.DrawEllipse(blueGlow, null, center, radius * .30, radius * .30);
        var gem = new StreamGeometry();
        using (var ctx = gem.Open())
        {
            ctx.BeginFigure(new Point(center.X, center.Y - radius * .23), true, true);
            ctx.LineTo(new Point(center.X + radius * .19, center.Y), true, false);
            ctx.LineTo(new Point(center.X, center.Y + radius * .23), true, false);
            ctx.LineTo(new Point(center.X - radius * .19, center.Y), true, false);
        }
        gem.Freeze();
        dc.DrawGeometry(blue, new Pen(new SolidColorBrush(Color.FromRgb(116, 191, 239)), 1), gem);

        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(95, 210, 231, 247)), null,
            new Point(center.X - radius * .055, center.Y - radius * .07), radius * .055, radius * .035);
    }

    private static void DrawDiamondRay(DrawingContext dc, Point center, double length, double rotation, Brush brush)
    {
        var perp = rotation + Math.PI / 2;
        var tip1 = new Point(center.X + Math.Cos(rotation) * length, center.Y + Math.Sin(rotation) * length);
        var tip2 = new Point(center.X - Math.Cos(rotation) * length, center.Y - Math.Sin(rotation) * length);
        var side1 = new Point(center.X + Math.Cos(perp) * length * .12, center.Y + Math.Sin(perp) * length * .12);
        var side2 = new Point(center.X - Math.Cos(perp) * length * .12, center.Y - Math.Sin(perp) * length * .12);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(tip1, false, true);
            ctx.LineTo(side1, true, false);
            ctx.LineTo(tip2, true, false);
            ctx.LineTo(side2, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(brush, 1), geometry);
    }
}
