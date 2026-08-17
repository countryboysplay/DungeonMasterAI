using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DungeonMasterAI.App.Controls;

namespace DungeonMasterAI.App.Views;

public partial class LivePlayView : UserControl
{
    private bool _atmosphereApplied;

    public LivePlayView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyApprovedBattlefieldAtmosphere();
    }

    private void ApplyApprovedBattlefieldAtmosphere()
    {
        if (_atmosphereApplied) return;
        var gridControl = FindDescendant<CombatGridControl>(this);
        if (gridControl?.Parent is not Grid parent) return;

        var index = parent.Children.IndexOf(gridControl);
        if (index < 0) return;

        parent.Children.Insert(index + 1, new AaaBattlefieldAtmosphere
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        });
        _atmosphereApplied = true;
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
