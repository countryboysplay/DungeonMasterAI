using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        Background = new SolidColorBrush(Color.FromRgb(15, 17, 15));
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        SizeChanged += (_, _) => Redraw();
    }

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((CampaignMapControl)d).Redraw();

    private void Redraw()
    {
        Children.Clear();
        if (ActualWidth < 20 || ActualHeight < 20) return;

        DrawTerrainBase();
        DrawTerrainFeatures();
        DrawMapFrame();

        if (Campaign is null) return;

        var visible = Campaign.Locations
            .Where(l => ShowDmView || (l.Discovered && !l.DmOnly))
            .ToDictionary(l => l.Id);

        DrawConnections(visible);
        foreach (var location in visible.Values)
            DrawLocationMarker(location);
    }

    /// <summary>
    /// Thin inset rule that frames the parchment surface, salvaged from the abandoned
    /// r50 map experiment. Pinned to the top of the z-order so it reads as the edge of
    /// the map sheet rather than as terrain, and so it survives the empty-campaign path.
    /// </summary>
    private void DrawMapFrame()
    {
        var frame = new Border
        {
            Width = Math.Max(0, ActualWidth - 18),
            Height = Math.Max(0, ActualHeight - 18),
            BorderBrush = new SolidColorBrush(Color.FromArgb(115, 126, 105, 64)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            IsHitTestVisible = false
        };
        SetLeft(frame, 9);
        SetTop(frame, 9);
        SetZIndex(frame, 20);
        Children.Add(frame);
    }

    private void DrawTerrainBase()
    {
        var baseRect = new Rectangle
        {
            Width = ActualWidth,
            Height = ActualHeight,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(
                Color.FromRgb(47, 46, 31),
                Color.FromRgb(31, 37, 37),
                new Point(0, 0),
                new Point(1, 1))
        };
        Children.Add(baseRect);

        AddTerrainGlow(0.26, 0.57, 0.55, 0.70, Color.FromArgb(110, 45, 68, 40));
        AddTerrainGlow(0.48, 0.25, 0.46, 0.42, Color.FromArgb(90, 91, 82, 57));
        AddTerrainGlow(0.79, 0.30, 0.46, 0.55, Color.FromArgb(100, 51, 55, 55));
        AddTerrainGlow(0.78, 0.79, 0.43, 0.50, Color.FromArgb(120, 26, 58, 65));
        AddTerrainGlow(0.22, 0.87, 0.40, 0.27, Color.FromArgb(95, 41, 48, 34));

        var vignette = new Rectangle
        {
            Width = ActualWidth,
            Height = ActualHeight,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                Center = new Point(.50, .46),
                GradientOrigin = new Point(.50, .46),
                RadiusX = .80,
                RadiusY = .78,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), .32),
                    new GradientStop(Color.FromArgb(90, 4, 7, 8), .78),
                    new GradientStop(Color.FromArgb(185, 3, 6, 7), 1)
                }
            }
        };
        Children.Add(vignette);
    }

    private void DrawTerrainFeatures()
    {
        // Snowy northern mountain chain.
        for (var row = 0; row < 3; row++)
        {
            var points = new PointCollection();
            var y = ActualHeight * (0.10 + row * 0.045);
            var start = ActualWidth * (0.23 + row * 0.025);
            var end = ActualWidth * (0.58 + row * 0.018);
            var step = Math.Max(24, ActualWidth * 0.035);
            var toggle = false;
            for (var x = start; x <= end; x += step)
            {
                points.Add(new Point(x, y + (toggle ? 10 : 0)));
                points.Add(new Point(x + step * .45, y - 22 - row * 4));
                points.Add(new Point(x + step, y + (toggle ? 8 : 13)));
                toggle = !toggle;
            }

            Children.Add(new Polyline
            {
                Points = points,
                Stroke = new SolidColorBrush(Color.FromArgb((byte)(150 - row * 25), 170, 166, 143)),
                StrokeThickness = row == 0 ? 1.7 : 1.1,
                IsHitTestVisible = false
            });
        }

        // River running from mountains toward the drowned reaches.
        var river = new Path
        {
            IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Color.FromArgb(150, 82, 117, 123)),
            StrokeThickness = Math.Max(3, ActualWidth * .0032),
            Data = BuildRiverGeometry(),
            Opacity = .82
        };
        Children.Add(river);

        // Fine road network for the parchment-map feel.
        var roadPen = new SolidColorBrush(Color.FromArgb(125, 184, 166, 119));
        AddDecorativeRoad(new Point(.12, .39), new Point(.35, .45), new Point(.48, .59), roadPen);
        AddDecorativeRoad(new Point(.35, .45), new Point(.25, .69), new Point(.18, .86), roadPen);
        AddDecorativeRoad(new Point(.35, .45), new Point(.56, .36), new Point(.72, .25), roadPen);
        AddDecorativeRoad(new Point(.48, .59), new Point(.66, .62), new Point(.84, .48), roadPen);

        // Soft fog banks similar to the approved World reference.
        AddFog(.05, .19, .22, .20);
        AddFog(.66, .07, .29, .19);
        AddFog(.72, .42, .26, .24);
        AddFog(.33, .78, .20, .18);
    }

    private void DrawConnections(IReadOnlyDictionary<string, WorldLocation> visible)
    {
        if (Campaign is null) return;

        foreach (var connection in Campaign.Connections.Where(c => ShowDmView || !c.Hidden))
        {
            if (!visible.TryGetValue(connection.FromLocationId, out var from)
                || !visible.TryGetValue(connection.ToLocationId, out var to))
                continue;

            var line = new Line
            {
                X1 = PointX(from),
                Y1 = PointY(from),
                X2 = PointX(to),
                Y2 = PointY(to),
                Stroke = new SolidColorBrush(connection.Hidden
                    ? Color.FromArgb(175, 118, 93, 148)
                    : Color.FromArgb(170, 211, 190, 137)),
                StrokeThickness = connection.Hidden ? 1.15 : 1.5,
                StrokeDashArray = connection.Hidden ? new DoubleCollection([3, 4]) : new DoubleCollection([2, 2]),
                Opacity = connection.Hidden ? .72 : .62,
                IsHitTestVisible = false
            };
            SetZIndex(line, 4);
            Children.Add(line);
        }
    }

    private void DrawLocationMarker(WorldLocation location)
    {
        if (Campaign is null) return;

        var partyHere = location.Id == Campaign.PartyLocationId;
        var selected = SelectedLocation?.Id == location.Id;
        var markerSize = partyHere ? 40d : selected ? 36d : 30d;
        var x = PointX(location);
        var y = PointY(location);
        var accent = location.DmOnly
            ? Color.FromRgb(147, 79, 104)
            : partyHere
                ? Color.FromRgb(213, 171, 85)
                : Color.FromRgb(164, 143, 96);

        var marker = new Grid
        {
            Width = markerSize,
            Height = markerSize,
            Cursor = Cursors.Hand,
            Tag = location,
            ToolTip = location.Description
        };
        marker.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(Color.FromArgb(232, 9, 15, 16)),
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = partyHere ? 2.4 : 1.4
        });
        if (partyHere)
        {
            marker.Children.Add(new Ellipse
            {
                Margin = new Thickness(5),
                Fill = new SolidColorBrush(Color.FromArgb(45, accent.R, accent.G, accent.B)),
                Stroke = new SolidColorBrush(Color.FromArgb(150, accent.R, accent.G, accent.B)),
                StrokeThickness = 1
            });
        }
        marker.Children.Add(new AaaVectorIcon
        {
            Kind = partyHere ? AaaIconKind.Home : AaaIconKind.Location,
            Width = markerSize * .47,
            Height = markerSize * .47,
            Foreground = new SolidColorBrush(accent),
            StrokeThickness = 1.45,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        marker.MouseLeftButtonUp += (_, e) =>
        {
            SelectedLocation = location;
            e.Handled = true;
            Redraw();
        };

        SetLeft(marker, Math.Clamp(x - markerSize / 2, 4, Math.Max(4, ActualWidth - markerSize - 4)));
        SetTop(marker, Math.Clamp(y - markerSize / 2, 4, Math.Max(4, ActualHeight - markerSize - 4)));
        SetZIndex(marker, 12);
        Children.Add(marker);

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(165, 5, 10, 11)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(.7),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(5, 2, 5, 2),
            Child = new TextBlock
            {
                Text = location.Name.ToUpperInvariant(),
                Foreground = new SolidColorBrush(partyHere ? Color.FromRgb(230, 213, 170) : Color.FromRgb(204, 197, 176)),
                FontFamily = new FontFamily("Georgia"),
                FontSize = partyHere ? 10.5 : 9,
                FontWeight = partyHere ? FontWeights.SemiBold : FontWeights.Normal
            },
            IsHitTestVisible = false
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SetLeft(label, Math.Clamp(x - label.DesiredSize.Width / 2, 4, Math.Max(4, ActualWidth - label.DesiredSize.Width - 4)));
        SetTop(label, Math.Clamp(y + markerSize / 2 + 4, 4, Math.Max(4, ActualHeight - 24)));
        SetZIndex(label, 11);
        Children.Add(label);
    }

    private void AddTerrainGlow(double centerX, double centerY, double widthFactor, double heightFactor, Color color)
    {
        var ellipse = new Ellipse
        {
            Width = ActualWidth * widthFactor,
            Height = ActualHeight * heightFactor,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(color, 0),
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)
                }
            }
        };
        SetLeft(ellipse, ActualWidth * centerX - ellipse.Width / 2);
        SetTop(ellipse, ActualHeight * centerY - ellipse.Height / 2);
        Children.Add(ellipse);
    }

    private void AddFog(double x, double y, double width, double height)
    {
        var fog = new Ellipse
        {
            Width = ActualWidth * width,
            Height = ActualHeight * height,
            IsHitTestVisible = false,
            Opacity = .45,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(70, 186, 185, 166), 0),
                    new GradientStop(Color.FromArgb(22, 140, 143, 135), .58),
                    new GradientStop(Color.FromArgb(0, 100, 105, 105), 1)
                }
            }
        };
        SetLeft(fog, ActualWidth * x);
        SetTop(fog, ActualHeight * y);
        SetZIndex(fog, 3);
        Children.Add(fog);
    }

    private void AddDecorativeRoad(Point start, Point middle, Point end, Brush brush)
    {
        var path = new Path
        {
            Stroke = brush,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection([5, 3]),
            Opacity = .52,
            IsHitTestVisible = false,
            Data = new PathGeometry(new[]
            {
                new PathFigure(
                    new Point(start.X * ActualWidth, start.Y * ActualHeight),
                    new PathSegment[]
                    {
                        new QuadraticBezierSegment(
                            new Point(middle.X * ActualWidth, middle.Y * ActualHeight),
                            new Point(end.X * ActualWidth, end.Y * ActualHeight), true)
                    }, false)
            })
        };
        SetZIndex(path, 2);
        Children.Add(path);
    }

    private Geometry BuildRiverGeometry()
    {
        var figure = new PathFigure { StartPoint = new Point(ActualWidth * .43, ActualHeight * .08) };
        figure.Segments.Add(new BezierSegment(
            new Point(ActualWidth * .46, ActualHeight * .25),
            new Point(ActualWidth * .37, ActualHeight * .39),
            new Point(ActualWidth * .45, ActualHeight * .52), true));
        figure.Segments.Add(new BezierSegment(
            new Point(ActualWidth * .51, ActualHeight * .63),
            new Point(ActualWidth * .62, ActualHeight * .68),
            new Point(ActualWidth * .72, ActualHeight * .91), true));
        return new PathGeometry([figure]);
    }

    private double PointX(WorldLocation location) => 52 + Math.Clamp(location.X, 0, 1) * Math.Max(20, ActualWidth - 104);
    private double PointY(WorldLocation location) => 54 + Math.Clamp(location.Y, 0, 1) * Math.Max(20, ActualHeight - 108);
}
