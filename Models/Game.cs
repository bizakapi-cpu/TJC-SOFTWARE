namespace TJCServiceDesk.Models;

public sealed class Game
{
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string AppId { get; set; } = "";
    public string CoverPath { get; set; } = "";
    public string CustomCoverPath { get; set; } = "";
    public string Source { get; set; } = "Local";
}

public sealed class ChatMessage : EventArgs
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    public string Channel { get; set; } = "#mededelingen";
    public bool IsDirectMessage { get; set; }
    public string ConversationId { get; set; } = "";
    public string RecipientTag { get; set; } = "";
    public string UserName { get; set; } = "";
    public string UserTag { get; set; } = "";
    public string ProfilePicturePath { get; set; } = "";
    public string ProfilePictureData { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime SentAt { get; set; }
    public string TimeLabel => SentAt.ToString("HH:mm");
}
