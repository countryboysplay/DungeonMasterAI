using System.Windows;
using System.Windows.Controls;

namespace DungeonMasterAI.App.Views;

public partial class UtilityPlaceholderView : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(UtilityPlaceholderView), new PropertyMetadata("Screen"));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(UtilityPlaceholderView), new PropertyMetadata(string.Empty));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public UtilityPlaceholderView() => InitializeComponent();
}
