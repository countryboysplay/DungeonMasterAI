using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DungeonMasterAI.App.Controls;

public enum AaaIconKind
{
    Menu,
    Home,
    LivePlay,
    Combat,
    Characters,
    World,
    Maps,
    Quests,
    Rules,
    Import,
    Settings,
    Play,
    Note,
    Dice,
    Spark,
    Location,
    Shield,
    Heart,
    Calendar,
    Save,
    Timeline
}

public sealed class AaaVectorIcon : Control
{
    private static readonly IReadOnlyDictionary<AaaIconKind, Geometry> Geometries =
        new Dictionary<AaaIconKind, Geometry>
        {
            [AaaIconKind.Menu] = Geometry.Parse("M4,7 L20,7 M4,12 L20,12 M4,17 L20,17"),
            [AaaIconKind.Home] = Geometry.Parse("M3,11 L12,3 L21,11 M5.5,9.5 L5.5,21 L10,21 L10,15 L14,15 L14,21 L18.5,21 L18.5,9.5"),
            [AaaIconKind.LivePlay] = Geometry.Parse("M12,9 A3,3 0 1 0 12,15 A3,3 0 1 0 12,9 M7.5,6.5 A7,7 0 0 0 7.5,17.5 M16.5,6.5 A7,7 0 0 1 16.5,17.5 M4.5,4 A11,11 0 0 0 4.5,20 M19.5,4 A11,11 0 0 1 19.5,20"),
            [AaaIconKind.Combat] = Geometry.Parse("M5,3 L19,17 M3.5,5.5 L6.5,2.5 M17.5,20.5 L21,17 M19,3 L5,17 M20.5,5.5 L17.5,2.5 M6.5,20.5 L3,17"),
            [AaaIconKind.Characters] = Geometry.Parse("M9,10 A3,3 0 1 0 9,4 A3,3 0 1 0 9,10 M3.5,20 C3.8,14.8 6,12.5 9,12.5 C12,12.5 14.2,14.8 14.5,20 M17,11 A2.5,2.5 0 1 0 17,6 A2.5,2.5 0 1 0 17,11 M15.5,13 C18.6,12.4 21,14.8 21,19"),
            [AaaIconKind.World] = Geometry.Parse("M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 M2.5,12 L21.5,12 M12,2 C8,6 8,18 12,22 M12,2 C16,6 16,18 12,22"),
            [AaaIconKind.Maps] = Geometry.Parse("M3,5 L9,3 L15,5 L21,3 L21,19 L15,21 L9,19 L3,21 Z M9,3 L9,19 M15,5 L15,21"),
            [AaaIconKind.Quests] = Geometry.Parse("M6,3 L18,3 L18,21 L6,21 Z M9,7 L15,7 M9,11 L15,11 M9,15 L13,15 M4,6 L6,6 M4,18 L6,18"),
            [AaaIconKind.Rules] = Geometry.Parse("M3,5 C7,3 10,4 12,6 C14,4 17,3 21,5 L21,20 C17,18 14,19 12,21 C10,19 7,18 3,20 Z M12,6 L12,21"),
            [AaaIconKind.Import] = Geometry.Parse("M5,4 L19,4 L19,20 L5,20 Z M12,6 L12,15 M8.5,11.5 L12,15 L15.5,11.5 M8,18 L16,18"),
            [AaaIconKind.Settings] = Geometry.Parse("M12,8.5 A3.5,3.5 0 1 0 12,15.5 A3.5,3.5 0 1 0 12,8.5 M12,2 L12,5 M12,19 L12,22 M2,12 L5,12 M19,12 L22,12 M4.9,4.9 L7,7 M17,17 L19.1,19.1 M19.1,4.9 L17,7 M7,17 L4.9,19.1"),
            [AaaIconKind.Play] = Geometry.Parse("M7,4 L20,12 L7,20 Z"),
            [AaaIconKind.Note] = Geometry.Parse("M6,3 L15,3 L19,7 L19,21 L6,21 Z M15,3 L15,7 L19,7 M9,11 L16,11 M9,15 L16,15"),
            [AaaIconKind.Dice] = Geometry.Parse("M12,2 L20,7 L18,17 L12,22 L6,17 L4,7 Z M4,7 L12,11 L20,7 M12,11 L12,22 M6,17 L12,11 L18,17"),
            [AaaIconKind.Spark] = Geometry.Parse("M12,2 L13.8,9.2 L21,12 L13.8,14.8 L12,22 L10.2,14.8 L3,12 L10.2,9.2 Z"),
            [AaaIconKind.Location] = Geometry.Parse("M12,22 C8,17 5,13 5,9 A7,7 0 1 1 19,9 C19,13 16,17 12,22 Z M12,6.5 A2.5,2.5 0 1 0 12,11.5 A2.5,2.5 0 1 0 12,6.5"),
            [AaaIconKind.Shield] = Geometry.Parse("M12,2 L20,5 L19,13 C18,18 15,21 12,22 C9,21 6,18 5,13 L4,5 Z"),
            [AaaIconKind.Heart] = Geometry.Parse("M12,21 C4,16 2,11 4.5,7.5 C6.5,4.5 10,5 12,8 C14,5 17.5,4.5 19.5,7.5 C22,11 20,16 12,21 Z"),
            [AaaIconKind.Calendar] = Geometry.Parse("M4,6 L20,6 L20,21 L4,21 Z M7,3 L7,8 M17,3 L17,8 M4,10 L20,10"),
            [AaaIconKind.Save] = Geometry.Parse("M4,3 L18,3 L21,6 L21,21 L3,21 L3,3 Z M7,3 L7,9 L16,9 L16,3 M7,21 L7,14 L17,14 L17,21"),
            [AaaIconKind.Timeline] = Geometry.Parse("M3,12 L21,12 M6,9 A3,3 0 1 0 6,15 A3,3 0 1 0 6,9 M18,9 A3,3 0 1 0 18,15 A3,3 0 1 0 18,9")
        };

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(AaaIconKind), typeof(AaaVectorIcon),
        new FrameworkPropertyMetadata(AaaIconKind.Home, FrameworkPropertyMetadataOptions.AffectsRender));

    public AaaIconKind Kind
    {
        get => (AaaIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(AaaVectorIcon),
        new FrameworkPropertyMetadata(1.55, FrameworkPropertyMetadataOptions.AffectsRender));

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!Geometries.TryGetValue(Kind, out var geometry) || ActualWidth <= 0 || ActualHeight <= 0) return;

        var size = Math.Min(ActualWidth, ActualHeight);
        var scale = size / 24d;
        var offsetX = (ActualWidth - size) / 2d;
        var offsetY = (ActualHeight - size) / 2d;

        dc.PushTransform(new TranslateTransform(offsetX, offsetY));
        dc.PushTransform(new ScaleTransform(scale, scale));

        var brush = Foreground ?? Brushes.White;
        var pen = new Pen(brush, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();

        var fill = Kind is AaaIconKind.Play or AaaIconKind.Spark or AaaIconKind.Heart ? brush : null;
        dc.DrawGeometry(fill, pen, geometry);

        dc.Pop();
        dc.Pop();
    }
}
