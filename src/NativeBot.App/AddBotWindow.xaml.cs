using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using NativeBot.Core;
using NativeBot.Telegram;

namespace NativeBot.App;

public partial class AddBotWindow : Window
{
    public AddBotWindow() => InitializeComponent();

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        StatusPanel.Visibility = Visibility.Visible;
        ConnectButton.IsEnabled = false;
        StatusText.Text = "Проверяю подключение…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var client = new TelegramClient(http, TokenBox.Password);
            var info = await client.GetMeAsync();
            var mode = UpdateModeBox.SelectedItem is ComboBoxItem { Tag: "LongPolling" }
                ? BotUpdateMode.LongPolling
                : BotUpdateMode.Webhook;
            var bot = new Bot(Guid.NewGuid(), info.FirstName, "@" + info.Username, BotStatus.Stopped, AutoStartBox.IsChecked == true, UpdateMode: mode);
            await App.Database.AddBotAsync(bot, TokenBox.Password);
            TokenBox.Clear();
            DialogResult = true;
        }
        catch (Exception exception) when (exception is TelegramException or ArgumentException or HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = exception is TaskCanceledException ? "Telegram не ответил вовремя." : exception.Message;
        }
        finally { ConnectButton.IsEnabled = true; }
    }
}
