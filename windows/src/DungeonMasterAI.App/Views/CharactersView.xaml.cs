using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DungeonMasterAI.App.Controls;

namespace DungeonMasterAI.App.Views;

public partial class CharactersView : UserControl
{
    private bool _referenceArtApplied;
    private bool _staticVectorTreatmentApplied;
    private bool _dynamicVectorTreatmentApplied;

    public CharactersView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyApprovedCharacterArtwork();
            ApplyStaticVectorTreatment();
        };
        LayoutUpdated += OnLayoutUpdated;
    }

    private void ApplyApprovedCharacterArtwork()
    {
        if (_referenceArtApplied) return;
        var placeholder = FindText(this, "♛");
        if (placeholder?.Parent is not Grid portraitGrid) return;
        portraitGrid.Children.Clear();
        portraitGrid.Children.Add(new Image
        {
            Source = LoadReferenceBitmap("Assets/Reference/aeliana-portrait.jpg"),
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
                StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), .48),
                    new GradientStop(Color.FromArgb(110, 3, 7, 8), 1)
                }
            }
        });
        portraitGrid.Children.Add(new Border
        {
            Width = 58, Height = 58,
            Background = new SolidColorBrush(Color.FromRgb(21, 19, 15)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(162, 129, 73)),
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(29),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 15),
            Child = new AaaVectorIcon
            {
                Kind = AaaIconKind.Shield, Width = 28, Height = 28,
                Foreground = new SolidColorBrush(Color.FromRgb(199, 162, 92)), StrokeThickness = 1.5
            }
        });
        _referenceArtApplied = true;
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

    private void ApplyStaticVectorTreatment()
    {
        if (_staticVectorTreatmentApplied) return;
        ReplaceLabeledGlyph("▣ Overview", "Overview", AaaIconKind.Home, 14);
        ReplaceLabeledGlyph("♟ Inventory", "Inventory", AaaIconKind.Inventory, 14);
        ReplaceLabeledGlyph("✣ Spells", "Spells", AaaIconKind.Spark, 14);
        ReplaceLabeledGlyph("✥ Conditions", "Conditions", AaaIconKind.Condition, 14);
        ReplaceLabeledGlyph("▥ Journal", "Journal", AaaIconKind.Rules, 14);
        ReplaceLabeledGlyph("♜ Progression", "Progression", AaaIconKind.Progress, 14);
        ReplaceIconOnly("♡", AaaIconKind.Heart, 19);
        ReplaceIconOnly("◇", AaaIconKind.Shield, 19);
        ReplaceIconOnly("♞", AaaIconKind.Speed, 19);
        ReplaceIconOnly("⚔", AaaIconKind.Combat, 18);
        ReplaceIconOnly("✣", AaaIconKind.Spark, 18);
        _staticVectorTreatmentApplied = true;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_dynamicVectorTreatmentApplied) return;
        if (FindText(this, "♟") is null && FindText(this, "◆") is null && FindText(this, "✦") is null) return;
        _dynamicVectorTreatmentApplied = true;
        LayoutUpdated -= OnLayoutUpdated;
        ReplaceAllIconOnly("♟", AaaIconKind.Characters, 25);
        ReplaceAllIconOnly("◆", AaaIconKind.Inventory, 18);
        ReplaceAllIconOnly("✦", AaaIconKind.Spark, 23);
    }

    private void ReplaceLabeledGlyph(string currentText, string label, AaaIconKind kind, double iconSize)
    {
        var block = FindText(this, currentText);
        if (block is null) return;
        var parent = block.Parent;
        var margin = block.Margin;
        var index = parent is Panel existingPanel ? existingPanel.Children.IndexOf(block) : -1;
        switch (parent)
        {
            case Panel targetPanel when index >= 0: targetPanel.Children.RemoveAt(index); break;
            case Border border: border.Child = null; break;
            case ContentControl contentControl: contentControl.Content = null; break;
            default: return;
        }
        block.Margin = new Thickness(0); block.Text = label; block.VerticalAlignment = VerticalAlignment.Center;
        var wrapper = new StackPanel { Orientation = Orientation.Horizontal, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        wrapper.Children.Add(new AaaVectorIcon
        {
            Kind = kind, Width = iconSize, Height = iconSize, Foreground = block.Foreground,
            StrokeThickness = 1.4, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center
        });
        wrapper.Children.Add(block);
        switch (parent)
        {
            case Panel targetPanel when index >= 0: targetPanel.Children.Insert(index, wrapper); break;
            case Border border: border.Child = wrapper; break;
            case ContentControl contentControl: contentControl.Content = wrapper; break;
        }
    }

    private void ReplaceIconOnly(string text, AaaIconKind kind, double size)
    {
        var block = FindText(this, text);
        if (block is not null) ReplaceTextBlockWithIcon(block, kind, size);
    }

    private int ReplaceAllIconOnly(string text, AaaIconKind kind, double size)
    {
        var blocks = new List<TextBlock>();
        CollectTextBlocks(this, text, blocks);
        foreach (var block in blocks) ReplaceTextBlockWithIcon(block, kind, size);
        return blocks.Count;
    }

    private static void ReplaceTextBlockWithIcon(TextBlock block, AaaIconKind kind, double size)
    {
        ReplaceChild(block, new AaaVectorIcon
        {
            Kind = kind, Width = size, Height = size, Foreground = block.Foreground, StrokeThickness = 1.35,
            HorizontalAlignment = block.HorizontalAlignment, VerticalAlignment = block.VerticalAlignment, Margin = block.Margin
        });
    }

    private static void ReplaceChild(FrameworkElement oldChild, FrameworkElement newChild)
    {
        switch (oldChild.Parent)
        {
            case Panel targetPanel:
                var childIndex = targetPanel.Children.IndexOf(oldChild);
                if (childIndex >= 0) { targetPanel.Children.RemoveAt(childIndex); targetPanel.Children.Insert(childIndex, newChild); }
                break;
            case Border border: border.Child = newChild; break;
            case ContentControl contentControl: contentControl.Content = newChild; break;
        }
    }

    private static void CollectTextBlocks(DependencyObject root, string text, ICollection<TextBlock> destination)
    {
        if (root is TextBlock block && block.Text == text) destination.Add(block);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) CollectTextBlocks(VisualTreeHelper.GetChild(root, i), text, destination);
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
