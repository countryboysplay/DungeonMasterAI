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
        Background = new SolidColorBrush(Color.FromRgb(14, 15, 21));
        ClipToBounds = true;
        SizeChanged += (_, _) => Redraw();
    }

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((CampaignMapControl)d).Redraw();

    private void Redraw()
    {
        Children.Clear();
        if (Campaign is null || ActualWidth < 20 || ActualHeight < 20) return;
        var visible = Campaign.Locations.Where(l => ShowDmView || (l.Discovered && !l.DmOnly)).ToDictionary(l => l.Id);
        foreach (var c in Campaign.Connections.Where(c => ShowDmView || !c.Hidden))
        {
            if (!visible.TryGetValue(c.FromLocationId, out var from) || !visible.TryGetValue(c.ToLocationId, out var to)) continue;
            var line = new Line
            {
                X1 = PointX(from), Y1 = PointY(from), X2 = PointX(to), Y2 = PointY(to),
                Stroke = new SolidColorBrush(c.Hidden ? Color.FromRgb(111,98,167) : Color.FromRgb(65,70,92)),
                StrokeThickness = c.Hidden ? 1 : 2,
                StrokeDashArray = c.Hidden ? new DoubleCollection([4, 4]) : null,
                IsHitTestVisible = false
            };
            Children.Add(line);
        }
        foreach (var l in visible.Values)
        {
            var button = new Button
            {
                Content = l.Name,
                Tag = l,
                ToolTip = l.Description,
                MinWidth = 92,
                Padding = new Thickness(8, 5, 8, 5),
                Background = new SolidColorBrush(l.DmOnly ? Color.FromRgb(63,49,74) : Color.FromRgb(30,33,48)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(l.Id == Campaign.PartyLocationId ? Color.FromRgb(185,160,106) : Color.FromRgb(48,52,72))
            };
            button.Click += (_, _) => SelectedLocation = l;
            SetLeft(button, Math.Clamp(PointX(l) - 46, 4, Math.Max(4, ActualWidth - 100)));
            SetTop(button, Math.Clamp(PointY(l) - 16, 4, Math.Max(4, ActualHeight - 38)));
            Children.Add(button);
        }
    }

    private double PointX(WorldLocation l) => 35 + Math.Clamp(l.X, 0, 1) * Math.Max(20, ActualWidth - 70);
    private double PointY(WorldLocation l) => 35 + Math.Clamp(l.Y, 0, 1) * Math.Max(20, ActualHeight - 70);
}
