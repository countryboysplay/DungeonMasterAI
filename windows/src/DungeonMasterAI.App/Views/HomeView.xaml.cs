using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DungeonMasterAI.App.Controls;

namespace DungeonMasterAI.App.Views;

public partial class HomeView : UserControl
{
    private bool _referenceArtApplied;
    private bool _parchmentArtApplied;
    private bool _campaignCrestApplied;
    private bool _vectorHeadersApplied;

    public HomeView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyApprovedHeroArtwork();
            ApplyApprovedParchmentArtwork();
            ApplyApprovedCampaignCrest();
            ApplyVectorSectionHeaders();
        };
    }

    private void ApplyApprovedHeroArtwork()
    {
        if (_referenceArtApplied) return;
        var maps = FindDescendants<CampaignMapControl>(this);
        var map = maps.FirstOrDefault();
        if (map?.Parent is not Grid heroGrid) return;

        var index = heroGrid.Children.IndexOf(map);
        if (index < 0) return;

        var artwork = new Image
        {
            Source = LoadReferenceBitmap("Assets/Reference/home-hero-greenhaven.jpg"),
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false
        };

        map.Visibility = Visibility.Collapsed;
        heroGrid.Children.Insert(index, artwork);
        _referenceArtApplied = true;
    }

    private void ApplyApprovedParchmentArtwork()
    {
        if (_parchmentArtApplied) return;
        var maps = FindDescendants<CampaignMapControl>(this);
        var map = maps.Skip(1).FirstOrDefault();
        if (map?.Parent is not Grid panelGrid) return;

        var index = panelGrid.Children.IndexOf(map);
        if (index < 0) return;

        var parchment = new Image
        {
            Source = LoadReferenceBitmap("Assets/Reference/home-parchment.jpg"),
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
            Opacity = 0.93
        };

        map.Visibility = Visibility.Collapsed;
        panelGrid.Children.Insert(index, parchment);
        _parchmentArtApplied = true;
    }

    private static BitmapImage LoadReferenceBitmap(string relativePath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.UriSource = new Uri($"pack://application:,,,/DungeonMasterAI;component/{relativePath}", UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void ApplyApprovedCampaignCrest()
    {
        if (_campaignCrestApplied) return;
        var placeholder = FindTextBlock(this, "♧");
        if (placeholder?.Parent is not Grid crestGrid) return;
        if (crestGrid.Parent is Border frame)
        {
            frame.Background = Brushes.Transparent;
            frame.BorderThickness = new Thickness(0);
        }

        crestGrid.Children.Clear();
        crestGrid.Children.Add(new AaaCampaignCrest
        {
            Width = 104,
            Height = 126,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        _campaignCrestApplied = true;
    }

    private void ApplyVectorSectionHeaders()
    {
        if (_vectorHeadersApplied) return;

        ReplaceHeader("♧  AI RUNTIME STATUS", "AI RUNTIME STATUS", AaaIconKind.Spark);
        ReplaceHeader("▣  NEXT SESSION", "NEXT SESSION", AaaIconKind.Calendar);
        ReplaceHeader("▤  ACTIVE QUEST", "ACTIVE QUEST", AaaIconKind.Quests);
        ReplaceHeader("●  CURRENT LOCATION", "CURRENT LOCATION", AaaIconKind.Location);
        ReplaceHeader("♟  PARTY STATUS", "PARTY STATUS", AaaIconKind.Characters);
        ReplaceHeader("◇  SAVE & RECOVERY", "SAVE & RECOVERY", AaaIconKind.Shield);
        ReplaceHeader("▥  RECENT WORLD EVENTS", "RECENT WORLD EVENTS", AaaIconKind.World);
        ReplaceHeader("✎  RECENT ACTIVITY", "RECENT ACTIVITY", AaaIconKind.Timeline);

        _vectorHeadersApplied = true;
    }

    private void ReplaceHeader(string currentText, string label, AaaIconKind kind)
    {
        var block = FindTextBlock(this, currentText);
        if (block?.Parent is not Panel parent) return;

        var index = parent.Children.IndexOf(block);
        if (index < 0) return;

        var iconBrush = block.Foreground;
        parent.Children.RemoveAt(index);

        block.Text = label;
        block.VerticalAlignment = VerticalAlignment.Center;

        var wrapper = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        wrapper.Children.Add(new AaaVectorIcon
        {
            Kind = kind,
            Width = 14,
            Height = 14,
            Foreground = iconBrush,
            StrokeThickness = 1.45,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        wrapper.Children.Add(block);
        parent.Children.Insert(index, wrapper);
    }

    private static TextBlock? FindTextBlock(DependencyObject root, string text)
    {
        if (root is TextBlock block && block.Text == text) return block;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindTextBlock(VisualTreeHelper.GetChild(root, i), text);
            if (found is not null) return found;
        }
        return null;
    }

    private static List<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var results = new List<T>();
        CollectDescendants(root, results);
        return results;
    }

    private static void CollectDescendants<T>(DependencyObject root, ICollection<T> destination) where T : DependencyObject
    {
        if (root is T match) destination.Add(match);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            CollectDescendants(VisualTreeHelper.GetChild(root, i), destination);
    }
}
