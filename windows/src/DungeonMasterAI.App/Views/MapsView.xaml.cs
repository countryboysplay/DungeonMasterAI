using System.Windows;
using System.Windows.Controls;

namespace DungeonMasterAI.App.Views;

public partial class MapsView : UserControl
{
    public MapsView()
    {
        InitializeComponent();
    }

    private void MapsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.InitializeMapWorkspace();
    }
}
