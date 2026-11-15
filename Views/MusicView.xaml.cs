using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TJCServiceDesk.Views;

public partial class MusicView : UserControl
{
    public MusicView() => InitializeComponent();

    private void SearchQuery_GotFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MusicViewModel viewModel) viewModel.ShowSearchHistory();
    }

    private void Tracks_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView list && ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListViewItem item)
            item.IsSelected = true;
    }
}
