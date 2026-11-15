using System.Windows;
using System.Windows.Controls;

namespace TJCServiceDesk.Controls;

public partial class StatusPill : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusPill), new PropertyMetadata(""));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public StatusPill() => InitializeComponent();
}
