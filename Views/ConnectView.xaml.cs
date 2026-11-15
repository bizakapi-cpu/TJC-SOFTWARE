using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;

namespace TJCServiceDesk.Views;

public partial class ConnectView : UserControl
{
    public ConnectView() => InitializeComponent();
    private void Message_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && DataContext is ViewModels.ConnectViewModel vm)
        {
            vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}
