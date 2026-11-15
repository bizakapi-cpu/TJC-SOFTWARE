using System.Collections.Concurrent;
using System.Diagnostics;
using TJCServiceDesk.Models;

namespace TJCServiceDesk.Services;

public sealed class SupabaseChatBackend(SupabaseService supabase, SqliteDataService data) : IChatBackend
{
    private readonly SupabaseService _supabase = supabase;
    private readonly SqliteDataService _data = data;
    private readonly ConcurrentDictionary<string, byte> _activeConversations = new(StringComparer.Ordinal);
    private CancellationTokenSource? _lifetime;
    private Task? _pollTask;
    private Task? _realtimeTask;
    private readonly ConcurrentDictionary<string, Task> _conversationRealtimeTasks = new(StringComparer.Ordinal);
    private string _localUserTag = "";

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChatMember>? MemberPresenceReceived;
    public event Action<Exception>? TransportError;

    public async Task StartAsync(ChatMember localMember, CancellationToken cancellationToken = default)
    {
        _localUserTag = localMember.UserTag.Contains('#') ? localMember.UserTag : $"{localMember.UserName}#{localMember.UserTag}";
        if (_lifetime is not null || !_supabase.IsConfigured) return;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try { await _supabase.UpsertProfileAsync(_localUserTag, localMember.UserName, localMember.ProfilePicturePath, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { PublishTransportError(ex); }
        _pollTask = PollLoopAsync(_lifetime.Token);
        _realtimeTask = RealtimeLoopAsync(_lifetime.Token);
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string channel, CancellationToken cancellationToken = default)
    {
        if (!_supabase.IsConfigured) return await _data.GetMessagesAsync(channel, cancellationToken);
        var messages = await _supabase.GetMessagesAsync(channel, cancellationToken: cancellationToken);
        foreach (var message in messages) await CacheAndNotifyAsync(message, false, cancellationToken);
        return messages;
    }

    public async Task<IReadOnlyList<ChatMessage>> GetDirectMessagesAsync(string localUserTag, string remoteUserTag, CancellationToken cancellationToken = default)
    {
        var conversation = GetConversationId(localUserTag, remoteUserTag);
        if (_activeConversations.TryAdd(conversation, 0) && _supabase.IsConfigured && _lifetime is not null)
            _conversationRealtimeTasks.TryAdd(conversation, RealtimeConversationLoopAsync(conversation, _lifetime.Token));
        if (!_supabase.IsConfigured) return await _data.GetDirectMessagesAsync(conversation, cancellationToken);
        var messages = await _supabase.GetMessagesAsync("", conversation, cancellationToken);
        foreach (var message in messages) await CacheAndNotifyAsync(message, false, cancellationToken);
        return messages;
    }

    public async Task SendMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (!_supabase.IsConfigured) throw new InvalidOperationException("Live announcements require configured Supabase credentials.");
        if (message.IsDirectMessage || message.Channel != "#mededelingen") throw new ArgumentException("Only announcement messages can use this operation.", nameof(message));
        if (!string.Equals(message.UserTag, _localUserTag, StringComparison.Ordinal)) throw new UnauthorizedAccessException("The message sender does not match the current profile.");
        var senderRole = await _supabase.GetRoleAsync(_localUserTag, cancellationToken);
        if (senderRole is not ("owner" or "announcer")) throw new UnauthorizedAccessException("Only owners and announcers can post announcements.");
        var avatarUrl = await _supabase.ResolveAvatarUrlAsync(message.UserTag, message.ProfilePicturePath, cancellationToken);
        var sent = await _supabase.SendMessageAsync(message, avatarUrl, cancellationToken);
        await CacheAndNotifyAsync(sent, true, cancellationToken);
    }

    public async Task SendDirectMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (!_supabase.IsConfigured) throw new InvalidOperationException("Live private messages require configured Supabase credentials.");
        if (!message.IsDirectMessage || string.IsNullOrWhiteSpace(message.ConversationId)
            || !message.ConversationId.Equals(GetConversationId(message.UserTag, message.RecipientTag), StringComparison.Ordinal))
            throw new ArgumentException("A direct message must target the canonical sender/recipient conversation.", nameof(message));
        if (!string.Equals(message.UserTag, _localUserTag, StringComparison.Ordinal)) throw new UnauthorizedAccessException("The message sender does not match the current profile.");
        _activeConversations.TryAdd(message.ConversationId, 0);
        var avatarUrl = await _supabase.ResolveAvatarUrlAsync(message.UserTag, message.ProfilePicturePath, cancellationToken);
        var sent = await _supabase.SendMessageAsync(message, avatarUrl, cancellationToken);
        await CacheAndNotifyAsync(sent, true, cancellationToken);
    }

    public async Task<int> GetUnreadAnnouncementCountAsync(long afterMessageId, CancellationToken cancellationToken = default) =>
        await _data.GetUnreadAnnouncementCountAsync(afterMessageId, cancellationToken);

    public async Task<long> GetLatestAnnouncementMessageIdAsync(CancellationToken cancellationToken = default) =>
        await _data.GetLatestAnnouncementMessageIdAsync(cancellationToken);

    public async Task<IReadOnlyList<ChatMember>> GetFriendsAsync(CancellationToken cancellationToken = default)
    {
        if (!_supabase.IsConfigured) return await _data.GetFriendsAsync(cancellationToken);
        var friends = await _supabase.GetFriendsAsync(_localUserTag, cancellationToken);
        foreach (var friend in friends) await _data.AddFriendAsync(friend, cancellationToken);
        return friends;
    }

    public async Task AddFriendAsync(ChatMember friend, CancellationToken cancellationToken = default)
    {
        if (!_supabase.IsConfigured) throw new InvalidOperationException("Adding verified friends requires configured Supabase credentials.");
        var verified = await _supabase.AddFriendAsync(_localUserTag, friend.UserTag, cancellationToken);
        foreach (var profile in verified) await _data.AddFriendAsync(profile, cancellationToken);
    }

    public Task<string> GetRoleAsync(string userTag, CancellationToken cancellationToken = default) => _supabase.IsConfigured
        ? _supabase.GetRoleAsync(userTag, cancellationToken)
        : Task.FromResult(string.Equals(userTag, SupabaseService.OWNER_TAG, StringComparison.OrdinalIgnoreCase) && !SupabaseService.OWNER_TAG.StartsWith("PLAK_HIER", StringComparison.Ordinal) ? "owner" : "member");

    public Task SetAnnouncerAsync(string ownerTag, string userTag, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(ownerTag, _localUserTag, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ownerTag, SupabaseService.OWNER_TAG, StringComparison.OrdinalIgnoreCase)
            || SupabaseService.OWNER_TAG.StartsWith("PLAK_HIER", StringComparison.Ordinal))
            throw new InvalidOperationException("Set OWNER_TAG in SupabaseService.cs to the current owner's user_tag before managing announcers.");
        return _supabase.SetAnnouncerAsync(ownerTag, userTag, enabled, cancellationToken);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var presencePolls = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await PollConversationAsync("#mededelingen", null, cancellationToken);
                foreach (var conversation in _activeConversations.Keys)
                    await PollConversationAsync("", conversation, cancellationToken);
                if (++presencePolls >= 15)
                {
                    presencePolls = 0;
                    await _supabase.SetOnlineAsync(_localUserTag, cancellationToken);
                    foreach (var friend in await _supabase.GetFriendsAsync(_localUserTag, cancellationToken))
                        PublishPresence(friend);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) { PublishTransportError(ex); }
        }
    }

    private async Task PollConversationAsync(string channel, string? conversation, CancellationToken cancellationToken)
    {
        var messages = await _supabase.GetMessagesAsync(channel, conversation, cancellationToken);
        foreach (var message in messages) await CacheAndNotifyAsync(message, false, cancellationToken);
    }

    private async Task RealtimeLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _supabase.SubscribeMessagesAsync(message => HandleRealtimeMessageAsync(message, cancellationToken), cancellationToken, "channel=eq.#mededelingen");
                if (!cancellationToken.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Debug.WriteLine($"Supabase announcement realtime is unavailable; polling remains active: {ex}");
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            }
        }
    }

    private async Task RealtimeConversationLoopAsync(string conversationId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _supabase.SubscribeMessagesAsync(message => HandleRealtimeMessageAsync(message, cancellationToken), cancellationToken, $"channel=eq.{conversationId}");
                if (!cancellationToken.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Debug.WriteLine($"Supabase DM realtime is unavailable for {conversationId}; polling remains active: {ex}");
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            }
        }
    }

    private async Task HandleRealtimeMessageAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        try { await CacheAndNotifyAsync(message, false, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { PublishTransportError(ex); }
    }

    private async Task CacheAndNotifyAsync(ChatMessage message, bool notifyLocalSender, CancellationToken cancellationToken)
    {
        if (message.IsDirectMessage)
        {
            var peerTag = string.Equals(message.UserTag, _localUserTag, StringComparison.Ordinal)
                ? message.RecipientTag
                : message.UserTag;
            if (!_activeConversations.ContainsKey(message.ConversationId)
                || !message.ConversationId.Equals(GetConversationId(_localUserTag, peerTag), StringComparison.Ordinal)) return;
        }
        else if (message.Channel != "#mededelingen") return;

        var isNew = await _data.SaveMessageIfNewAsync(message, cancellationToken);
        if (isNew && (notifyLocalSender || message.UserTag != _localUserTag)) PublishMessage(message);
    }

    private void PublishMessage(ChatMessage message)
    {
        var handlers = MessageReceived;
        if (handlers is null) return;
        foreach (EventHandler<ChatMessage> handler in handlers.GetInvocationList())
        {
            try { handler(this, message); }
            catch (Exception ex) { Debug.WriteLine($"Chat message observer failed: {ex}"); }
        }
    }

    private void PublishPresence(ChatMember member)
    {
        var handlers = MemberPresenceReceived;
        if (handlers is null) return;
        foreach (EventHandler<ChatMember> handler in handlers.GetInvocationList())
        {
            try { handler(this, member); }
            catch (Exception ex) { Debug.WriteLine($"Chat presence observer failed: {ex}"); }
        }
    }

    private void PublishTransportError(Exception exception)
    {
        Debug.WriteLine($"Supabase chat transport error: {exception}");
        var handlers = TransportError;
        if (handlers is null) return;
        foreach (Action<Exception> handler in handlers.GetInvocationList())
        {
            try { handler(exception); }
            catch (Exception observerException) { Debug.WriteLine($"Chat transport-error observer failed: {observerException}"); }
        }
    }

    public static string GetConversationId(string firstTag, string secondTag)
    {
        var ordered = string.CompareOrdinal(firstTag, secondTag) < 0 ? new[] { firstTag, secondTag } : new[] { secondTag, firstTag };
        return $"dm_{ordered[0]}_{ordered[1]}";
    }

    public async ValueTask DisposeAsync()
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is null) return;

        try
        {
            await lifetime.CancelAsync();
            var tasks = new[] { _pollTask, _realtimeTask }.OfType<Task>().Concat(_conversationRealtimeTasks.Values);
            foreach (var task in tasks)
            {
                try { await task; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { PublishTransportError(ex); }
            }
        }
        finally
        {
            _conversationRealtimeTasks.Clear();
            lifetime.Dispose();
            _pollTask = null;
            _realtimeTask = null;
        }
    }
}
