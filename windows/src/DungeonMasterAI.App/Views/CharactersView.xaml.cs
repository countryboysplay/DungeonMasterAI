using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

        var replacedParty = ReplaceAllIconOnly("♟", AaaIconKind.Characters, 25);
        var replacedInventory = ReplaceAllIconOnly("◆", AaaIconKind.Inventory, 18);
        var replacedSpells = ReplaceAllIconOnly("✦", AaaIconKind.Spark, 23);

        // Party cards only exist after campaign data arrives. Once at least one has
        // materialized, the major dynamic templates have been exercised.
        if (replacedParty > 0)
        {
            _dynamicVectorTreatmentApplied = true;
            LayoutUpdated -= OnLayoutUpdated;
        }
    }

    private void ReplaceLabeledGlyph(string currentText, string label, AaaIconKind kind, double iconSize)
    {
        var block = FindText(this, currentText);
        if (block is null) return;

        var margin = block.Margin;
        block.Margin = new Thickness(0);
        block.Text = label;
        block.VerticalAlignment = VerticalAlignment.Center;

        var wrapper = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = margin,
            VerticalAlignment = block.VerticalAlignment
        };
        wrapper.Children.Add(new AaaVectorIcon
        {
            Kind = kind,
            Width = iconSize,
            Height = iconSize,
            Foreground = block.Foreground,
            StrokeThickness = 1.4,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        wrapper.Children.Add(block);
        ReplaceChild(block, wrapper);
    }

    private void ReplaceIconOnly(string text, AaaIconKind kind, double size)
    {
        var block = FindText(this, text);
        if (block is null) return;
        ReplaceTextBlockWithIcon(block, kind, size);
    }

    private int ReplaceAllIconOnly(string text, AaaIconKind kind, double size)
    {
        var blocks = new List<TextBlock>();
        CollectTextBlocks(this, text, blocks);
        foreach (var block in blocks)
            ReplaceTextBlockWithIcon(block, kind, size);
        return blocks.Count;
    }

    private static void ReplaceTextBlockWithIcon(TextBlock block, AaaIconKind kind, double size)
    {
        var icon = new AaaVectorIcon
        {
            Kind = kind,
            Width = size,
            Height = size,
            Foreground = block.Foreground,
            StrokeThickness = 1.35,
            HorizontalAlignment = block.HorizontalAlignment,
            VerticalAlignment = block.VerticalAlignment,
            Margin = block.Margin
        };
        ReplaceChild(block, icon);
    }

    private static void ReplaceChild(FrameworkElement oldChild, FrameworkElement newChild)
    {
        switch (oldChild.Parent)
        {
            case Panel panel:
            {
                var index = panel.Children.IndexOf(oldChild);
                if (index < 0) return;
                panel.Children.RemoveAt(index);
                panel.Children.Insert(index, newChild);
                break;
            }
            case Border border:
                border.Child = newChild;
                break;
            case ContentControl contentControl:
                contentControl.Content = newChild;
                break;
        }
    }

    private static void CollectTextBlocks(DependencyObject root, string text, ICollection<TextBlock> destination)
    {
        if (root is TextBlock block && block.Text == text)
            destination.Add(block);

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            CollectTextBlocks(VisualTreeHelper.GetChild(root, i), text, destination);
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
