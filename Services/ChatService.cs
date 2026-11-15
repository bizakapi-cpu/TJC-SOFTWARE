using TJCServiceDesk.Models;

namespace TJCServiceDesk.Services;

public sealed class ChatService(IChatBackend backend)
{
    private readonly IChatBackend _backend = backend;
    public event EventHandler<ChatMessage>? MessageReceived
    {
        add => _backend.MessageReceived += value;
        remove => _backend.MessageReceived -= value;
    }
    public event EventHandler<ChatMember>? MemberPresenceReceived
    {
        add => _backend.MemberPresenceReceived += value;
        remove => _backend.MemberPresenceReceived -= value;
    }
    public event Action<Exception>? TransportError
    {
        add => _backend.TransportError += value;
        remove => _backend.TransportError -= value;
    }

    public Task StartAsync(ChatMember localMember, CancellationToken cancellationToken = default) => _backend.StartAsync(localMember, cancellationToken);
    public ValueTask StopAsync() => _backend.DisposeAsync();
    public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string channel, CancellationToken cancellationToken = default) => _backend.GetMessagesAsync(channel, cancellationToken);
    public Task<IReadOnlyList<ChatMessage>> GetDirectMessagesAsync(string localUserTag, string remoteUserTag, CancellationToken cancellationToken = default) => _backend.GetDirectMessagesAsync(localUserTag, remoteUserTag, cancellationToken);
    public Task SendMessageAsync(ChatMessage message, CancellationToken cancellationToken = default) => _backend.SendMessageAsync(message, cancellationToken);
    public Task SendDirectMessageAsync(ChatMessage message, CancellationToken cancellationToken = default) => _backend.SendDirectMessageAsync(message, cancellationToken);
    public Task<int> GetUnreadAnnouncementCountAsync(long afterMessageId, CancellationToken cancellationToken = default) => _backend.GetUnreadAnnouncementCountAsync(afterMessageId, cancellationToken);
    public Task<long> GetLatestAnnouncementMessageIdAsync(CancellationToken cancellationToken = default) => _backend.GetLatestAnnouncementMessageIdAsync(cancellationToken);
    public Task<string> GetRoleAsync(string userTag, CancellationToken cancellationToken = default) => _backend.GetRoleAsync(userTag, cancellationToken);
    public Task SetAnnouncerAsync(string ownerTag, string userTag, bool enabled, CancellationToken cancellationToken = default) => _backend.SetAnnouncerAsync(ownerTag, userTag, enabled, cancellationToken);
    public Task<IReadOnlyList<ChatMember>> GetFriendsAsync(CancellationToken cancellationToken = default) => _backend.GetFriendsAsync(cancellationToken);
    public Task AddFriendAsync(ChatMember friend, CancellationToken cancellationToken = default) => _backend.AddFriendAsync(friend, cancellationToken);
}
