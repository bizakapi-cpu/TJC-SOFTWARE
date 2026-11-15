using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TJCServiceDesk.Models;
using TJCServiceDesk.Services;

namespace TJCServiceDesk.ViewModels;

public partial class GamesViewModel : ObservableObject
{
    private readonly GameService _games;
    public ObservableCollection<Game> Games { get; } = new();
    [ObservableProperty] private Game? selectedGame;
    [ObservableProperty] private string status = "Your game library";

    public GamesViewModel(GameService games) => _games = games;

    public async Task InitializeAsync() => await ReloadAsync();

    [RelayCommand]
    private async Task ScanAsync()
    {
        try { Status = "Scanning Steam and Epic Games…"; var count = await _games.ScanAsync(); await ReloadAsync(); Status = $"Found {count} installed games."; }
        catch (Exception ex) { Status = $"Game scan failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task AddGameAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Windows executable (*.exe)|*.exe", Title = "Add a game" };
        if (dialog.ShowDialog() != true) return;
        try { await _games.AddLocalAsync(dialog.FileName); await ReloadAsync(); Status = "Game added to your library."; }
        catch (Exception ex) { Status = $"Could not add game: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SetCustomCoverAsync(Game? game)
    {
        if (game is null) return;
        var dialog = new OpenFileDialog
        {
            Filter = "Images and icons (*.png;*.jpg;*.jpeg;*.ico;*.exe)|*.png;*.jpg;*.jpeg;*.ico;*.exe|All files (*.*)|*.*",
            Title = "Set custom game cover or icon",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _games.SetCustomCoverAsync(game, dialog.FileName);
            await ReloadAsync();
            Status = $"Custom cover set for {game.Name}.";
        }
        catch (Exception ex) { Status = $"Could not set game cover: {ex.Message}"; }
    }

    [RelayCommand]
    private void Play(Game? game)
    {
        if (game is null) return;
        try { _games.Launch(game); Status = $"Launching {game.Name}…"; }
        catch (Exception ex) { Status = $"Could not launch game: {ex.Message}"; }
    }

    private async Task ReloadAsync()
    {
        var games = await _games.GetGamesAsync(); Games.Clear(); foreach (var game in games) Games.Add(game);
    }
}
