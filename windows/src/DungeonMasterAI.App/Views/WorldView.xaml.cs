using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DungeonMasterAI.App.Controls;

namespace DungeonMasterAI.App.Views;

public partial class WorldView : UserControl
{
    private bool _controlsWired;
    private bool _staticVectorTreatmentApplied;
    private bool _dynamicVectorTreatmentApplied;

    public WorldView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyStaticVectorTreatment();
        WireMapModeControls();
    }

    private void WireMapModeControls()
    {
        if (_controlsWired) return;
        var player = FindRadioButton(this, "PLAYER VIEW");
        var dm = FindRadioButton(this, "DM VIEW");
        if (player is null || dm is null) return;

        player.Checked += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.ShowDmMap = false;
        };
        dm.Checked += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.ShowDmMap = true;
        };

        if (DataContext is MainViewModel viewModel)
        {
            dm.IsChecked = viewModel.ShowDmMap;
            player.IsChecked = !viewModel.ShowDmMap;
        }
        else
        {
            player.IsChecked = true;
        }
        _controlsWired = true;
    }

    private void ApplyStaticVectorTreatment()
    {
        if (_staticVectorTreatmentApplied) return;

        ReplaceLabeledGlyph("◇ LEGEND", "LEGEND", AaaIconKind.Maps, 13);
        ReplaceLabeledGlyph("● Ping", "Ping", AaaIconKind.Location, 12);
        ReplaceLabeledGlyph("◉ Reveal", "Reveal", AaaIconKind.Spark, 12);
        ReplaceLabeledGlyph("⊘ Hide", "Hide", AaaIconKind.Condition, 12);
        ReplaceLabeledGlyph("▽ Filter", "Filter", AaaIconKind.Settings, 12);
        ReplaceLabeledGlyph("▣ Add Note", "Add Note", AaaIconKind.Note, 12);

        ReplaceHeader("CURRENT QUESTS", AaaIconKind.Quests);
        ReplaceHeader("FACTIONS", AaaIconKind.Shield);
        ReplaceHeader("RUMORS", AaaIconKind.LivePlay);
        ReplaceHeader("SECRETS  ◉", AaaIconKind.Rules, "SECRETS");
        ReplaceHeader("WORLD TIMELINE", AaaIconKind.Timeline);
        ReplaceHeader("QUEST TRACKER", AaaIconKind.Quests);

        // Selected-location illustration slot should read as an intentional map card,
        // not as a giant chess-piece placeholder.
        ReplaceIconOnly("♜", AaaIconKind.Location, 39);

        _staticVectorTreatmentApplied = true;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_dynamicVectorTreatmentApplied) return;
        var questGlyphs = ReplaceAllIconOnly("♜", AaaIconKind.Quests, 14);
        if (questGlyphs > 0)
        {
            _dynamicVectorTreatmentApplied = true;
            LayoutUpdated -= OnLayoutUpdated;
        }
    }

    private void ReplaceHeader(string currentText, AaaIconKind kind, string? replacementText = null)
    {
        var block = FindText(this, currentText);
        if (block is null) return;
        WrapTextBlock(block, replacementText ?? currentText, kind, 13, 6);
    }

    private void ReplaceLabeledGlyph(string currentText, string label, AaaIconKind kind, double iconSize)
    {
        var block = FindText(this, currentText);
        if (block is null) return;
        WrapTextBlock(block, label, kind, iconSize, 5);
    }

    private static void WrapTextBlock(TextBlock block, string label, AaaIconKind kind, double iconSize, double gap)
    {
        var parent = block.Parent;
        var margin = block.Margin;
        var index = parent is Panel existingPanel ? existingPanel.Children.IndexOf(block) : -1;
        switch (parent)
        {
            case Panel targetPanel when index >= 0:
                targetPanel.Children.RemoveAt(index);
                break;
            case Border border:
                border.Child = null;
                break;
            case ContentControl contentControl:
                contentControl.Content = null;
                break;
            default:
                return;
        }

        block.Text = label;
        block.Margin = new Thickness(0);
        block.VerticalAlignment = VerticalAlignment.Center;
        var wrapper = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = margin,
            VerticalAlignment = VerticalAlignment.Center
        };
        wrapper.Children.Add(new AaaVectorIcon
        {
            Kind = kind,
            Width = iconSize,
            Height = iconSize,
            Foreground = block.Foreground,
            StrokeThickness = 1.4,
            Margin = new Thickness(0, 0, gap, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        wrapper.Children.Add(block);

        switch (parent)
        {
            case Panel targetPanel when index >= 0:
                targetPanel.Children.Insert(index, wrapper);
                break;
            case Border border:
                border.Child = wrapper;
                break;
            case ContentControl contentControl:
                contentControl.Content = wrapper;
                break;
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
        switch (block.Parent)
        {
            case Panel panel:
                var index = panel.Children.IndexOf(block);
                if (index >= 0)
                {
                    panel.Children.RemoveAt(index);
                    panel.Children.Insert(index, icon);
                }
                break;
            case Border border:
                border.Child = icon;
                break;
            case ContentControl contentControl:
                contentControl.Content = icon;
                break;
        }
    }

    private static RadioButton? FindRadioButton(DependencyObject root, string content)
    {
        if (root is RadioButton button && button.Content is string text && text == content) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindRadioButton(VisualTreeHelper.GetChild(root, i), content);
            if (found is not null) return found;
        }
        return null;
    }

    private static void CollectTextBlocks(DependencyObject root, string text, ICollection<TextBlock> destination)
    {
        if (root is TextBlock block && block.Text == text) destination.Add(block);
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
