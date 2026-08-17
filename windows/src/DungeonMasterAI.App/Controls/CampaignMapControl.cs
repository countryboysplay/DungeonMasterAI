using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.App.Controls;

public sealed class CampaignMapControl : Canvas
{
    public static readonly DependencyProperty CampaignProperty = DependencyProperty.Register(
        nameof(Campaign), typeof(CampaignState), typeof(CampaignMapControl), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty ShowDmViewProperty = DependencyProperty.Register(
        nameof(ShowDmView), typeof(bool), typeof(CampaignMapControl), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(CampaignMapControl), new PropertyMetadata(0, Changed));
    public static readonly DependencyProperty SelectedLocationProperty = DependencyProperty.Register(
        nameof(SelectedLocation), typeof(WorldLocation), typeof(CampaignMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public CampaignState? Campaign { get => (CampaignState?)GetValue(CampaignProperty); set => SetValue(CampaignProperty, value); }
    public bool ShowDmView { get => (bool)GetValue(ShowDmViewProperty); set => SetValue(ShowDmViewProperty, value); }
    public int Revision { get => (int)GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public WorldLocation? SelectedLocation { get => (WorldLocation?)GetValue(SelectedLocationProperty); set => SetValue(SelectedLocationProperty, value); }

    public CampaignMapControl()
    {
        ClipToBounds = true;
        Background = CreateMapBackground();
        SizeChanged += (_, _) => Redraw();
    }

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((CampaignMapControl)d).Redraw();

    private static Brush CreateMapBackground()
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.43, 0.48),
            GradientOrigin = new Point(0.38, 0.42),
            RadiusX = 0.82,
            RadiusY = 0.78
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(83, 76, 55), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(48, 48, 39), 0.38));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(28, 34, 32), 0.69));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(12, 18, 20), 1));
        return brush;
    }

    private void Redraw()
    {
        Children.Clear();
        Background = CreateMapBackground();
        if (ActualWidth < 20 || ActualHeight < 20) return;

        AddTerrainWash();
        AddMapFrame();

        if (Campaign is null) return;
        var visible = Campaign.Locations
            .Where(l => ShowDmView || (l.Discovered && !l.DmOnly))
            .ToDictionary(l => l.Id);

        foreach (var c in Campaign.Connections.Where(c => ShowDmView || !c.Hidden))
        {
            if (!visible.TryGetValue(c.FromLocationId, out var from) || !visible.TryGetValue(c.ToLocationId, out var to)) continue;
            var roadGlow = new Line
            {
                X1 = PointX(from), Y1 = PointY(from), X2 = PointX(to), Y2 = PointY(to),
                Stroke = new SolidColorBrush(Color.FromArgb(90, 10, 11, 10)),
                StrokeThickness = c.Hidden ? 3 : 5,
                IsHitTestVisible = false
            };
            Children.Add(roadGlow);
            var road = new Line
            {
                X1 = PointX(from), Y1 = PointY(from), X2 = PointX(to), Y2 = PointY(to),
                Stroke = new SolidColorBrush(c.Hidden ? Color.FromArgb(185, 114, 102, 148) : Color.FromArgb(215, 186, 168, 119)),
                StrokeThickness = c.Hidden ? 1 : 1.55,
                StrokeDashArray = c.Hidden ? new DoubleCollection([5, 5]) : new DoubleCollection([8, 2]),
                IsHitTestVisible = false
            };
            Children.Add(road);
        }

        foreach (var location in visible.Values)
            AddLocationMarker(location);

        AddFogBanks();
    }

    private void AddTerrainWash()
    {
        AddTerrainBlob(0.13, 0.17, 0.34, 0.27, Color.FromArgb(38, 106, 116, 74));
        AddTerrainBlob(0.47, 0.09, 0.36, 0.29, Color.FromArgb(30, 147, 137, 96));
        AddTerrainBlob(0.17, 0.56, 0.38, 0.34, Color.FromArgb(36, 78, 102, 69));
        AddTerrainBlob(0.62, 0.53, 0.42, 0.34, Color.FromArgb(27, 87, 89, 75));
        AddTerrainBlob(0.75, 0.12, 0.30, 0.24, Color.FromArgb(31, 141, 137, 119));

        for (var i = 0; i < 15; i++)
        {
            var x = (i * 0.071 + 0.08) % 0.86;
            var y = (i * 0.137 + 0.12) % 0.78;
            var ridge = new Polygon
            {
                Points = new PointCollection([new Point(0, 18), new Point(12, 0), new Point(24, 18), new Point(17, 14), new Point(12, 21), new Point(7, 14)]),
                Fill = new SolidColorBrush(Color.FromArgb(45, 214, 205, 177)),
                Stroke = new SolidColorBrush(Color.FromArgb(55, 26, 28, 25)),
                StrokeThickness = 0.8,
                IsHitTestVisible = false,
                Opacity = 0.7
            };
            SetLeft(ridge, x * ActualWidth);
            SetTop(ridge, y * ActualHeight);
            Children.Add(ridge);
        }
    }

    private void AddTerrainBlob(double x, double y, double width, double height, Color color)
    {
        var ellipse = new Ellipse
        {
            Width = Math.Max(80, ActualWidth * width),
            Height = Math.Max(60, ActualHeight * height),
            Fill = new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B)),
            IsHitTestVisible = false
        };
        SetLeft(ellipse, ActualWidth * x);
        SetTop(ellipse, ActualHeight * y);
        Children.Add(ellipse);
    }

    private void AddMapFrame()
    {
        var inner = new Border
        {
            Width = Math.Max(0, ActualWidth - 18),
            Height = Math.Max(0, ActualHeight - 18),
            BorderBrush = new SolidColorBrush(Color.FromArgb(115, 126, 105, 64)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            IsHitTestVisible = false
        };
        SetLeft(inner, 9);
        SetTop(inner, 9);
        Children.Add(inner);
    }

    private void AddFogBanks()
    {
        AddFog(ActualWidth * 0.78, ActualHeight * 0.02, ActualWidth * 0.28, ActualHeight * 0.24, 65);
        AddFog(ActualWidth * -0.03, ActualHeight * 0.34, ActualWidth * 0.23, ActualHeight * 0.20, 46);
        AddFog(ActualWidth * 0.58, ActualHeight * 0.72, ActualWidth * 0.32, ActualHeight * 0.18, 52);
    }

    private void AddFog(double left, double top, double width, double height, byte alpha)
    {
        var fog = new Ellipse
        {
            Width = Math.Max(90, width),
            Height = Math.Max(55, height),
            Fill = new RadialGradientBrush(Color.FromArgb(alpha, 195, 194, 179), Color.FromArgb(0, 195, 194, 179)),
            IsHitTestVisible = false
        };
        SetLeft(fog, left);
        SetTop(fog, top);
        Children.Add(fog);
    }

    private void AddLocationMarker(WorldLocation location)
    {
        var partyHere = location.Id == Campaign?.PartyLocationId;
        var selected = SelectedLocation?.Id == location.Id;
        var marker = new Button
        {
            Tag = location,
            ToolTip = location.Description,
            Width = partyHere ? 46 : 38,
            Height = partyHere ? 46 : 38,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(220, 10, 15, 15)),
            Foreground = new SolidColorBrush(Color.FromRgb(232, 218, 184)),
            BorderBrush = new SolidColorBrush(selected || partyHere ? Color.FromRgb(220, 179, 93) : location.DmOnly ? Color.FromRgb(139, 102, 151) : Color.FromRgb(122, 104, 67)),
            BorderThickness = new Thickness(selected || partyHere ? 2 : 1),
            FontFamily = new FontFamily("Georgia"),
            FontSize = partyHere ? 17 : 14,
            Content = partyHere ? "✦" : location.DmOnly ? "?" : "◆",
            Cursor = System.Windows.Input.Cursors.Hand
        };
        marker.Click += (_, _) =>
        {
            SelectedLocation = location;
            Redraw();
        };
        SetLeft(marker, Math.Clamp(PointX(location) - marker.Width / 2, 4, Math.Max(4, ActualWidth - marker.Width - 4)));
        SetTop(marker, Math.Clamp(PointY(location) - marker.Height / 2, 4, Math.Max(4, ActualHeight - marker.Height - 4)));
        Children.Add(marker);

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(184, 8, 12, 13)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(100, 103, 86, 55)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(6, 2, 6, 2),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = location.Name,
                Foreground = new SolidColorBrush(partyHere ? Color.FromRgb(230, 207, 143) : Color.FromRgb(222, 215, 198)),
                FontFamily = new FontFamily("Georgia"),
                FontSize = partyHere ? 11 : 9.5,
                FontWeight = partyHere ? FontWeights.SemiBold : FontWeights.Normal
            }
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelWidth = Math.Max(64, label.DesiredSize.Width);
        SetLeft(label, Math.Clamp(PointX(location) - labelWidth / 2, 4, Math.Max(4, ActualWidth - labelWidth - 4)));
        SetTop(label, Math.Clamp(PointY(location) + marker.Height / 2 + 4, 4, Math.Max(4, ActualHeight - 27)));
        Children.Add(label);
    }

    private double PointX(WorldLocation l) => 35 + Math.Clamp(l.X, 0, 1) * Math.Max(20, ActualWidth - 70);
    private double PointY(WorldLocation l) => 35 + Math.Clamp(l.Y, 0, 1) * Math.Max(20, ActualHeight - 70);
}
