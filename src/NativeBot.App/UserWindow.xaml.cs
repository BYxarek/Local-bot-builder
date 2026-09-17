using System.Windows;
using NativeBot.Core;
using MessageBox = System.Windows.MessageBox;

namespace NativeBot.App;

public partial class UserWindow : Window
{
    private readonly BotUser _user;

    public UserWindow(BotUser user)
    {
        InitializeComponent();
        _user = user;
        Loaded += LoadAsync;
    }

    private async void LoadAsync(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var card = await App.Database.GetUserCardAsync(_user.BotId, _user.TelegramId);
        TitleText.Text = string.Join(' ', new[] { card.User.FirstName, card.User.LastName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        SummaryText.Text = $"Telegram ID: {card.User.TelegramId}\nUsername: {card.User.Username ?? "—"}\nЯзык: {card.User.Language ?? "—"}\n" +
                           $"Первое взаимодействие: {card.User.FirstSeenAt:g}\nПоследнее взаимодействие: {card.User.LastSeenAt:g}\n" +
                           $"Статус: {card.User.Status}\nТекущий сценарий: {card.CurrentFlow ?? "—"}";
        HistoryGrid.ItemsSource = card.History;
        TagsList.ItemsSource = card.Tags;
        FieldsGrid.ItemsSource = card.Fields.Select(x => new { Поле = x.Key, Значение = x.Value });
        WaitsList.ItemsSource = card.WaitingStates;
    }

    private async void AddTag_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TagBox.Text)) return;
        await App.Database.AddTagAsync(_user.BotId, _user.TelegramId, TagBox.Text);
        TagBox.Clear();
        await RefreshAsync();
    }

    private async void RemoveTag_Click(object sender, RoutedEventArgs e)
    {
        if (TagsList.SelectedItem is not string tag) return;
        await App.Database.RemoveTagAsync(_user.BotId, _user.TelegramId, tag);
        await RefreshAsync();
    }

    private async void SaveField_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.Database.SetUserFieldAsync(_user.BotId, _user.TelegramId, FieldNameBox.Text.Trim(), FieldKeyBox.Text.Trim(), VariableType.Text, FieldValueBox.Text);
            await RefreshAsync();
        }
        catch (ArgumentException exception) { MessageBox.Show(this, exception.Message); }
    }
}
