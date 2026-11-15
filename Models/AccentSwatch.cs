using CommunityToolkit.Mvvm.ComponentModel;

namespace TJCServiceDesk.Models;

public partial class AccentSwatch(string name, string hex) : ObservableObject
{
    public string Name { get; } = name;
    public string Hex { get; } = hex;
    [ObservableProperty] private bool isSelected;
}
