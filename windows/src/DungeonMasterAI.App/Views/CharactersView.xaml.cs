using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DungeonMasterAI.App.Views;

public partial class CharactersView : UserControl
{
    private bool _referenceArtApplied;

    public CharactersView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyApprovedCharacterArtwork();
    }

    private void ApplyApprovedCharacterArtwork()
    {
        if (_referenceArtApplied) return;
        var placeholder = FindText(this, "♛");
        if (placeholder?.Parent is not Grid portraitGrid) return;

        portraitGrid.Children.Clear();
        portraitGrid.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri(
                "pack://application:,,,/DungeonMasterAI;component/Assets/Reference/aeliana-portrait.jpg",
                UriKind.Absolute)),
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            SnapsToDevicePixels = true
        });
        _referenceArtApplied = true;
    }

    private static TextBlock? FindText(DependencyObject root, string text)
    {
        if (root is TextBlock block && block.Text == text) return block;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindText(VisualTreeHelper.GetChild(root, i), text);
            if (found is not null) return found;
        }
        return null;
    }
}
