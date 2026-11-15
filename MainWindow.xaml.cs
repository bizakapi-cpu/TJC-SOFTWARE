using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using TJCServiceDesk.Services;
using TJCServiceDesk.ViewModels;

namespace TJCServiceDesk;

public partial class MainWindow : Window
{
    private readonly SupabaseService _supabase;
    private bool _isClosing;
    private bool _isFinalClose;

    public MainWindow(SupabaseService supabase)
    {
        _supabase = supabase;
        InitializeComponent();
        Closing += HandleClosing;
    }

    private async void HandleClosing(object? sender, CancelEventArgs e)
    {
        if (_isFinalClose) return;
        e.Cancel = true;
        if (_isClosing) return;
        _isClosing = true;
        try
        {
            if (DataContext is MainViewModel viewModel)
            {
                try { await viewModel.StopAsync(); }
                catch (Exception ex) { Debug.WriteLine($"Application background services did not stop cleanly: {ex}"); }

                if (_supabase.IsConfigured)
                {
                    try { await _supabase.SetOfflineAsync(viewModel.Settings.UserDisplayTag); }
                    catch (Exception ex) { Debug.WriteLine($"Could not update Supabase presence before closing: {ex}"); }
                }
            }
        }
        finally
        {
            _isFinalClose = true;
            Close();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}