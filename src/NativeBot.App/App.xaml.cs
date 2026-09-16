using System.IO;
using System.Windows;
using NativeBot.Storage;

namespace NativeBot.App;

public partial class App : System.Windows.Application
{
    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NativeBot");
    public static string DatabasePath { get; } = Path.Combine(DataDirectory, "nativebot.db");
    public static AppDatabase Database { get; } = new(DatabasePath);

    protected override async void OnStartup(StartupEventArgs e)
    {
        await Database.InitializeAsync();
        base.OnStartup(e);
    }
}
