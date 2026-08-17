using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

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

        portraitGrid.Children.Add(new Rectangle
        {
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 5, 8, 10), 0.42),
                    new GradientStop(Color.FromArgb(90, 5, 8, 10), 0.73),
                    new GradientStop(Color.FromArgb(220, 5, 8, 10), 1)
                }
            }
        });

        portraitGrid.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 183, 146, 81)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(7),
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = false
        });

        var crest = new Border
        {
            Width = 58,
            Height = 58,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 14),
            Background = new SolidColorBrush(Color.FromArgb(238, 15, 14, 11)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(171, 137, 73)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(29),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "✦",
                FontFamily = new FontFamily("Georgia"),
                FontSize = 25,
                Foreground = new SolidColorBrush(Color.FromRgb(223, 192, 122)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        crest.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 13,
            ShadowDepth = 0,
            Opacity = 0.48,
            Color = Color.FromRgb(174, 128, 58)
        };
        portraitGrid.Children.Add(crest);

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
