using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DungeonMasterAI.App.Controls;

namespace DungeonMasterAI.App.Views;

public partial class LivePlayView : UserControl
{
    private bool _staticTreatmentApplied;
    private bool _dynamicTreatmentApplied;
    private bool _atmosphereApplied;

    public LivePlayView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyStaticTreatment();
            ApplyCombatAtmosphere();
        };
        LayoutUpdated += OnLayoutUpdated;
    }

    private void ApplyCombatAtmosphere()
    {
        if (_atmosphereApplied) return;
        var gridControl = FindDescendant<CombatGridControl>(this);
        if (gridControl?.Parent is not Grid battlefield) return;
        var index = battlefield.Children.IndexOf(gridControl);
        if (index < 0) return;

        battlefield.Children.Insert(index + 1, new AaaCombatAtmosphere
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        });
        _atmosphereApplied = true;
    }

    private void ApplyStaticTreatment()
    {
        if (_staticTreatmentApplied) return;

        ReplaceIconOnly("✣", AaaIconKind.Location, 25);
        ReplaceLabeledGlyph("▣  DM NARRATION", "DM NARRATION", AaaIconKind.LivePlay, 14);
        ReplaceLabeledGlyph("☠  PLAYER ROLL REQUIRED", "PLAYER ROLL REQUIRED", AaaIconKind.Condition, 15);
        ReplaceIconOnly("⬡", AaaIconKind.Dice, 31);

        ReplaceButtonGlyph("➤", AaaIconKind.Location, 18);
        ReplaceButtonGlyph("✥", AaaIconKind.Maps, 18);
        ReplaceButtonGlyph("▦", AaaIconKind.Rules, 17);
        ReplaceButtonGlyph("⌖", AaaIconKind.Location, 17);

        ReplaceLabeledGlyph("«  COMBAT TRACKER", "COMBAT TRACKER", AaaIconKind.Combat, 14);
        ReplaceLabeledGlyph("♙  REACTIONS", "REACTIONS", AaaIconKind.Spark, 14);

        ReplaceLabeledGlyph("⚔  Attack", "Attack", AaaIconKind.Combat, 17);
        ReplaceLabeledGlyph("✺  Cast Spell", "Cast Spell", AaaIconKind.Spark, 17);
        ReplaceLabeledGlyph("◇  Dodge", "Dodge", AaaIconKind.Shield, 17);
        ReplaceLabeledGlyph("◉  Ready", "Ready", AaaIconKind.Timeline, 17);
        ReplaceLabeledGlyph("⇥  End Turn", "End Turn", AaaIconKind.Progress, 17);
        ReplaceLabeledGlyph("✦  Ask AI", "Ask AI", AaaIconKind.Spark, 17);

        _staticTreatmentApplied = true;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_dynamicTreatmentApplied) return;
        var combatants = ReplaceAllIconOnly("♟", AaaIconKind.Characters, 21);
        if (combatants > 0)
        {
            _dynamicTreatmentApplied = true;
            LayoutUpdated -= OnLayoutUpdated;
        }
    }

    private void ReplaceButtonGlyph(string currentContent, AaaIconKind kind, double size)
    {
        var button = FindButton(this, currentContent);
        if (button is null) return;
        button.Content = new AaaVectorIcon
        {
            Kind = kind,
            Width = size,
            Height = size,
            Foreground = button.Foreground,
            StrokeThickness = 1.45,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void ReplaceLabeledGlyph(string currentText, string label, AaaIconKind kind, double iconSize)
    {
        var block = FindText(this, currentText);
        if (block is null) return;
        WrapTextBlock(block, label, kind, iconSize);
    }

    private static void WrapTextBlock(TextBlock block, string label, AaaIconKind kind, double iconSize)
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
            Margin = new Thickness(0, 0, 7, 0),
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

    private static Button? FindButton(DependencyObject root, string content)
    {
        if (root is Button button && button.Content is string text && text == content) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindButton(VisualTreeHelper.GetChild(root, i), content);
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
