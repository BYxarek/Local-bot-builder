using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using NativeBot.Storage;

namespace NativeBot.App;

public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = @"Local\BYxarek.NativeBot.App";
    private const string ActivationEventName = @"Local\BYxarek.NativeBot.App.Activate";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;

    public static bool StartedAutomatically { get; private set; }
    public static string CurrentTheme { get; private set; } = "light";
    public static string DisplayVersion { get; } = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Replace("-beta.", " beta ");
    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NativeBot");
    public static string DatabasePath { get; } = Path.Combine(DataDirectory, "nativebot.db");
    public static AppDatabase Database { get; } = new(DatabasePath);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        var mutex = new Mutex(true, InstanceMutexName, out var firstInstance);
        if (!firstInstance)
        {
            _activationEvent.Set();
            _activationEvent.Dispose();
            mutex.Dispose();
            Shutdown();
            return;
        }
        _instanceMutex = mutex;
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(_activationEvent, (_, timedOut) =>
        {
            if (!timedOut) Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray());
        }, null, Timeout.Infinite, false);

        StartedAutomatically = e.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);
        if (e.Args.Contains("--high-priority", StringComparer.OrdinalIgnoreCase))
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
        }

        await Database.InitializeAsync();
        ApplyTheme(await Database.GetSettingAsync("AppTheme") ?? "light");
        var window = new MainWindow();
        MainWindow = window;
        await window.InitializeAsync();
        if (!StartedAutomatically) window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationRegistration?.Unregister(null);
        _activationEvent?.Dispose();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    public static void ApplyTheme(string theme)
    {
        CurrentTheme = theme == "dark" ? "dark" : "light";
        var dark = CurrentTheme == "dark";
        var palette = new PaletteHelper();
        var materialTheme = palette.GetTheme();
        materialTheme.SetBaseTheme(dark ? BaseTheme.Dark : BaseTheme.Light);
        materialTheme.SetPrimaryColor(dark
            ? System.Windows.Media.Color.FromRgb(96, 96, 96)
            : System.Windows.Media.Color.FromRgb(63, 81, 181));
        materialTheme.SetSecondaryColor(dark
            ? System.Windows.Media.Color.FromRgb(158, 158, 158)
            : System.Windows.Media.Color.FromRgb(3, 169, 244));
        palette.SetTheme(materialTheme);
        Current.Resources["PageBackgroundBrush"] = new SolidColorBrush(dark
            ? System.Windows.Media.Color.FromRgb(26, 26, 26)
            : System.Windows.Media.Color.FromRgb(232, 234, 242));
        foreach (Window window in Current.Windows) ApplyWindowTheme(window);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => ApplyWindowTheme((Window)sender);

    private static void ApplyWindowTheme(Window window)
    {
        var enabled = CurrentTheme == "dark" ? 1 : 0;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

}
