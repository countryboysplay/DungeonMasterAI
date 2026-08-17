using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DungeonMasterAI.App.Controls;

namespace DungeonMasterAI.App.Views;

public partial class HomeView : UserControl
{
    private bool _referenceArtApplied;
    private bool _campaignCrestApplied;
    private bool _vectorHeadersApplied;

    public HomeView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyApprovedHeroArtwork();
            ApplyApprovedCampaignCrest();
            ApplyVectorSectionHeaders();
        };
    }

    private void ApplyApprovedHeroArtwork()
    {
        if (_referenceArtApplied) return;
        var map = FindDescendant<CampaignMapControl>(this);
        if (map?.Parent is not Grid heroGrid) return;

        var index = heroGrid.Children.IndexOf(map);
        if (index < 0) return;

        var artwork = new Image
        {
            Source = new BitmapImage(new Uri(
                "pack://application:,,,/DungeonMasterAI;component/Assets/Reference/home-hero-greenhaven.jpg",
                UriKind.Absolute)),
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            SnapsToDevicePixels = true
        };

        map.Visibility = Visibility.Collapsed;
        heroGrid.Children.Insert(index, artwork);
        _referenceArtApplied = true;
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

        parent.Children.RemoveAt(index);
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

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }
}
