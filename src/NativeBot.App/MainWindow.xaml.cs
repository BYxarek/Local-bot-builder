using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NativeBot.Core;
using MessageBox = System.Windows.MessageBox;

namespace NativeBot.App;

public partial class MainWindow : Window
{
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _allowClose;
    private bool _settingsInitialized;
    private string _closeBehavior = "background";

    public MainWindow()
    {
        InitializeComponent();
        Activated += async (_, _) => _closeBehavior = await App.Database.GetSettingAsync("CloseBehavior") ?? "background";
        Closing += Window_Closing;
    }

    internal async Task InitializeAsync()
    {
        InitializeTrayIcon();
        _closeBehavior = await App.Database.GetSettingAsync("CloseBehavior") ?? "background";
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        key.DeleteValue("NativeBot.Agent", false);
        AutoStartBox.IsChecked = key.GetValue("NativeBot.App") is not null;
        ThemeBox.SelectedIndex = App.CurrentTheme == "dark" ? 1 : 0;
        VersionTextBlock.Text = App.DisplayVersion;
        _settingsInitialized = true;
        await EnsureAgentAsync();
        await RefreshAsync();
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Конструктор Telegram-ботов",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private System.Windows.Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Приостановить всех", null, async (_, _) => await SendAgentCommandAsync("pause"));
        menu.Items.Add("Продолжить", null, async (_, _) => await SendAgentCommandAsync("resume"));
        menu.Items.Add("Выход", null, async (_, _) => { await SendAgentCommandAsync("exit"); Dispatcher.Invoke(CloseCompletely); });
        return menu;
    }

    private async Task RefreshAsync() => BotsGrid.ItemsSource = await App.Database.GetBotsAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void AddBot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddBotWindow { Owner = this };
        if (dialog.ShowDialog() == true) await RefreshAsync();
    }

    private void BotsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BotsGrid.SelectedItem is Bot bot) new BotWindow(bot) { Owner = this }.ShowDialog();
    }

    private async void PauseAll_Click(object sender, RoutedEventArgs e) => await SendAgentCommandAsync("pause");

    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (AutoStartBox.IsChecked == true)
            key.SetValue("NativeBot.App", $"\"{Path.Combine(AppContext.BaseDirectory, "NativeBot.App.exe")}\" --autostart --high-priority");
        else
            key.DeleteValue("NativeBot.App", false);
    }

    private async void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsInitialized || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string theme }) return;
        App.ApplyTheme(theme);
        await App.Database.SetSettingAsync("AppTheme", theme);
    }

    private async Task EnsureAgentAsync()
    {
        if (await SendAgentCommandAsync("resume")) return;
        var executable = Path.Combine(AppContext.BaseDirectory, "NativeBot.Agent.exe");
        var dll = Path.Combine(AppContext.BaseDirectory, "NativeBot.Agent.dll");
        if (File.Exists(executable))
            Process.Start(new ProcessStartInfo(executable, $"--db \"{App.DatabasePath}\"") { UseShellExecute = false, CreateNoWindow = true });
        else if (File.Exists(dll))
            Process.Start(new ProcessStartInfo("dotnet", $"\"{dll}\" --db \"{App.DatabasePath}\"") { UseShellExecute = false, CreateNoWindow = true });
    }

    internal static async Task<bool> SendAgentCommandAsync(string command)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", "NativeBot.Agent", PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(350);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await writer.WriteLineAsync(command);
            return await reader.ReadLineAsync() == "ok";
        }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (_closeBehavior == "background") { e.Cancel = true; Hide(); return; }
        if (_closeBehavior == "exit") { _ = SendAgentCommandAsync("exit"); _allowClose = true; return; }

        var answer = MessageBox.Show(this,
            "Да — продолжить работу ботов в фоне.\nНет — завершить приложение и ботов.\nОтмена — вернуться в приложение.",
            "Закрытие приложения", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (answer == MessageBoxResult.Yes)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _ = SendAgentCommandAsync("exit");
        _allowClose = true;
    }

    internal void ShowFromTray() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized; Activate(); }
    private void CloseCompletely() { _allowClose = true; _trayIcon?.Dispose(); Close(); }

    protected override void OnClosed(EventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnClosed(e);
    }
}
