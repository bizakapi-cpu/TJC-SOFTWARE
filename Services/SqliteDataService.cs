using Microsoft.Data.Sqlite;
using TJCServiceDesk.Models;

namespace TJCServiceDesk.Services;

public sealed class SqliteDataService
{
    private readonly string _connectionString;

    public SqliteDataService()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TJCServiceDesk");
        Directory.CreateDirectory(folder);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "service-desk.db") }.ToString();
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    public async Task InitializeAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Tracks (Path TEXT PRIMARY KEY, Title TEXT, Artist TEXT, Album TEXT, CoverPath TEXT, Duration REAL);
            CREATE TABLE IF NOT EXISTS Playlists (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS PlaylistTracks (PlaylistId INTEGER NOT NULL, TrackPath TEXT NOT NULL, PRIMARY KEY (PlaylistId, TrackPath));
            CREATE TABLE IF NOT EXISTS Games (ExecutablePath TEXT PRIMARY KEY, Name TEXT, AppId TEXT, CoverPath TEXT, Source TEXT, CustomCoverPath TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Messages (Id INTEGER PRIMARY KEY AUTOINCREMENT, MessageId TEXT NOT NULL DEFAULT '', Channel TEXT, IsDirectMessage INTEGER NOT NULL DEFAULT 0, ConversationId TEXT NOT NULL DEFAULT '', RecipientTag TEXT NOT NULL DEFAULT '', UserName TEXT, UserTag TEXT NOT NULL DEFAULT '', ProfilePicturePath TEXT NOT NULL DEFAULT '', Text TEXT, SentAt TEXT);
            CREATE TABLE IF NOT EXISTS Friends (UserTag TEXT PRIMARY KEY, UserName TEXT NOT NULL, ProfilePicturePath TEXT NOT NULL DEFAULT '', AddedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS recent_songs (SongId TEXT PRIMARY KEY, Provider TEXT NOT NULL, Title TEXT NOT NULL, Artist TEXT NOT NULL, DurationSeconds REAL NOT NULL, ThumbnailUrl TEXT NOT NULL DEFAULT '', Url TEXT NOT NULL, LocalPath TEXT NOT NULL DEFAULT '', SearchQuery TEXT NOT NULL DEFAULT '', LastActivityUtc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS favorite_songs (SongId TEXT PRIMARY KEY, Provider TEXT NOT NULL, Title TEXT NOT NULL, Artist TEXT NOT NULL, DurationSeconds REAL NOT NULL, ThumbnailUrl TEXT NOT NULL DEFAULT '', Url TEXT NOT NULL, LocalPath TEXT NOT NULL DEFAULT '', AddedAtUtc TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync();
        await EnsureColumnAsync(connection, "Games", "CustomCoverPath", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "Messages", "MessageId", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "Messages", "IsDirectMessage", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "Messages", "ConversationId", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "Messages", "RecipientTag", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "Messages", "UserTag", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "Messages", "ProfilePicturePath", "TEXT NOT NULL DEFAULT ''");
        var migrateMessages = connection.CreateCommand();
        migrateMessages.CommandText = "UPDATE Messages SET MessageId='legacy-' || Id WHERE MessageId='' OR MessageId IS NULL; UPDATE Messages SET Channel='#mededelingen' WHERE Channel IN ('#algemeen','#gaming','#muziek','#support'); CREATE UNIQUE INDEX IF NOT EXISTS IX_Messages_MessageId ON Messages(MessageId); CREATE INDEX IF NOT EXISTS IX_Messages_Channel_Id ON Messages(Channel,Id); CREATE INDEX IF NOT EXISTS IX_Messages_Conversation_Id ON Messages(ConversationId,Id);";
        await migrateMessages.ExecuteNonQueryAsync();
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        await reader.DisposeAsync();
        var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        await alter.ExecuteNonQueryAsync();
    }

    public async Task<List<Track>> GetTracksAsync(long? playlistId = null)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = playlistId is null
            ? "SELECT Path,Title,Artist,Album,CoverPath,Duration FROM Tracks ORDER BY Title COLLATE NOCASE"
            : "SELECT t.Path,t.Title,t.Artist,t.Album,t.CoverPath,t.Duration FROM Tracks t JOIN PlaylistTracks p ON p.TrackPath=t.Path WHERE p.PlaylistId=$id ORDER BY t.Title COLLATE NOCASE";
        if (playlistId is not null) command.Parameters.AddWithValue("$id", playlistId.Value);
        var result = new List<Track>(); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new Track { Path = reader.GetString(0), Title = reader.GetString(1), Artist = reader.GetString(2), Album = reader.GetString(3), CoverPath = reader.GetString(4), DurationSeconds = reader.GetDouble(5) });
        return result;
    }

    public async Task SaveTracksAsync(IEnumerable<Track> tracks)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(); await using var transaction = await connection.BeginTransactionAsync();
        foreach (var track in tracks)
        {
            var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO Tracks(Path,Title,Artist,Album,CoverPath,Duration) VALUES($p,$t,$a,$al,$c,$d) ON CONFLICT(Path) DO UPDATE SET Title=excluded.Title,Artist=excluded.Artist,Album=excluded.Album,CoverPath=excluded.CoverPath,Duration=excluded.Duration";
            command.Parameters.AddWithValue("$p", track.Path); command.Parameters.AddWithValue("$t", track.Title); command.Parameters.AddWithValue("$a", track.Artist); command.Parameters.AddWithValue("$al", track.Album); command.Parameters.AddWithValue("$c", track.CoverPath); command.Parameters.AddWithValue("$d", track.DurationSeconds);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task<List<Playlist>> GetPlaylistsAsync()
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(); var command = connection.CreateCommand(); command.CommandText = "SELECT Id,Name FROM Playlists ORDER BY Name COLLATE NOCASE";
        var result = new List<Playlist>(); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result.Add(new Playlist { Id = reader.GetInt64(0), Name = reader.GetString(1) }); return result;
    }

    public async Task<long> CreatePlaylistAsync(string name)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(); var command = connection.CreateCommand(); command.CommandText = "INSERT INTO Playlists(Name) VALUES($name); SELECT last_insert_rowid();"; command.Parameters.AddWithValue("$name", name); return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    public async Task DeletePlaylistAsync(long id)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(); await using var transaction = await connection.BeginTransactionAsync();
        foreach (var sql in new[] { "DELETE FROM PlaylistTracks WHERE PlaylistId=$id", "DELETE FROM Playlists WHERE Id=$id" }) { var cmd = connection.CreateCommand(); cmd.Transaction = (SqliteTransaction)transaction; cmd.CommandText = sql; cmd.Parameters.AddWithValue("$id", id); await cmd.ExecuteNonQueryAsync(); }
        await transaction.CommitAsync();
    }

    public async Task AddTrackToPlaylistAsync(long id, string path)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(); var command = connection.CreateCommand(); command.CommandText = "INSERT OR IGNORE INTO PlaylistTracks(PlaylistId,TrackPath) VALUES($id,$path)"; command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$path", path); await command.ExecuteNonQueryAsync();
    }

    public async Task<List<Game>> GetGamesAsync()
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(); var command = connection.CreateCommand(); command.CommandText = "SELECT Name,ExecutablePath,AppId,COALESCE(NULLIF(CustomCoverPath,''),CoverPath),Source,CustomCoverPath FROM Games ORDER BY Name COLLATE NOCASE";
        var result = new List<Game>(); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result.Add(new Game { Name = reader.GetString(0), ExecutablePath = reader.GetString(1), AppId = reader.GetString(2), CoverPath = reader.GetString(3), Source = reader.GetString(4), CustomCoverPath = reader.GetString(5) }); return result;
    }

    public async Task SaveGamesAsync(IEnumerable<Game> games)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(); await using var transaction = await connection.BeginTransactionAsync();
        foreach (var game in games) { var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction; command.CommandText = "INSERT INTO Games(ExecutablePath,Name,AppId,CoverPath,Source,CustomCoverPath) VALUES($p,$n,$a,$c,$s,$custom) ON CONFLICT(ExecutablePath) DO UPDATE SET Name=excluded.Name,AppId=excluded.AppId,CoverPath=CASE WHEN Games.CustomCoverPath='' THEN excluded.CoverPath ELSE Games.CoverPath END,Source=excluded.Source,CustomCoverPath=CASE WHEN Games.CustomCoverPath='' THEN excluded.CustomCoverPath ELSE Games.CustomCoverPath END"; command.Parameters.AddWithValue("$p", game.ExecutablePath); command.Parameters.AddWithValue("$n", game.Name); command.Parameters.AddWithValue("$a", game.AppId); command.Parameters.AddWithValue("$c", game.CoverPath); command.Parameters.AddWithValue("$s", game.Source); command.Parameters.AddWithValue("$custom", game.CustomCoverPath); await command.ExecuteNonQueryAsync(); }
        await transaction.CommitAsync();
    }

    public async Task SetGameCustomCoverAsync(string executablePath, string coverPath)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE Games SET CustomCoverPath=$cover WHERE ExecutablePath=$path";
        command.Parameters.AddWithValue("$cover", coverPath); command.Parameters.AddWithValue("$path", executablePath);
        if (await command.ExecuteNonQueryAsync() == 0) throw new InvalidOperationException("The selected game is no longer in the library.");
    }

    public async Task<string?> GetSettingAsync(string key)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(); var command = connection.CreateCommand(); command.CommandText = "SELECT Value FROM Settings WHERE Key=$key"; command.Parameters.AddWithValue("$key", key); return (string?)await command.ExecuteScalarAsync();
    }

    public async Task SetSettingAsync(string key, string value)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(); var command = connection.CreateCommand(); command.CommandText = "INSERT INTO Settings(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value"; command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$value", value); await command.ExecuteNonQueryAsync();
    }

    public async Task<List<ChatMessage>> GetMessagesAsync(string channel, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(cancellationToken); var command = connection.CreateCommand(); command.CommandText = "SELECT MessageId,Channel,IsDirectMessage,ConversationId,RecipientTag,UserName,UserTag,ProfilePicturePath,Text,SentAt FROM Messages WHERE IsDirectMessage=0 AND Channel=$channel ORDER BY Id"; command.Parameters.AddWithValue("$channel", channel);
        var result = new List<ChatMessage>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) result.Add(ReadChatMessage(reader)); return result;
    }

    public async Task<List<ChatMessage>> GetDirectMessagesAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT MessageId,Channel,IsDirectMessage,ConversationId,RecipientTag,UserName,UserTag,ProfilePicturePath,Text,SentAt FROM Messages WHERE IsDirectMessage=1 AND ConversationId=$conversation ORDER BY Id"; command.Parameters.AddWithValue("$conversation", conversationId);
        var result = new List<ChatMessage>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) result.Add(ReadChatMessage(reader)); return result;
    }

    public async Task<int> GetUnreadAnnouncementCountAsync(long afterMessageId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM Messages WHERE IsDirectMessage=0 AND Channel='#mededelingen' AND Id>$id"; command.Parameters.AddWithValue("$id", afterMessageId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<long> GetLatestAnnouncementMessageIdAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT COALESCE(MAX(Id),0) FROM Messages WHERE IsDirectMessage=0 AND Channel='#mededelingen'";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static ChatMessage ReadChatMessage(SqliteDataReader reader) => new()
    {
        MessageId = reader.GetString(0), Channel = reader.GetString(1), IsDirectMessage = reader.GetBoolean(2),
        ConversationId = reader.GetString(3), RecipientTag = reader.GetString(4), UserName = reader.GetString(5),
        UserTag = reader.GetString(6), ProfilePicturePath = reader.GetString(7), Text = reader.GetString(8),
        SentAt = DateTime.Parse(reader.GetString(9))
    };

    public Task SaveMessageAsync(ChatMessage message, CancellationToken cancellationToken = default) => SaveMessageCoreAsync(message, false, cancellationToken);

    public async Task<bool> SaveMessageIfNewAsync(ChatMessage message, CancellationToken cancellationToken = default) => await SaveMessageCoreAsync(message, true, cancellationToken);

    private async Task<bool> SaveMessageCoreAsync(ChatMessage message, bool ignoreDuplicate, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = $"INSERT {(ignoreDuplicate ? "OR IGNORE " : "")}INTO Messages(MessageId,Channel,IsDirectMessage,ConversationId,RecipientTag,UserName,UserTag,ProfilePicturePath,Text,SentAt) VALUES($id,$c,$dm,$conversation,$recipient,$u,$tag,$picture,$t,$d)";
        command.Parameters.AddWithValue("$id", message.MessageId); command.Parameters.AddWithValue("$c", message.Channel); command.Parameters.AddWithValue("$dm", message.IsDirectMessage); command.Parameters.AddWithValue("$conversation", message.ConversationId); command.Parameters.AddWithValue("$recipient", message.RecipientTag); command.Parameters.AddWithValue("$u", message.UserName); command.Parameters.AddWithValue("$tag", message.UserTag); command.Parameters.AddWithValue("$picture", message.ProfilePicturePath); command.Parameters.AddWithValue("$t", message.Text); command.Parameters.AddWithValue("$d", message.SentAt.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<List<ChatMember>> GetFriendsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT UserName,UserTag,ProfilePicturePath FROM Friends ORDER BY UserName COLLATE NOCASE";
        var result = new List<ChatMember>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new ChatMember { UserName = reader.GetString(0), UserTag = reader.GetString(1), ProfilePicturePath = reader.GetString(2), IsOnline = false });
        return result;
    }

    public async Task AddFriendAsync(ChatMember friend, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "INSERT INTO Friends(UserTag,UserName,ProfilePicturePath,AddedAt) VALUES($tag,$name,$picture,$added) ON CONFLICT(UserTag) DO UPDATE SET UserName=excluded.UserName,ProfilePicturePath=excluded.ProfilePicturePath";
        command.Parameters.AddWithValue("$tag", friend.UserTag); command.Parameters.AddWithValue("$name", friend.UserName); command.Parameters.AddWithValue("$picture", friend.ProfilePicturePath); command.Parameters.AddWithValue("$added", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveRecentSongsAsync(IEnumerable<MusicSearchResult> songs, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var song in songs)
        {
            var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO recent_songs(SongId,Provider,Title,Artist,DurationSeconds,ThumbnailUrl,Url,LocalPath,SearchQuery,LastActivityUtc) VALUES($id,$provider,$title,$artist,$duration,$thumbnail,$url,$path,$query,$activity) ON CONFLICT(SongId) DO UPDATE SET Provider=excluded.Provider,Title=excluded.Title,Artist=excluded.Artist,DurationSeconds=excluded.DurationSeconds,ThumbnailUrl=excluded.ThumbnailUrl,Url=excluded.Url,LocalPath=excluded.LocalPath,SearchQuery=excluded.SearchQuery,LastActivityUtc=excluded.LastActivityUtc";
            AddMusicParameters(command, song);
            command.Parameters.AddWithValue("$query", song.SearchQuery ?? "");
            command.Parameters.AddWithValue("$activity", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var trimHistory = connection.CreateCommand(); trimHistory.Transaction = (SqliteTransaction)transaction;
        trimHistory.CommandText = "DELETE FROM recent_songs WHERE SongId NOT IN (SELECT SongId FROM recent_songs ORDER BY LastActivityUtc DESC LIMIT 100)";
        await trimHistory.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<List<MusicSearchResult>> GetRecentSongsAsync(int limit = 30, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT SongId,Provider,Title,Artist,DurationSeconds,ThumbnailUrl,Url,LocalPath,SearchQuery FROM recent_songs ORDER BY LastActivityUtc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        return await ReadMusicResultsAsync(command, cancellationToken);
    }

    public async Task SetFavoriteSongAsync(MusicSearchResult song, bool isFavorite, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = isFavorite
            ? "INSERT INTO favorite_songs(SongId,Provider,Title,Artist,DurationSeconds,ThumbnailUrl,Url,LocalPath,AddedAtUtc) VALUES($id,$provider,$title,$artist,$duration,$thumbnail,$url,$path,$added) ON CONFLICT(SongId) DO UPDATE SET Provider=excluded.Provider,Title=excluded.Title,Artist=excluded.Artist,DurationSeconds=excluded.DurationSeconds,ThumbnailUrl=excluded.ThumbnailUrl,Url=excluded.Url,LocalPath=excluded.LocalPath"
            : "DELETE FROM favorite_songs WHERE SongId=$id";
        if (isFavorite)
        {
            AddMusicParameters(command, song);
            command.Parameters.AddWithValue("$added", DateTime.UtcNow.ToString("O"));
        }
        else command.Parameters.AddWithValue("$id", song.SongId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<MusicSearchResult>> GetFavoriteSongsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT SongId,Provider,Title,Artist,DurationSeconds,ThumbnailUrl,Url,LocalPath,'' FROM favorite_songs ORDER BY AddedAtUtc DESC";
        return await ReadMusicResultsAsync(command, cancellationToken);
    }

    private static void AddMusicParameters(SqliteCommand command, MusicSearchResult song)
    {
        command.Parameters.AddWithValue("$id", song.SongId); command.Parameters.AddWithValue("$provider", song.Provider);
        command.Parameters.AddWithValue("$title", song.Title); command.Parameters.AddWithValue("$artist", song.Artist);
        command.Parameters.AddWithValue("$duration", song.DurationSeconds); command.Parameters.AddWithValue("$thumbnail", song.ThumbnailUrl);
        command.Parameters.AddWithValue("$url", song.Url); command.Parameters.AddWithValue("$path", song.LocalPath);
    }

    private static async Task<List<MusicSearchResult>> ReadMusicResultsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var results = new List<MusicSearchResult>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new MusicSearchResult { SongId = reader.GetString(0), Provider = reader.GetString(1), Title = reader.GetString(2), Artist = reader.GetString(3), DurationSeconds = reader.GetDouble(4), ThumbnailUrl = reader.GetString(5), Url = reader.GetString(6), LocalPath = reader.GetString(7), SearchQuery = reader.GetString(8) });
        return results;
    }
}
