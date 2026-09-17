using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NativeBot.App;
using NativeBot.Core;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Size = System.Windows.Size;
using TabControl = System.Windows.Controls.TabControl;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new App();
        app.InitializeComponent();
        var output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("bin", "ui-tests"));
        Directory.CreateDirectory(output);
        var captures = 0;

        App.ApplyTheme("light");
        AssertPageBackground();
        var main = new MainWindow { WindowState = WindowState.Normal };
        Capture(main, output, "light-main-bots", 980, 640);
        captures++;

        App.ApplyTheme("dark");
        ((TabItem)main.FindName("SettingsTab")).IsSelected = true;
        ((ComboBox)main.FindName("ThemeBox")).SelectedIndex = 1;
        ((CheckBox)main.FindName("AutoStartBox")).IsChecked = true;
        ((TextBlock)main.FindName("VersionTextBlock")).Text = App.DisplayVersion;
        Capture(main, output, "dark-main-settings", 1200, 760);
        captures++;

        App.ApplyTheme("light");
        var addBotWindow = new AddBotWindow();
        AssertChildWindow(addBotWindow);
        Capture(addBotWindow, output, "light-add-bot", 820, 700);
        captures++;

        var bot = new Bot(Guid.NewGuid(), "Демонстрационный бот", "@nativebot_demo", UpdateMode: BotUpdateMode.Webhook);
        var botWindow = new BotWindow(bot);
        AssertChildWindow(botWindow);
        var botTabs = (TabControl)botWindow.FindName("BotTabs");
        for (var index = 0; index < botTabs.Items.Count; index++)
        {
            App.ApplyTheme(index % 2 == 0 ? "dark" : "light");
            botTabs.SelectedIndex = index;
            Capture(botWindow, output, $"bot-{index + 1}-{Slug(((TabItem)botTabs.Items[index]).Header?.ToString())}", 1280, 780);
            captures++;
        }

        var now = DateTimeOffset.Now;
        var user = new BotUser(bot.Id, 123456789, "native_user", "Иван", "Петров", "ru", now.AddDays(-30), now);
        var userWindow = new UserWindow(user);
        AssertChildWindow(userWindow);
        ((TextBlock)userWindow.FindName("TitleText")).Text = "Иван Петров";
        ((TextBlock)userWindow.FindName("SummaryText")).Text = "Telegram ID: 123456789\nUsername: native_user\nЯзык: ru\nСтатус: Активен";
        var userTabs = (TabControl)userWindow.FindName("UserTabs");
        for (var index = 0; index < userTabs.Items.Count; index++)
        {
            App.ApplyTheme(index % 2 == 0 ? "light" : "dark");
            userTabs.SelectedIndex = index;
            Capture(userWindow, output, $"user-{index + 1}-{Slug(((TabItem)userTabs.Items[index]).Header?.ToString())}", 920, 700);
            captures++;
        }

        Console.WriteLine($"PASS: {captures} экранов Material Design 3 отрисованы. PNG: {output}");
        typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, true);
        main.Close();
        Dispatcher.CurrentDispatcher.InvokeShutdown();
    }

    private static void Capture(Window window, string directory, string name, int width, int height)
    {
        var root = (FrameworkElement)window.Content;
        if (root is not System.Windows.Controls.Panel { Background: not null })
            throw new InvalidOperationException($"Корневой контейнер экрана {name} не закрашивает клиентскую область окна.");
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        var pixels = new byte[width * height * 4];
        image.CopyPixels(pixels, width * 4, 0);
        if (VisualVariance(pixels) < 120)
            throw new InvalidOperationException($"Экран {name} отрисован почти одноцветным.");
        if (App.CurrentTheme == "dark") AssertMonochrome(pixels, name);

        var path = Path.Combine(directory, name + ".png");
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
    }

    private static void AssertChildWindow(Window window)
    {
        if (window.ShowInTaskbar || window.WindowStartupLocation != WindowStartupLocation.CenterOwner)
            throw new InvalidOperationException($"Окно {window.GetType().Name} должно открываться как дочерний диалог, а не как отдельное приложение.");
    }

    private static double VisualVariance(byte[] pixels)
    {
        double sum = 0;
        double squareSum = 0;
        var count = pixels.Length / 4;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var luminance = (pixels[index] + pixels[index + 1] + pixels[index + 2]) / 3.0;
            sum += luminance;
            squareSum += luminance * luminance;
        }
        var mean = sum / count;
        return squareSum / count - mean * mean;
    }

    private static void AssertPageBackground()
    {
        if (System.Windows.Application.Current.TryFindResource("PageBackgroundBrush") is not SolidColorBrush brush)
            throw new InvalidOperationException("Не задан общий фон страниц PageBackgroundBrush.");
        var color = brush.Color;
        if (Math.Max(color.R, Math.Max(color.G, color.B)) > 247 || Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B)) < 3)
            throw new InvalidOperationException($"Светлый фон страниц остаётся белым или ахроматическим: {color}.");
    }

    private static void AssertMonochrome(byte[] pixels, string name)
    {
        var chromaticPixels = 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var max = Math.Max(pixels[index], Math.Max(pixels[index + 1], pixels[index + 2]));
            var min = Math.Min(pixels[index], Math.Min(pixels[index + 1], pixels[index + 2]));
            if (max - min > 3) chromaticPixels++;
        }
        if (chromaticPixels > pixels.Length / 400)
            throw new InvalidOperationException($"Экран {name} содержит цветные пиксели вне монохромной палитры.");
    }

    private static string Slug(string? value) => value switch
    {
        "Конструктор" => "builder",
        "Пользователи" => "users",
        "Рассылки" => "broadcasts",
        "Журнал" => "logs",
        "Настройки" => "settings",
        "История" => "history",
        "Теги" => "tags",
        "Поля" => "fields",
        "Ожидания" => "waits",
        _ => "screen"
    };
}
