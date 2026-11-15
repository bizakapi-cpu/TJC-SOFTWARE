using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TJCServiceDesk.Models;
using TJCServiceDesk.Services;

namespace TJCServiceDesk.ViewModels;

public partial class ConnectViewModel : ObservableObject
{
    private readonly ChatService _chat;
    private readonly SettingsService _settings;
    private readonly SupabaseService _supabase;
    private const string AnnouncementsChannel = "#mededelingen";
    private const string AnnouncementReadSetting = "AnnouncementsReadThroughId";
    private DispatcherTimer? _presenceTimer;
    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public ObservableCollection<ChatMember> Friends { get; } = new();
    public ObservableCollection<ChatMember> CommunityMembers { get; } = new();
    [ObservableProperty] private string? selectedChannel = AnnouncementsChannel;
    [ObservableProperty] private ChatMember? selectedFriend;
    [ObservableProperty] private string messageText = "";
    [ObservableProperty] private string friendTagInput = "";
    [ObservableProperty] private int unreadAnnouncementCount;
    [ObservableProperty] private string currentChatTitle = AnnouncementsChannel;
    private string _currentUserRole = "member";
    public string CurrentUserRole
    {
        get => _currentUserRole;
        private set
        {
            if (!SetProperty(ref _currentUserRole, value)) return;
            OnPropertyChanged(nameof(IsAnnouncementWriter));
            OnPropertyChanged(nameof(IsOwner));
            OnPropertyChanged(nameof(CanManageAnnouncementRoles));
            OnPropertyChanged(nameof(CanSendCurrentChat));
            OnPropertyChanged(nameof(ShowAnnouncementReadOnlyNotice));
        }
    }
    private string _announcerTagInput = "";
    public string AnnouncerTagInput { get => _announcerTagInput; set => SetProperty(ref _announcerTagInput, value); }
    public bool IsAnnouncementWriter => CurrentUserRole is "owner" or "announcer";
    public bool IsOwner => CurrentUserRole == "owner";
    public bool IsAnnouncementSelected => SelectedFriend is null && SelectedChannel == AnnouncementsChannel;
    public bool CanManageAnnouncementRoles => IsOwner && IsAnnouncementSelected;
    public bool CanSendCurrentChat => SelectedFriend is not null || IsAnnouncementWriter;
    public bool ShowAnnouncementReadOnlyNotice => IsAnnouncementSelected && !IsAnnouncementWriter;
    [ObservableProperty] private string messagePlaceholder = "Bericht sturen in #mededelingen...";
    [ObservableProperty] private bool isMessageTextEmpty = true;
    public bool IsUnreadAnnouncementVisible => UnreadAnnouncementCount > 0;
    private bool _isActive;
    [ObservableProperty] private string userName = "Guest";
    [ObservableProperty] private string userTag = "";
    [ObservableProperty] private string profilePicturePath = "";
    [ObservableProperty] private string status = "Supabase Connect is not configured.";

    public ConnectViewModel(ChatService chat, SettingsService settings, SupabaseService supabase)
    {
        _chat = chat;
        _settings = settings;
        _supabase = supabase;
        _chat.MessageReceived += OnMessageReceived;
        _chat.MemberPresenceReceived += OnMemberPresenceReceived;
        _chat.TransportError += OnTransportError;
    }

    partial void OnUnreadAnnouncementCountChanged(int value) => OnPropertyChanged(nameof(IsUnreadAnnouncementVisible));
    partial void OnMessageTextChanged(string value) => IsMessageTextEmpty = string.IsNullOrEmpty(value);
    partial void OnSelectedChannelChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        SelectedFriend = null;
        CurrentChatTitle = value;
        MessagePlaceholder = "Bericht sturen in #mededelingen...";
        OnPropertyChanged(nameof(IsAnnouncementSelected));
        OnPropertyChanged(nameof(CanManageAnnouncementRoles));
        OnPropertyChanged(nameof(CanSendCurrentChat));
        OnPropertyChanged(nameof(ShowAnnouncementReadOnlyNotice));
        _ = LoadChannelAsync(value);
    }

    partial void OnSelectedFriendChanged(ChatMember? value)
    {
        if (value is null) return;
        SelectedChannel = null;
        CurrentChatTitle = $"@{value.UserName}";
        MessagePlaceholder = $"Privébericht sturen naar @{value.UserName}...";
        Status = "Privéchats worden live via Supabase gesynchroniseerd.";
        OnPropertyChanged(nameof(IsAnnouncementSelected));
        OnPropertyChanged(nameof(CanManageAnnouncementRoles));
        OnPropertyChanged(nameof(CanSendCurrentChat));
        OnPropertyChanged(nameof(ShowAnnouncementReadOnlyNotice));
        _ = LoadDirectMessagesAsync(value);
    }

    public void SetActive(bool active)
    {
        _isActive = active;
        if (active && SelectedFriend is null) _ = MarkAnnouncementsReadAsync();
    }

    public async Task StopAsync()
    {
        _isActive = false;
        _presenceTimer?.Stop();
        _presenceTimer = null;
        _chat.MessageReceived -= OnMessageReceived;
        _chat.MemberPresenceReceived -= OnMemberPresenceReceived;
        _chat.TransportError -= OnTransportError;
        await _chat.StopAsync();
    }

    [RelayCommand]
    private void OpenAnnouncements()
    {
        SelectedFriend = null;
        if (SelectedChannel == AnnouncementsChannel) _ = MarkAnnouncementsReadAsync();
        else SelectedChannel = AnnouncementsChannel;
    }

    [RelayCommand]
    private void OpenDirectMessage(ChatMember? friend)
    {
        if (friend is not null) SelectedFriend = friend;
    }

    public async Task InitializeAsync()
    {
        UserName = await _settings.GetAsync("UserName") ?? "Guest";
        UserTag = $"{UserName}#{await _settings.GetAsync("UserTag") ?? "0000"}";
        ProfilePicturePath = await _settings.GetAsync("ProfilePicturePath") ?? "";
        try
        {
            await _chat.StartAsync(new ChatMember { UserName = UserName, UserTag = UserTag, ProfilePicturePath = ProfilePicturePath, IsOnline = true });
            CurrentUserRole = await _chat.GetRoleAsync(UserTag);
            Status = _supabase.IsConfigured
                ? "Supabase Connect actief; polling controleert iedere 2 seconden op nieuwe berichten."
                : "Supabase is niet geconfigureerd. Live chat, vrienden en rollen zijn uitgeschakeld.";
        }
        catch (Exception ex) { Status = $"Supabase Connect kon niet starten: {ex.Message}"; }
        await LoadFriendsAsync();
        var readThrough = long.TryParse(await _settings.GetAsync(AnnouncementReadSetting), out var savedReadThrough) ? savedReadThrough : 0;
        UnreadAnnouncementCount = await _chat.GetUnreadAnnouncementCountAsync(readThrough);
        await LoadChannelAsync(AnnouncementsChannel);
        _presenceTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => RefreshPresence(), Application.Current.Dispatcher);
        _presenceTimer.Start();
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = MessageText.Trim();
        if (text.Length == 0 || !CanSendCurrentChat || (SelectedFriend is null && SelectedChannel != AnnouncementsChannel)) return;
        try
        {
            UserName = await _settings.GetAsync("UserName") ?? UserName;
            UserTag = $"{UserName}#{await _settings.GetAsync("UserTag") ?? "0000"}";
            ProfilePicturePath = await _settings.GetAsync("ProfilePicturePath") ?? ProfilePicturePath;
            var isDirectMessage = SelectedFriend is not null;
            var message = new ChatMessage
            {
                Channel = isDirectMessage ? "" : AnnouncementsChannel,
                IsDirectMessage = isDirectMessage,
                ConversationId = isDirectMessage ? GetConversationId(UserTag, SelectedFriend!.UserTag) : "",
                RecipientTag = isDirectMessage ? SelectedFriend!.UserTag : "",
                UserName = UserName, UserTag = UserTag, ProfilePicturePath = ProfilePicturePath,
                Text = text, SentAt = DateTime.Now
            };
            if (isDirectMessage) await _chat.SendDirectMessageAsync(message);
            else await _chat.SendMessageAsync(message);
            MessageText = "";
            Status = isDirectMessage
                ? "Privébericht live verzonden via Supabase."
                : "Mededeling live gepubliceerd via Supabase.";
        }
        catch (Exception ex) { Status = $"Bericht kon niet worden verzonden: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ToggleAnnouncerAsync()
    {
        if (!IsOwner) return;
        var targetTag = AnnouncerTagInput.Trim();
        var separator = targetTag.LastIndexOf('#');
        if (separator <= 0 || targetTag.Length - separator != 5 || !targetTag[(separator + 1)..].All(char.IsDigit))
        {
            Status = "Voer een volledige user_tag in, bijvoorbeeld Naam#4821.";
            return;
        }
        try
        {
            var currentRole = await _chat.GetRoleAsync(targetTag);
            var grant = !string.Equals(currentRole, "announcer", StringComparison.OrdinalIgnoreCase);
            await _chat.SetAnnouncerAsync(UserTag, targetTag, grant);
            Status = grant ? $"{targetTag} mag nu mededelingen plaatsen." : $"Schrijfrechten van {targetTag} zijn ingetrokken.";
            AnnouncerTagInput = "";
        }
        catch (Exception ex) { Status = $"Schrijfrechten konden niet worden gewijzigd: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task AddFriendAsync()
    {
        var identity = FriendTagInput.Trim();
        var separator = identity.LastIndexOf('#');
        if (separator <= 0 || separator == identity.Length - 1)
        {
            Status = "Enter a complete user tag, for example Name#4821.";
            return;
        }
        var name = identity[..separator].Trim();
        var tag = identity[(separator + 1)..].Trim();
        if (name.Length == 0 || tag.Length != 4 || !tag.All(char.IsDigit))
        {
            Status = "User tags use a display name followed by four digits, for example Name#4821.";
            return;
        }
        var fullTag = $"{name}#{tag}";
        if (string.Equals(fullTag, UserTag, StringComparison.OrdinalIgnoreCase))
        {
            Status = "You cannot add your own account as a friend.";
            return;
        }
        try
        {
            await _chat.AddFriendAsync(new ChatMember { UserName = name, UserTag = fullTag });
            await LoadFriendsAsync();
            FriendTagInput = "";
            Status = $"{fullTag} was verified and added to your Supabase friends list.";
        }
        catch (Exception ex) { Status = $"Could not add friend: {ex.Message}"; }
    }

    private async Task LoadChannelAsync(string channel)
    {
        try
        {
            var messages = await _chat.GetMessagesAsync(channel);
            Messages.Clear();
            foreach (var message in messages)
            {
                Messages.Add(message);
                AddCommunityMember(message, false);
            }
            if (_isActive && SelectedFriend is null) await MarkAnnouncementsReadAsync();
            else await RefreshUnreadAnnouncementCountAsync();
        }
        catch (Exception ex) { Status = $"Could not load announcements: {ex.Message}"; }
    }

    private async Task LoadDirectMessagesAsync(ChatMember friend)
    {
        try
        {
            var messages = await _chat.GetDirectMessagesAsync(UserTag, friend.UserTag);
            if (!ReferenceEquals(SelectedFriend, friend)) return;
            Messages.Clear();
            foreach (var message in messages) Messages.Add(message);
        }
        catch (Exception ex) { Status = $"Could not load private chat: {ex.Message}"; }
    }

    private async Task MarkAnnouncementsReadAsync()
    {
        try
        {
            var latestMessageId = await _chat.GetLatestAnnouncementMessageIdAsync();
            await _settings.SetAsync(AnnouncementReadSetting, latestMessageId.ToString());
            UnreadAnnouncementCount = 0;
        }
        catch (Exception ex) { Status = $"Could not update announcement read status: {ex.Message}"; }
    }

    private async Task RefreshUnreadAnnouncementCountAsync()
    {
        var readThrough = long.TryParse(await _settings.GetAsync(AnnouncementReadSetting), out var savedReadThrough) ? savedReadThrough : 0;
        UnreadAnnouncementCount = await _chat.GetUnreadAnnouncementCountAsync(readThrough);
    }

    private async Task LoadFriendsAsync()
    {
        try
        {
            var friends = await _chat.GetFriendsAsync();
            Friends.Clear();
            foreach (var friend in friends) Friends.Add(friend);
        }
        catch (Exception ex) { Status = $"Could not load friends: {ex.Message}"; }
    }

    private void OnMemberPresenceReceived(object? sender, ChatMember member)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess()) AddCommunityPresence(member);
        else dispatcher.BeginInvoke(() => AddCommunityPresence(member));
    }

    private void AddCommunityPresence(ChatMember member)
    {
        if (member.UserTag == UserTag) return;
        var existing = CommunityMembers.FirstOrDefault(item => item.UserTag == member.UserTag);
        member.LastSeenUtc = member.IsOnline ? DateTime.UtcNow : null;
        if (existing is null) CommunityMembers.Add(member);
        else
        {
            existing.UserName = member.UserName;
            existing.ProfilePicturePath = member.ProfilePicturePath;
            existing.IsOnline = member.IsOnline;
            existing.LastSeenUtc = member.LastSeenUtc;
        }
        var friend = Friends.FirstOrDefault(item => item.UserTag == member.UserTag);
        if (friend is not null)
        {
            friend.UserName = member.UserName;
            friend.ProfilePicturePath = member.ProfilePicturePath;
            friend.IsOnline = member.IsOnline;
            friend.LastSeenUtc = member.LastSeenUtc;
        }
    }

    private void RefreshPresence()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-90);
        foreach (var member in CommunityMembers.Concat(Friends))
            if (member.IsOnline && (member.LastSeenUtc is null || member.LastSeenUtc < cutoff)) member.IsOnline = false;
    }

    private void OnTransportError(Exception exception)
    {
        var dispatcher = Application.Current.Dispatcher;
        dispatcher.BeginInvoke(() => Status = $"Supabase live-sync error: {exception.Message}");
    }

    private void OnMessageReceived(object? sender, ChatMessage message)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess()) AddMessage(message);
        else dispatcher.BeginInvoke(() => AddMessage(message));
    }

    private void AddMessage(ChatMessage message)
    {
        if (message.IsDirectMessage)
        {
            if (SelectedFriend is not null && message.ConversationId == GetConversationId(UserTag, SelectedFriend.UserTag)
                && !Messages.Any(existing => existing.MessageId == message.MessageId)) Messages.Add(message);
            return;
        }

        AddCommunityMember(message, true);
        if (message.Channel != AnnouncementsChannel) return;
        if (SelectedChannel == AnnouncementsChannel && SelectedFriend is null && !Messages.Any(existing => existing.MessageId == message.MessageId))
        {
            Messages.Add(message);
            if (_isActive) _ = MarkAnnouncementsReadAsync();
            else UnreadAnnouncementCount++;
        }
        else if (!(_isActive && SelectedFriend is null && SelectedChannel == AnnouncementsChannel))
        {
            UnreadAnnouncementCount++;
        }
    }

    private static string GetConversationId(string firstTag, string secondTag) => SupabaseChatBackend.GetConversationId(firstTag, secondTag);

    private void AddCommunityMember(ChatMessage message, bool online)
    {
        if (string.IsNullOrWhiteSpace(message.UserTag) || message.UserTag == UserTag) return;
        var existing = CommunityMembers.FirstOrDefault(member => member.UserTag == message.UserTag);
        if (existing is null)
            CommunityMembers.Add(new ChatMember { UserName = message.UserName, UserTag = message.UserTag, ProfilePicturePath = message.ProfilePicturePath, IsOnline = online, LastSeenUtc = online ? DateTime.UtcNow : null });
        else if (online) { existing.IsOnline = true; existing.LastSeenUtc = DateTime.UtcNow; }
        var friend = Friends.FirstOrDefault(item => item.UserTag == message.UserTag);
        if (friend is not null && online) { friend.IsOnline = true; friend.LastSeenUtc = DateTime.UtcNow; }
    }
}
