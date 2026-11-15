using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using TJCServiceDesk.Models;

namespace TJCServiceDesk.Services;

public sealed class LocalLanChatBackend(SqliteDataService data) : IChatBackend, IDisposable
{
    private const int Port = 47821;
    private const int MaximumPacketBytes = 16_384;
    private readonly SqliteDataService _data = data;
    private UdpClient? _listener;
    private CancellationTokenSource? _listenerCancellation;
    private string? _localInstanceId;
    private Task? _receiveTask;
    private Task? _presenceTask;
    private ChatMember? _localMember;

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChatMember>? MemberPresenceReceived;
    public event Action<Exception>? TransportError;

    public async Task StartAsync(ChatMember localMember, CancellationToken cancellationToken = default)
    {
        if (_listener is not null) return;
        _localMember = localMember;
        _localInstanceId = await _data.GetSettingAsync("ChatInstanceId") ?? Guid.NewGuid().ToString("N");
        await _data.SetSettingAsync("ChatInstanceId", _localInstanceId);
        _listenerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
        _listener.EnableBroadcast = true;
        _receiveTask = ReceiveLoopAsync(_listener, _listenerCancellation.Token);
        _presenceTask = PresenceLoopAsync(_listenerCancellation.Token);
        try { await BroadcastPresenceAsync(_listenerCancellation.Token); }
        catch (SocketException ex) { TransportError?.Invoke(ex); }
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string channel, CancellationToken cancellationToken = default) =>
        await _data.GetMessagesAsync(channel, cancellationToken);

    public async Task<IReadOnlyList<ChatMessage>> GetDirectMessagesAsync(string localUserTag, string remoteUserTag, CancellationToken cancellationToken = default) =>
        await _data.GetDirectMessagesAsync(GetConversationId(localUserTag, remoteUserTag), cancellationToken);

    public Task<int> GetUnreadAnnouncementCountAsync(long afterMessageId, CancellationToken cancellationToken = default) => _data.GetUnreadAnnouncementCountAsync(afterMessageId, cancellationToken);

    public Task<long> GetLatestAnnouncementMessageIdAsync(CancellationToken cancellationToken = default) => _data.GetLatestAnnouncementMessageIdAsync(cancellationToken);

    public async Task SendDirectMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (!message.IsDirectMessage || string.IsNullOrWhiteSpace(message.ConversationId)) throw new ArgumentException("A direct message must identify its conversation.", nameof(message));
        await _data.SaveMessageAsync(message, cancellationToken);
        MessageReceived?.Invoke(this, message);
    }

    public async Task SendMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (message.IsDirectMessage || message.Channel != "#mededelingen") throw new ArgumentException("Only announcements can be broadcast on the community channel.", nameof(message));
        await _data.SaveMessageAsync(message, cancellationToken);
        MessageReceived?.Invoke(this, message);
        var broadcastMessage = new ChatMessage
        {
            MessageId = message.MessageId, Channel = message.Channel, UserName = message.UserName,
            UserTag = message.UserTag, ProfilePictureData = CreateAvatarData(message.ProfilePicturePath),
            Text = message.Text, SentAt = message.SentAt
        };
        var envelope = new LanEnvelope("message", _localInstanceId ?? "", broadcastMessage);
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope);
        if (payload.Length > MaximumPacketBytes && broadcastMessage.ProfilePictureData.Length > 0)
        {
            broadcastMessage.ProfilePictureData = "";
            payload = JsonSerializer.SerializeToUtf8Bytes(envelope);
        }
        if (payload.Length > MaximumPacketBytes) throw new InvalidOperationException("The message is too large to broadcast on the local network.");
        using var sender = new UdpClient { EnableBroadcast = true };
        await sender.SendAsync(payload, new IPEndPoint(IPAddress.Broadcast, Port), cancellationToken);
    }

    public async Task<IReadOnlyList<ChatMember>> GetFriendsAsync(CancellationToken cancellationToken = default) => await _data.GetFriendsAsync(cancellationToken);

    public Task<string> GetRoleAsync(string userTag, CancellationToken cancellationToken = default) => Task.FromResult("member");

    public Task SetAnnouncerAsync(string ownerTag, string userTag, bool enabled, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Role management requires the Supabase backend."));

    public Task AddFriendAsync(ChatMember friend, CancellationToken cancellationToken = default) => _data.AddFriendAsync(friend, cancellationToken);

    private async Task ReceiveLoopAsync(UdpClient listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await listener.ReceiveAsync(cancellationToken);
                if (result.Buffer.Length == 0 || result.Buffer.Length > MaximumPacketBytes) continue;
                LanEnvelope? envelope;
                try { envelope = JsonSerializer.Deserialize<LanEnvelope>(result.Buffer); }
                catch (JsonException) { continue; }
                if (envelope is null || envelope.InstanceId == _localInstanceId) continue;
                if (envelope.Kind == "presence" && envelope.Member is not null)
                {
                    envelope.Member.ProfilePicturePath = await CacheAvatarAsync(envelope.Member.ProfilePictureData, envelope.Member.UserTag, cancellationToken);
                    envelope.Member.ProfilePictureData = "";
                    MemberPresenceReceived?.Invoke(this, envelope.Member);
                    continue;
                }
                if (envelope.Kind != "message" || envelope.Message is null || envelope.Message.IsDirectMessage || envelope.Message.Channel != "#mededelingen" || string.IsNullOrWhiteSpace(envelope.Message.MessageId)) continue;
                envelope.Message.ProfilePicturePath = await CacheAvatarAsync(envelope.Message.ProfilePictureData, envelope.Message.UserTag, cancellationToken);
                envelope.Message.ProfilePictureData = "";
                if (await _data.SaveMessageIfNewAsync(envelope.Message, cancellationToken))
                    MessageReceived?.Invoke(this, envelope.Message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task PresenceLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try { await BroadcastPresenceAsync(cancellationToken); }
                catch (SocketException ex) { TransportError?.Invoke(ex); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task BroadcastPresenceAsync(CancellationToken cancellationToken)
    {
        if (_localMember is null) return;
        _localMember.UserName = await _data.GetSettingAsync("UserName") ?? _localMember.UserName;
        _localMember.UserTag = await _data.GetSettingAsync("UserTag") ?? _localMember.UserTag;
        _localMember.ProfilePicturePath = await _data.GetSettingAsync("ProfilePicturePath") ?? _localMember.ProfilePicturePath;
        var member = new ChatMember
        {
            UserName = _localMember.UserName, UserTag = _localMember.UserTag,
            ProfilePictureData = CreateAvatarData(_localMember.ProfilePicturePath), IsOnline = true
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(new LanEnvelope("presence", _localInstanceId ?? "", null, member));
        using var sender = new UdpClient { EnableBroadcast = true };
        await sender.SendAsync(payload, new IPEndPoint(IPAddress.Broadcast, Port), cancellationToken);
    }

    private static string CreateAvatarData(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return "";
        try
        {
            using var source = Image.FromFile(path);
            var side = Math.Min(source.Width, source.Height);
            var crop = new Rectangle((source.Width - side) / 2, (source.Height - side) / 2, side, side);
            using var thumbnail = new Bitmap(64, 64, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(thumbnail))
            {
                graphics.Clear(Color.FromArgb(52, 52, 64));
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, new Rectangle(0, 0, 64, 64), crop, GraphicsUnit.Pixel);
            }
            using var stream = new MemoryStream();
            thumbnail.Save(stream, ImageFormat.Jpeg);
            return stream.Length <= 8192 ? Convert.ToBase64String(stream.ToArray()) : "";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException or ExternalException) { return ""; }
    }

    private static async Task<string> CacheAvatarAsync(string data, string userTag, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(data) || data.Length > 12_000) return "";
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data); }
        catch (FormatException) { return ""; }
        if (bytes.Length < 3 || bytes.Length > 8192 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF) return "";
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TJCServiceDesk", "chat-avatars");
        Directory.CreateDirectory(folder);
        var identityHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userTag)));
        var imageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var path = Path.Combine(folder, identityHash + "-" + imageHash + ".jpg");
        if (!System.IO.File.Exists(path)) await System.IO.File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return path;
    }

    public void Dispose()
    {
        if (_listenerCancellation is not null) _listenerCancellation.Cancel();
        _listener?.Dispose();
        _listenerCancellation?.Dispose();
        _listenerCancellation = null;
        _listener = null;
    }

    public async ValueTask DisposeAsync()
    {
        var receiveTask = _receiveTask;
        var presenceTask = _presenceTask;
        Dispose();
        foreach (var task in new[] { receiveTask, presenceTask }.Where(task => task is not null))
        {
            try { await task!; }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static string GetConversationId(string firstTag, string secondTag) =>
        string.CompareOrdinal(firstTag, secondTag) < 0 ? $"{firstTag}|{secondTag}" : $"{secondTag}|{firstTag}";

    private sealed record LanEnvelope(string Kind, string InstanceId, ChatMessage? Message, ChatMember? Member = null);
}
