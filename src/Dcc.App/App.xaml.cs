using System.IO;
using System.Threading;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NvUv.Dcc.Core;
using MessageBox = System.Windows.MessageBox;
using Size = System.Windows.Size;

namespace NvUv.Dcc.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceName = "Local\\NV-UV.DCC.UI.7D33F991";

    [STAThread]
    public static void Main()
    {
        string[] arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (AutoStartService.IsConfigurationCommand(arguments))
        {
            Environment.ExitCode = AutoStartService.RunConfigurationCommand(arguments);
            return;
        }

        if (arguments.Length is >= 2 and <= 4 &&
            string.Equals(arguments[0], "--render-preview", StringComparison.OrdinalIgnoreCase))
        {
            string? language = arguments.Length >= 3 ? arguments[2] : null;
            int? scale = arguments.Length >= 4 && int.TryParse(arguments[3], out int parsedScale) ? parsedScale : null;
            RenderPreview(arguments[1], language, scale);
            return;
        }
        if (arguments.Length is >= 2 and <= 4 &&
            string.Equals(arguments[0], "--render-games-preview", StringComparison.OrdinalIgnoreCase))
        {
            string? language = arguments.Length >= 3 ? arguments[2] : null;
            int? scale = arguments.Length >= 4 && int.TryParse(arguments[3], out int parsedScale) ? parsedScale : null;
            RenderGamesPreview(arguments[1], language, scale);
            return;
        }

        if (arguments.Length == 1 &&
            string.Equals(arguments[0], "--tray-probe", StringComparison.OrdinalIgnoreCase))
        {
            DccPaths.EnsureDataDirectoryReady();
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            var probeApp = new App();
            probeApp.InitializeComponent();
            var probeWindow = new MainWindow();
            probeWindow.Close();
            probeApp.Shutdown();
            return;
        }

        using var singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out bool createdNew);
        if (!createdNew)
        {
            DccLocalization.SetLanguage(DccUserSettingsStore.Load().Language);
            MessageBox.Show(
                DccLocalization.T("already_running"),
                "DCC",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DccPaths.EnsureDataDirectoryReady();

        // Keep all WPF drawing on the CPU so DCC cannot consume the GPU
        // capacity it is trying to govern.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";
        bool scheduledStart = arguments.Length == 1 &&
                              string.Equals(
                                  arguments[0],
                                  DccAutoStartTaskDefinition.ScheduledStartArgument,
                                  StringComparison.OrdinalIgnoreCase);
        DccLog.Ui("process.start",
            $"version={version}; os={Environment.OSVersion.VersionString}; scheduled_start={scheduledStart}; elevated={AutoStartService.IsCurrentProcessElevated()}");
        var app = new App();
        app.InitializeComponent();
        app.Run(new MainWindow());
    }

    private static void RenderPreview(string destinationPath, string? language, int? scale)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow(enableTray: false);
        window.ApplyPreviewSettings(language, scale);
        window.GpuNameText.Text = "RTX 5070 Ti";
        window.GameNameText.Text = "Grim Dawn";
        window.CurrentFpsText.Text = "120";
        window.PowerText.Text = "71 W";
        window.LoadText.Text = "78 %";
        window.TargetFpsSlider.Value = 120;
        window.TargetFpsBox.Text = "120";
        window.FrameLimiterCheckBox.IsChecked = true;
        window.FrameLimiterFpsBox.Visibility = Visibility.Visible;
        window.FrameLimiterFpsBox.IsEnabled = true;
        window.FrameLimiterFpsBox.Text = "40";
        window.FrameLimiterFpsBox.BorderBrush =
            (Brush)window.FindResource("NvGreen");
        window.AutoStartToggle.IsChecked = true;
        window.ApplyPreviewActiveState();
        var size = new Size(window.Width, window.Height);
        FrameworkElement root = (FrameworkElement)window.Content;
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width),
            (int)Math.Ceiling(size.Height),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(root);

        string fullPath = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(fullPath);
        encoder.Save(output);
        window.Close();
        app.Shutdown();
    }

    private static void RenderGamesPreview(string destinationPath, string? language, int? scale)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new App();
        app.InitializeComponent();
        DccLocalization.SetLanguage(language is "de" or "en" or "ru" or "es" ? language : "en");
        DccProfileStore profiles = DccProfileStore.Load();
        IReadOnlyCollection<DccGameHistoryEntry> history = DccGameHistoryStore.Load().Entries;
        if (history.Count == 0)
        {
            history =
            [
                new DccGameHistoryEntry(
                    "Grim Dawn", "grim dawn.exe", "Grim Dawn",
                    DateTimeOffset.UtcNow.AddDays(-8), DateTimeOffset.UtcNow,
                    4, DccControlMethod.DccGovernor, 120),
                new DccGameHistoryEntry(
                    "Jotunnslayer: Hordes of Hel", "jotunnslayer.exe", "Jotunnslayer: Hordes of Hel",
                    DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-1),
                    2, DccControlMethod.NvidiaEfficiency, 100)
            ];
        }
        var window = new GamesProfilesWindow(history, profiles.Profiles);
        window.ApplyPreviewSettings(language, scale);
        var size = new Size(window.Width, window.Height);
        FrameworkElement root = (FrameworkElement)window.Content;
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width),
            (int)Math.Ceiling(size.Height),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(root);
        string fullPath = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(fullPath);
        encoder.Save(output);
        window.Close();
        app.Shutdown();
    }
}
