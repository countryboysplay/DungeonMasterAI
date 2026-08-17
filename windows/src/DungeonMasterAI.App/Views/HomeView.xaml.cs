using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DungeonMasterAI.App.Controls;

namespace DungeonMasterAI.App.Views;

public partial class HomeView : UserControl
{
    private bool _referenceArtApplied;

    public HomeView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyApprovedHeroArtwork();
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
