using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NativeBot.Core;
using NativeBot.Telegram;
using MessageBox = System.Windows.MessageBox;

namespace NativeBot.App;

public partial class BotWindow : Window
{
    private readonly Bot _bot;
    private readonly ObservableCollection<FlowNode> _nodes = [];
    private readonly List<VariableDefinition> _variables =
    [
        new("user.first_name", VariableScope.User, VariableType.Text),
        new("user.username", VariableScope.User, VariableType.Text),
        new("event.message.text", VariableScope.Event, VariableType.Text, true),
        new("event.message.content_type", VariableScope.Event, VariableType.Text, true),
        new("event.chat.id", VariableScope.Event, VariableType.Number, true),
        new("event.update.type", VariableScope.Event, VariableType.Text, true),
        new("event.start.payload", VariableScope.Event, VariableType.Text, true),
        new("event.business.connection_id", VariableScope.Event, VariableType.Text, true),
        new("event.guest.query_id", VariableScope.Event, VariableType.Number, true),
        new("event.mini_app.data", VariableScope.Event, VariableType.Text, true),
        new("event.payment.status", VariableScope.Event, VariableType.Text, true),
        new("system.now", VariableScope.System, VariableType.DateTime, true)
    ];
    private Guid _flowId = Guid.NewGuid();
    private IReadOnlyList<BotUser> _visibleUsers = [];
    private IReadOnlyList<Segment> _segments = [];
    private string? _testedBroadcastText;

    public BotWindow(Bot bot)
    {
        InitializeComponent();
        _bot = bot;
        BotTitle.Text = $"{bot.Name}  {bot.Username}";
        BotAutoStartBox.IsChecked = bot.AutoStart;
        PaletteList.ItemsSource = PaletteItem.All;
        NodesList.ItemsSource = _nodes;
        Loaded += LoadAsync;
    }

    private async void LoadAsync(object sender, RoutedEventArgs e)
    {
        UserTagBox.ItemsSource = await App.Database.GetTagsAsync(_bot.Id);
        UserFieldBox.ItemsSource = await App.Database.GetCustomFieldsAsync(_bot.Id);
        _segments = await App.Database.GetSegmentsAsync(_bot.Id);
        UserSegmentBox.ItemsSource = _segments;
        await RefreshUsersAsync();
        BroadcastsGrid.ItemsSource = await App.Database.GetBroadcastsAsync(_bot.Id);
        LogsGrid.ItemsSource = await App.Database.GetLogsAsync(_bot.Id, false);
        var draft = (await App.Database.GetDraftsAsync(_bot.Id)).FirstOrDefault();
        if (draft is not null)
        {
            _flowId = draft.Id;
            FlowNameBox.Text = draft.Name;
            foreach (var node in draft.Nodes) _nodes.Add(node);
        }
        var closeBehavior = await App.Database.GetSettingAsync("CloseBehavior") ?? "ask";
        WebhookPublicUrlBox.Text = await App.Database.GetSettingAsync("WebhookPublicUrl") ?? "";
        WebhookListenUrlBox.Text = await App.Database.GetSettingAsync("WebhookListenUrl") ?? "http://localhost:8443";
        PrivacyModeDisabledBox.IsChecked = await App.Database.GetSettingAsync($"PrivacyModeDisabled:{_bot.Id}") == "true";
        foreach (ComboBoxItem item in UpdateModeBox.Items) item.IsSelected = Equals(item.Tag, _bot.UpdateMode.ToString());
        foreach (ComboBoxItem item in CloseBehaviorBox.Items) item.IsSelected = Equals(item.Tag, closeBehavior);
        AgentAutoStartBox.IsChecked = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue("NativeBot.Agent") is not null;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        RuntimeStatus.Text = await MainWindow.SendAgentCommandAsync($"start:{_bot.Id}") ? "Бот запускается" : "Bot Agent недоступен";
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        RuntimeStatus.Text = await MainWindow.SendAgentCommandAsync($"stop:{_bot.Id}") ? "Бот остановлен" : "Bot Agent недоступен";
    }

    private void AddNode_Click(object sender, RoutedEventArgs e) => AddSelectedNode();
    private void AddNode_Click(object sender, MouseButtonEventArgs e) => AddSelectedNode();

    private void AddSelectedNode()
    {
        if (PaletteList.SelectedItem is not PaletteItem item) return;
        var node = new FlowNode(Guid.NewGuid(), item.Label, item.Kind, item.Handler, null, new JsonObject());
        if (_nodes.LastOrDefault() is { Kind: not NodeKind.End } previous)
        {
            var index = _nodes.IndexOf(previous);
            _nodes[index] = previous with { Next = new Dictionary<NodeOutcome, Guid> { [NodeOutcome.Success] = node.Id } };
        }
        _nodes.Add(node);
        NodesList.SelectedItem = node;
    }

    private void NodesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NodesList.SelectedItem is FlowNode node) ContentBox.Text = node.Parameters?["text"]?.GetValue<string>() ?? "";
    }

    private void ApplyNode_Click(object sender, RoutedEventArgs e)
    {
        if (NodesList.SelectedItem is not FlowNode node) return;
        var index = _nodes.IndexOf(node);
        var parameters = node.Parameters?.DeepClone().AsObject() ?? new JsonObject();
        parameters["text"] = ContentBox.Text;
        _nodes[index] = node with { Parameters = parameters };
        NodesList.SelectedIndex = index;
    }

    private async void SaveDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_nodes.Count == 0) { MessageBox.Show("Добавьте хотя бы один блок."); return; }
        await App.Database.SaveDraftAsync(_bot.Id, BuildFlow());
        RuntimeStatus.Text = "Черновик сохранён";
    }

    private void Validate_Click(object sender, RoutedEventArgs e) => ValidateFlow(showSuccess: true);

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        var issues = ValidateFlow(showSuccess: false);
        if (issues.Any(x => x.IsError)) { MessageBox.Show("Исправьте ошибки перед публикацией."); return; }
        var version = await App.Database.PublishAsync(_bot.Id, BuildFlow());
        RuntimeStatus.Text = $"Опубликована версия {version.Version}";
    }

    private IReadOnlyList<ValidationIssue> ValidateFlow(bool showSuccess)
    {
        if (_nodes.Count == 0)
        {
            var empty = new[] { new ValidationIssue(null, "flow.empty", "Сценарий пуст.") };
            ValidationList.ItemsSource = empty;
            return empty;
        }
        foreach (var node in _nodes.Where(x => x.Handler == "groupText").ToArray())
        {
            var index = _nodes.IndexOf(node);
            var parameters = node.Parameters?.DeepClone().AsObject() ?? new();
            parameters["requiresGroupMessages"] = true;
            parameters["privacyModeDisabled"] = PrivacyModeDisabledBox.IsChecked == true;
            _nodes[index] = node with { Parameters = parameters };
        }
        var flow = BuildFlow();
        var issues = new FlowValidator().Validate(flow).Concat(new TelegramFlowValidator().Validate(flow)).ToArray();
        ValidationList.ItemsSource = issues;
        if (showSuccess && issues.Length == 0) MessageBox.Show("Ошибок не найдено.");
        return issues;
    }

    private FlowDefinition BuildFlow() => new(_flowId, FlowNameBox.Text.Trim(), _nodes[0].Id, _nodes.ToArray(), _variables);

    private void ContentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        VariableHints.Visibility = ContentBox.Text.EndsWith('{') ? Visibility.Visible : Visibility.Collapsed;
        VariableHints.ItemsSource = _variables.Select(x => x.Name).Distinct().ToArray();
    }

    private void VariableHints_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VariableHints.SelectedItem is string name)
        {
            ContentBox.Text += name + "}";
            ContentBox.CaretIndex = ContentBox.Text.Length;
            VariableHints.Visibility = Visibility.Collapsed;
        }
    }

    private async void BotAutoStart_Click(object sender, RoutedEventArgs e) =>
        await App.Database.SetBotAutoStartAsync(_bot.Id, BotAutoStartBox.IsChecked == true);

    private async void PrivacyModeDisabled_Click(object sender, RoutedEventArgs e) =>
        await App.Database.SetSettingAsync($"PrivacyModeDisabled:{_bot.Id}", PrivacyModeDisabledBox.IsChecked == true ? "true" : "false");

    private void UsersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (UsersGrid.SelectedItem is BotUser user) new UserWindow(user) { Owner = this }.ShowDialog();
    }

    private async Task RefreshUsersAsync()
    {
        var filter = UserSegmentBox.SelectedItem is Segment segment ? segment.Filter : new UserFilter();
        var status = (UserStatusBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var fields = UserFieldBox.SelectedItem is CustomField field && !string.IsNullOrWhiteSpace(UserFieldValueBox.Text)
            ? new Dictionary<string, string?> { [field.Name] = UserFieldValueBox.Text.Trim() }
            : null;
        filter = filter with
        {
            Search = UserSearchBox.Text.Trim(),
            Language = string.IsNullOrWhiteSpace(UserLanguageBox.Text) ? filter.Language : UserLanguageBox.Text.Trim(),
            Status = status == "Любое состояние" ? filter.Status : status,
            ActiveAfter = UserActiveAfterBox.SelectedDate is { } active ? new DateTimeOffset(active) : filter.ActiveAfter,
            TagIds = UserTagBox.SelectedItem is BotTag tag ? [tag.Id] : filter.TagIds,
            FieldEquals = fields ?? filter.FieldEquals
        };
        _visibleUsers = await App.Database.FindUsersAsync(_bot.Id, filter);
        UsersGrid.ItemsSource = _visibleUsers;
    }

    private async void RefreshUsers_Click(object sender, RoutedEventArgs e) => await RefreshUsersAsync();

    private async void ResetUsers_Click(object sender, RoutedEventArgs e)
    {
        UserSearchBox.Clear();
        UserLanguageBox.Clear();
        UserStatusBox.SelectedIndex = 0;
        UserActiveAfterBox.SelectedDate = null;
        UserTagBox.SelectedItem = null;
        UserFieldBox.SelectedItem = null;
        UserFieldValueBox.Clear();
        UserSegmentBox.SelectedItem = null;
        await RefreshUsersAsync();
    }

    private async void TestBroadcast_Click(object sender, RoutedEventArgs e)
    {
        if (UsersGrid.SelectedItem is not BotUser user) { MessageBox.Show("Выберите получателя на вкладке «Пользователи»."); return; }
        var text = BroadcastTextBox.Text.Trim();
        if (text.Length is 0 or > 4096) { MessageBox.Show("Введите от 1 до 4096 символов."); return; }
        await App.Database.EnqueueOutboxAsync(new(Guid.NewGuid(), _bot.Id, user.TelegramId, "text", text, OutboxStatus.Pending, DateTimeOffset.UtcNow));
        _testedBroadcastText = text;
        RuntimeStatus.Text = "Тестовое сообщение поставлено в очередь";
    }

    private async void StartBroadcast_Click(object sender, RoutedEventArgs e)
    {
        var name = BroadcastNameBox.Text.Trim();
        var text = BroadcastTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || text.Length is 0 or > 4096) { MessageBox.Show("Укажите название и текст до 4096 символов."); return; }
        if (_testedBroadcastText != text) { MessageBox.Show("Сначала отправьте тест этого сообщения выбранному пользователю."); return; }
        if (_visibleUsers.Count == 0) { MessageBox.Show("Получатели не найдены."); return; }
        var id = Guid.NewGuid();
        await App.Database.AddBroadcastAsync(new(id, _bot.Id, name, null, text, DateTimeOffset.UtcNow, BroadcastStatus.Running, _visibleUsers.Count));
        foreach (var user in _visibleUsers)
            await App.Database.EnqueueOutboxAsync(new(Guid.NewGuid(), _bot.Id, user.TelegramId, "text", text, OutboxStatus.Pending, DateTimeOffset.UtcNow,
                DeduplicationKey: $"broadcast:{id}:{user.TelegramId}"));
        BroadcastsGrid.ItemsSource = await App.Database.GetBroadcastsAsync(_bot.Id);
        RuntimeStatus.Text = $"Рассылка поставлена в очередь: {_visibleUsers.Count}";
    }

    private void BroadcastTextBox_TextChanged(object sender, TextChangedEventArgs e) => _testedBroadcastText = null;

    private async void RefreshLogs_Click(object sender, RoutedEventArgs e) =>
        LogsGrid.ItemsSource = await App.Database.GetLogsAsync(_bot.Id, TechnicalLogBox.IsChecked == true);

    private void AgentAutoStart_Click(object sender, RoutedEventArgs e)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (AgentAutoStartBox.IsChecked == true)
            key.SetValue("NativeBot.Agent", $"\"{Path.Combine(AppContext.BaseDirectory, "NativeBot.Agent.exe")}\" --db \"{App.DatabasePath}\"");
        else
            key.DeleteValue("NativeBot.Agent", false);
    }

    private async void SaveWebhookSettings_Click(object sender, RoutedEventArgs e)
    {
        var mode = UpdateModeBox.SelectedItem is ComboBoxItem { Tag: "LongPolling" }
            ? BotUpdateMode.LongPolling
            : BotUpdateMode.Webhook;
        var publicUrl = WebhookPublicUrlBox.Text.Trim().TrimEnd('/');
        var listenUrl = WebhookListenUrlBox.Text.Trim().TrimEnd('/');
        if (mode == BotUpdateMode.Webhook && (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var publicUri) || publicUri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show("Укажите корректный публичный HTTPS-адрес.");
            return;
        }
        if (mode == BotUpdateMode.Webhook && (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var listenUri) || listenUri.Scheme is not ("http" or "https") || !listenUri.IsLoopback))
        {
            MessageBox.Show("Локальный адрес должен иметь вид http://localhost:8443 или https://localhost:8443.");
            return;
        }
        await App.Database.SetBotUpdateModeAsync(_bot.Id, mode);
        if (mode == BotUpdateMode.Webhook)
        {
            await App.Database.SetSettingAsync("WebhookPublicUrl", publicUrl);
            await App.Database.SetSettingAsync("WebhookListenUrl", listenUrl);
        }
        await MainWindow.SendAgentCommandAsync("reload");
        RuntimeStatus.Text = mode == BotUpdateMode.Webhook
            ? "Webhook-настройки сохранены. Для смены локального порта перезапустите Bot Agent."
            : "Long polling сохранён и применяется.";
    }

    private async void ExportProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Проект NativeBot (*.nativebot)|*.nativebot", FileName = $"{_bot.Username.TrimStart('@')}.nativebot" };
        if (dialog.ShowDialog(this) != true) return;
        await App.Database.ExportProjectAsync(_bot.Id, dialog.FileName);
        RuntimeStatus.Text = "Проект экспортирован без токена и секретов.";
    }

    private async void ImportProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Проект NativeBot (*.nativebot)|*.nativebot" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await App.Database.ImportProjectAsync(_bot.Id, dialog.FileName);
            RuntimeStatus.Text = "Проект импортирован. Откройте окно бота заново, чтобы увидеть сценарии.";
            await MainWindow.SendAgentCommandAsync("reload");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            MessageBox.Show(exception.Message);
        }
    }

    private async void CloseBehavior_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && CloseBehaviorBox.SelectedItem is ComboBoxItem { Tag: string value })
            await App.Database.SetSettingAsync("CloseBehavior", value);
    }
}

internal sealed record PaletteItem(string Category, string Label, NodeKind Kind, string Handler)
{
    public static IReadOnlyList<PaletteItem> All { get; } =
    [
        new("События", "Команда /start", NodeKind.Event, "start"),
        new("События", "Текстовое сообщение", NodeKind.Event, "text"),
        new("События", "Сообщение в группе", NodeKind.Event, "groupText"),
        new("Сообщения", "Отправить текст", NodeKind.Action, "sendText"),
        new("Сообщения", "Отправить вложение", NodeKind.Action, "sendAsset"),
        new("Логика", "Если", NodeKind.Condition, "if"),
        new("Логика", "Иначе", NodeKind.Branch, "else"),
        new("Данные", "Записать переменную", NodeKind.Action, "setVariable"),
        new("Данные", "Добавить тег", NodeKind.Action, "addTag"),
        new("Диалог", "Ждать следующий ответ", NodeKind.Wait, "wait"),
        new("Управление", "Задержка", NodeKind.Wait, "delay"),
        new("Управление", "Переход", NodeKind.Transition, "transition"),
        new("Интеграции", "HTTP-запрос", NodeKind.Action, "http"),
        new("Управление", "Завершить сценарий", NodeKind.End, "end")
    ];
}
