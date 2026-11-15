using TJCServiceDesk.Models;

namespace TJCServiceDesk.Services;

public interface IChatBackend : IAsyncDisposable
{
    event EventHandler<ChatMessage>? MessageReceived;
    event EventHandler<ChatMember>? MemberPresenceReceived;
    event Action<Exception>? TransportError;
    Task StartAsync(ChatMember localMember, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string channel, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessage>> GetDirectMessagesAsync(string localUserTag, string remoteUserTag, CancellationToken cancellationToken = default);
    Task SendMessageAsync(ChatMessage message, CancellationToken cancellationToken = default);
    Task SendDirectMessageAsync(ChatMessage message, CancellationToken cancellationToken = default);
    Task<int> GetUnreadAnnouncementCountAsync(long afterMessageId, CancellationToken cancellationToken = default);
    Task<long> GetLatestAnnouncementMessageIdAsync(CancellationToken cancellationToken = default);
    Task<string> GetRoleAsync(string userTag, CancellationToken cancellationToken = default);
    Task SetAnnouncerAsync(string ownerTag, string userTag, bool enabled, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMember>> GetFriendsAsync(CancellationToken cancellationToken = default);
    Task AddFriendAsync(ChatMember friend, CancellationToken cancellationToken = default);
}
