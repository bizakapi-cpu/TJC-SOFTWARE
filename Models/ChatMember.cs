using CommunityToolkit.Mvvm.ComponentModel;

namespace TJCServiceDesk.Models;

public partial class ChatMember : ObservableObject
{
    [ObservableProperty] private string userName = "";
    [ObservableProperty] private string userTag = "";
    [ObservableProperty] private string profilePicturePath = "";
    public string ProfilePictureData { get; set; } = "";
    [ObservableProperty] private bool isOnline;
    public DateTime? LastSeenUtc { get; set; }
    public string DisplayTag => UserTag.Contains('#') ? UserTag : $"{UserName}#{UserTag}";

    partial void OnUserNameChanged(string value) => OnPropertyChanged(nameof(DisplayTag));
    partial void OnUserTagChanged(string value) => OnPropertyChanged(nameof(DisplayTag));
}
