using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using Supabase.Realtime.PostgresChanges;
using TJCServiceDesk.Models;
using SupabaseSdkClient = Supabase.Client;
using SupabaseSdkOptions = Supabase.SupabaseOptions;

namespace TJCServiceDesk.Services;

public sealed class SupabaseService
{
    public const string SUPABASE_URL = "https://sqcnrfxrqegivfrpupri.supabase.co";
    public const string SUPABASE_ANON_KEY = "sb_publishable_YtLM4ZwkvZOFE-Fx2OF7Qg_tFMt9O3Y";
    public const string OWNER_TAG = "PLAK_HIER_JE_TAG";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _baseUrl = SUPABASE_URL.TrimEnd('/');
    private readonly SettingsService _settings;
    private readonly SupabaseSdkClient _client;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _isInitialized;

    public SupabaseService(SettingsService settings)
    {
        _settings = settings;
        _client = new SupabaseSdkClient(SUPABASE_URL, SUPABASE_ANON_KEY, new SupabaseSdkOptions { AutoConnectRealtime = false });
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var displayName = (await _settings.GetAsync("UserName"))?.Trim();
        if (string.IsNullOrWhiteSpace(displayName)) displayName = "Guest";
        var tagSuffix = await _settings.GetAsync("UserTag");
        if (string.IsNullOrWhiteSpace(tagSuffix))
        {
            tagSuffix = RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
            await _settings.SetAsync("UserTag", tagSuffix);
        }
        var userTag = $"{displayName}#{tagSuffix}";
        var avatarPath = await _settings.GetAsync("ProfilePicturePath") ?? "";
        await UpsertProfileAsync(userTag, displayName, avatarPath, cancellationToken);
    }

    public bool IsConfigured => Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)
        && !string.IsNullOrWhiteSpace(SUPABASE_ANON_KEY)
        && !SUPABASE_URL.Contains("PLAK_HIER", StringComparison.Ordinal)
        && !SUPABASE_ANON_KEY.Contains("PLAK_HIER", StringComparison.Ordinal);

    private void EnsureConfigured()
    {
        if (!IsConfigured) throw new InvalidOperationException("Configure a Supabase project API URL and anon/publishable key in SupabaseService.cs.");
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized) return;
        EnsureConfigured();
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized) return;
            await _client.InitializeAsync();
            _isInitialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, HttpContent? content = null)
    {
        EnsureConfigured();
        var request = new HttpRequestMessage(method, $"{_baseUrl}/rest/v1/{path}") { Content = content };
        request.Headers.Add("apikey", SUPABASE_ANON_KEY);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SUPABASE_ANON_KEY);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            throw new HttpRequestException($"Supabase request failed ({statusCode}): {detail}");
        }
        return response;
    }

    public async Task UpsertProfileAsync(string userTag, string displayName, string avatarPath, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        ValidateTag(userTag);
        var avatarUrl = await ResolveAvatarUrlAsync(userTag, avatarPath, cancellationToken);
        var isOwner = !string.IsNullOrWhiteSpace(OWNER_TAG) && !OWNER_TAG.StartsWith("PLAK_HIER", StringComparison.Ordinal)
            && string.Equals(userTag, OWNER_TAG, StringComparison.OrdinalIgnoreCase);
        var existingProfile = isOwner ? null : await GetProfileAsync(userTag, cancellationToken);
        var profile = new Dictionary<string, object?>
        {
            ["user_tag"] = userTag,
            ["display_name"] = displayName,
            ["avatar_url"] = avatarUrl,
            ["role"] = isOwner ? "owner" : existingProfile?.Role ?? "member",
            ["is_online"] = true
        };
        using var request = CreateRequest(HttpMethod.Post, "profiles?on_conflict=user_tag", JsonContent(profile));
        request.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates,return=minimal");
        using var response = await SendAsync(request, cancellationToken);
    }

    public async Task<string> ResolveAvatarUrlAsync(string userTag, string avatarPath, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        ValidateTag(userTag);
        if (string.IsNullOrWhiteSpace(avatarPath)) return "";
        if (Uri.TryCreate(avatarPath, UriKind.Absolute, out var avatarUri)
            && (avatarUri.Scheme == Uri.UriSchemeHttps || avatarUri.IsLoopback)) return avatarUri.ToString();
        if (!File.Exists(avatarPath)) return "";
        var extension = Path.GetExtension(avatarPath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg")) throw new InvalidOperationException("Profile avatars must be PNG or JPEG files.");
        var objectPath = Uri.EscapeDataString(userTag) + extension;
        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(avatarPath, cancellationToken));
        content.Headers.ContentType = new MediaTypeHeaderValue(extension == ".png" ? "image/png" : "image/jpeg");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/storage/v1/object/avatars/{objectPath}") { Content = content };
        request.Headers.Add("apikey", SUPABASE_ANON_KEY);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SUPABASE_ANON_KEY);
        request.Headers.TryAddWithoutValidation("x-upsert", "true");
        using var response = await SendAsync(request, cancellationToken);
        return $"{_baseUrl}/storage/v1/object/public/avatars/{objectPath}";
    }

    public async Task SetOnlineAsync(string userTag, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        using var request = CreateRequest(HttpMethod.Patch, $"profiles?user_tag=eq.{Uri.EscapeDataString(userTag)}", JsonContent(new { is_online = true }));
        using var response = await SendAsync(request, cancellationToken);
    }

    public async Task SetOfflineAsync(string userTag, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(userTag)) return;
        await EnsureInitializedAsync(cancellationToken);
        using var request = CreateRequest(HttpMethod.Patch, $"profiles?user_tag=eq.{Uri.EscapeDataString(userTag)}", JsonContent(new { is_online = false }));
        using var response = await SendAsync(request, cancellationToken);
    }

    public async Task<SupabaseUserProfile?> GetProfileAsync(string userTag, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        ValidateTag(userTag);
        using var request = CreateRequest(HttpMethod.Get, $"profiles?select=user_tag,display_name,avatar_url,is_online,role&user_tag=eq.{Uri.EscapeDataString(userTag)}&limit=1");
        using var response = await SendAsync(request, cancellationToken);
        var profiles = await response.Content.ReadFromJsonAsync<List<SupabaseUserProfile>>(JsonOptions, cancellationToken);
        return profiles?.FirstOrDefault();
    }

    public async Task<string> GetRoleAsync(string userTag, CancellationToken cancellationToken = default)
    {
        var profile = await GetProfileAsync(userTag, cancellationToken);
        return profile?.Role ?? "member";
    }

    public async Task SetAnnouncerAsync(string ownerTag, string userTag, bool enabled, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        ValidateTag(ownerTag);
        ValidateTag(userTag);
        var owner = await GetProfileAsync(ownerTag, cancellationToken);
        if (owner?.Role != "owner") throw new UnauthorizedAccessException("Only the Supabase owner can manage announcement permissions.");
        var profile = await GetProfileAsync(userTag, cancellationToken) ?? throw new InvalidOperationException("That user_tag does not exist in Supabase profiles.");
        if (string.Equals(profile.Role, "owner", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The owner role cannot be changed here.");
        using var request = CreateRequest(HttpMethod.Patch, $"profiles?user_tag=eq.{Uri.EscapeDataString(userTag)}", JsonContent(new { role = enabled ? "announcer" : "member" }));
        using var response = await SendAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<ChatMember>> AddFriendAsync(string ownerTag, string friendTag, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        ValidateTag(ownerTag);
        ValidateTag(friendTag);
        if (string.Equals(ownerTag, friendTag, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("You cannot add yourself as a friend.");
        var profile = await GetProfileAsync(friendTag, cancellationToken) ?? throw new InvalidOperationException("No Supabase profile exists for that user_tag.");
        using (var request = CreateRequest(HttpMethod.Post, "friends?on_conflict=user_tag,friend_tag", JsonContent(new { user_tag = ownerTag, friend_tag = friendTag })))
        {
            request.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates,return=minimal");
            using var response = await SendAsync(request, cancellationToken);
        }
        return new[] { ToChatMember(profile) };
    }

    public async Task<IReadOnlyList<ChatMember>> GetFriendsAsync(string ownerTag, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        ValidateTag(ownerTag);
        using var request = CreateRequest(HttpMethod.Get, $"friends?select=friend_tag&user_tag=eq.{Uri.EscapeDataString(ownerTag)}");
        using var response = await SendAsync(request, cancellationToken);
        var links = await response.Content.ReadFromJsonAsync<List<FriendLink>>(JsonOptions, cancellationToken) ?? new List<FriendLink>();
        var friends = new List<ChatMember>();
        foreach (var link in links)
        {
            var profile = await GetProfileAsync(link.FriendTag, cancellationToken);
            if (profile is not null) friends.Add(ToChatMember(profile));
        }
        return friends;
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string channel, string? conversationId = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var channelName = string.IsNullOrWhiteSpace(conversationId) ? channel : conversationId;
        if (string.IsNullOrWhiteSpace(channelName)) throw new ArgumentException("A message channel is required.", nameof(channel));

        var filter = $"channel=eq.{Uri.EscapeDataString(channelName)}";
        using var request = CreateRequest(HttpMethod.Get, $"messages?select=id,channel,recipient_tag,user_tag,display_name,avatar_url,content,created_at&{filter}&order=created_at.asc&limit=500");
        using var response = await SendAsync(request, cancellationToken);
        var records = await response.Content.ReadFromJsonAsync<List<SupabaseChatRecord>>(JsonOptions, cancellationToken) ?? new List<SupabaseChatRecord>();
        return records.Select(ToChatMessage).ToArray();
    }

    public async Task<ChatMessage> SendMessageAsync(ChatMessage message, string avatarUrl, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var channel = message.IsDirectMessage ? message.ConversationId : "#mededelingen";
        if (string.IsNullOrWhiteSpace(channel)) throw new ArgumentException("A message channel is required.", nameof(message));
        var body = new Dictionary<string, object?>
        {
            ["channel"] = channel,
            ["recipient_tag"] = message.IsDirectMessage ? message.RecipientTag : null,
            ["user_tag"] = message.UserTag,
            ["display_name"] = message.UserName,
            ["avatar_url"] = avatarUrl,
            ["content"] = message.Text,
            ["created_at"] = message.SentAt.ToUniversalTime()
        };
        using var request = CreateRequest(HttpMethod.Post, "messages?select=id,channel,recipient_tag,user_tag,display_name,avatar_url,content,created_at", JsonContent(body));
        request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
        using var response = await SendAsync(request, cancellationToken);
        var records = await response.Content.ReadFromJsonAsync<List<SupabaseChatRecord>>(JsonOptions, cancellationToken);
        return records?.FirstOrDefault() is { } record ? ToChatMessage(record) : message;
    }

    public async Task SubscribeMessagesAsync(Func<ChatMessage, Task> onMessage, CancellationToken cancellationToken, string? filter = null)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        await EnsureInitializedAsync(cancellationToken);
        await _client.Realtime.ConnectAsync();

        var channel = _client.Realtime.Channel($"messages-{Guid.NewGuid():N}");
        var listenType = PostgresChangesOptions.ListenType.Inserts;
        channel.Register(new PostgresChangesOptions("public", "messages", listenType, filter ?? "", new Dictionary<string, string>()));
        channel.AddPostgresChangeHandler(listenType, async (_, response) =>
        {
            try
            {
                var record = response.Model<SupabaseChatRecord>();
                if (record is null || string.IsNullOrWhiteSpace(record.Id))
                {
                    Debug.WriteLine("Supabase Realtime returned an insert without a valid message ID.");
                    return;
                }
                await onMessage(ToChatMessage(record));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The subscription is being shut down.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Supabase Realtime message handler failed: {ex}");
            }
        });

        try
        {
            await channel.Subscribe();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            try
            {
                channel.Unsubscribe();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Debug.WriteLine("Supabase Realtime channel cleanup was cancelled during shutdown.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Supabase Realtime channel cleanup failed: {ex}");
            }
        }
    }

    private static ChatMember ToChatMember(SupabaseUserProfile profile) => new()
    {
        UserName = profile.DisplayName,
        UserTag = profile.UserTag,
        ProfilePicturePath = profile.AvatarUrl,
        IsOnline = profile.IsOnline,
        LastSeenUtc = profile.IsOnline ? DateTime.UtcNow : null
    };

    private static ChatMessage ToChatMessage(SupabaseChatRecord record) => new()
    {
        MessageId = record.Id,
        Channel = record.Channel ?? "",
        IsDirectMessage = record.Channel?.StartsWith("dm_", StringComparison.Ordinal) == true,
        ConversationId = record.Channel?.StartsWith("dm_", StringComparison.Ordinal) == true ? record.Channel : "",
        RecipientTag = record.RecipientTag ?? "",
        UserTag = record.UserTag,
        UserName = record.DisplayName,
        ProfilePicturePath = record.AvatarUrl ?? "",
        Text = record.Content,
        SentAt = record.CreatedAt.LocalDateTime
    };

    private static void ValidateTag(string userTag)
    {
        if (string.IsNullOrWhiteSpace(userTag) || userTag.Length > 120 || userTag.Contains(',') || userTag.Contains('(') || userTag.Contains(')'))
            throw new ArgumentException("A valid user_tag is required.", nameof(userTag));
    }

    private sealed class FriendLink
    {
        [JsonPropertyName("friend_tag")] public string FriendTag { get; set; } = "";
    }
}

public sealed class SupabaseUserProfile
{
    [JsonPropertyName("user_tag")] public string UserTag { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("avatar_url")] public string AvatarUrl { get; set; } = "";
    [JsonPropertyName("is_online")] public bool IsOnline { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; } = "member";
}

[Table("messages")]
public sealed class SupabaseChatRecord : BaseModel
{
    [PrimaryKey("id", false), Column("id"), JsonPropertyName("id")] public string Id { get; set; } = "";
    [Column("channel"), JsonPropertyName("channel")] public string? Channel { get; set; }
    [Column("recipient_tag"), JsonPropertyName("recipient_tag")] public string? RecipientTag { get; set; }
    [Column("user_tag"), JsonPropertyName("user_tag")] public string UserTag { get; set; } = "";
    [Column("display_name"), JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [Column("avatar_url"), JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
    [Column("content"), JsonPropertyName("content")] public string Content { get; set; } = "";
    [Column("created_at"), JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
}
