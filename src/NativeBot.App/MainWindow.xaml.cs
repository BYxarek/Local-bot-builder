using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Input;
using NativeBot.Core;
using MessageBox = System.Windows.MessageBox;

namespace NativeBot.App;

public partial class MainWindow : Window
{
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private bool _allowClose;
    private string _closeBehavior = "ask";

    public MainWindow()
    {
        InitializeComponent();
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Конструктор Telegram-ботов",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
        Loaded += async (_, _) => { _closeBehavior = await App.Database.GetSettingAsync("CloseBehavior") ?? "ask"; await EnsureAgentAsync(); await RefreshAsync(); };
        Activated += async (_, _) => _closeBehavior = await App.Database.GetSettingAsync("CloseBehavior") ?? "ask";
        Closing += Window_Closing;
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

        var answer = MessageBox.Show(
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

    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void CloseCompletely() { _allowClose = true; _trayIcon.Dispose(); Close(); }

    protected override void OnClosed(EventArgs e)
    {
        _trayIcon.Dispose();
        base.OnClosed(e);
    }
}
