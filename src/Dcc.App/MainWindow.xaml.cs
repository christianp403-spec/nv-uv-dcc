using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NvUv.Dcc.Core;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace NvUv.Dcc.App;

public partial class MainWindow : Window
{
    private const double BaseWindowWidth = 300;
    private const double BaseWindowHeight = 350;
    private const int NvidiaAutomaticSliderValue = DccFpsRange.NvidiaEfficiencyFloor - 5;

    private readonly NvidiaSmiClient _nvidia = new();
    private readonly DccAutomaticClockGovernor _governor = new();
    private readonly DispatcherTimer _controlTimer;
    private readonly DispatcherTimer _targetFpsCommitTimer;
    private readonly SemaphoreSlim _controlGate = new(1, 1);

    private DccGameDetector? _gameDetector;
    private DccProfileStore _profiles = DccProfileStore.Load();
    private DccGameHistoryStore _history = DccGameHistoryStore.Load();
    private IReadOnlyList<DccDriverApplication> _driverApplications = [];
    private readonly HashSet<int> _rememberedProcessIds = [];
    private DccUserSettings _settings = DccUserSettingsStore.Load();
    private SupportedClockCatalog? _clockCatalog;
    private NvidiaGpuInfo? _gpu;
    private DccDetectedGame? _governedGame;
    private int? _perGameProcessId;
    private ElevatedClockCapSession? _session;
    private ElevatedClockCapSession? _baselineSession;
    private bool _capApplied;
    private bool _frameLimiterApplied;
    private bool _vsyncApplied;
    private bool _jpacApplied;
    private bool _busy;
    private bool _loading;
    private bool _closingAfterReset;
    private bool _recoveryRequired;
    private DateTimeOffset? _lastFreshGameAt;
    private string? _trayGameName;
    private string? _lastLoggedDetection;
    private DccFpsSample _lastFpsSample = DccFpsSample.Unavailable(
        0,
        "NV-UV ETW",
        "DCC is not active.");
    private DccPerformanceRecorder? _performanceRecorder;
    private DccJpacVerification _jpacVerification = DccJpacVerification.NotRequested;
    private DccClockChangeAssessment? _lastClockChangeAssessment;
    private DccGameDisplayInfo _gameDisplayInfo = DccGameDisplayInfo.Unknown;
    private DccGameDisplayInfo _jpacPrimaryDisplay = DccGameDisplayInfo.Unknown;
    private DateTimeOffset? _jpacArmedAtUtc;
    private string? _lastJpacRequirement;
    private DateTimeOffset? _lastGameDisplayProbeAt;
    private NativeTrayIcon? _trayIcon;

    public MainWindow() : this(enableTray: true)
    {
    }

    internal MainWindow(bool enableTray)
    {
        _loading = true;
        _targetFpsCommitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _targetFpsCommitTimer.Tick += (_, _) =>
        {
            _targetFpsCommitTimer.Stop();
            CommitTargetFps();
        };
        InitializeComponent();
        _settings = _settings.WithValidatedValues();
        MethodComboBox.SelectedIndex = _settings.ControlMethod == DccControlMethod.NvidiaEfficiency ? 1 : 0;
        ActivationScopeComboBox.SelectedIndex = _settings.ActivationScope == DccActivationScope.PerGame ? 1 : 0;
        FrameLimiterCheckBox.IsChecked = _settings.FrameLimiterEnabled;
        VsyncCheckBox.IsChecked = _settings.NvidiaVsyncEnabled;
        FrameLimiterFpsBox.Text = _settings.FrameLimitFps.ToString(CultureInfo.InvariantCulture);
        AutoStartToggle.IsChecked = AutoStartService.IsEnabled();
        StartMinimizedToggle.IsChecked = _settings.StartMinimizedToTray;
        DccLocalization.SetLanguage(_settings.Language);
        ApplyUiScale(recenter: false);
        ApplyLocalization();
        _loading = false;
        if (enableTray)
            InitializeTrayIcon();
        if (enableTray && _settings.StartMinimizedToTray && _trayIcon is not null)
        {
            ShowActivated = false;
            ShowInTaskbar = false;
        }
        _controlTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _controlTimer.Tick += ControlTimer_Tick;
    }

    private void ApplyLocalization()
    {
        Title = DccLocalization.T("title_bar");
        TitleBarText.Text = DccLocalization.T("title_bar");
        VersionText.Text = ShortAppVersion();
        ScaleButton.Content = $"{_settings.UiScalePercent}%";
        ScaleButton.ToolTip = DccLocalization.T("scale_tip");
        MinimizeButton.ToolTip = DccLocalization.T("minimize");
        CloseButton.ToolTip = DccLocalization.T("close");
        if (_gpu is null)
            GpuNameText.Text = DccLocalization.T("gpu_detecting");

        GameHeaderText.Text = "NVIDIA PROFILE";
        if (GameNameText.Text == "—")
            GameNameText.ToolTip = DccLocalization.T("no_game");
        FpsTelemetryPanel.ToolTip = DccLocalization.T("fps_tip");
        PowerTelemetryPanel.ToolTip = DccLocalization.T("power_tip");
        LoadTelemetryPanel.ToolTip = DccLocalization.T("load_tip");

        EnableText.Text = DccLocalization.T("enable");
        ActiveToggle.ToolTip = _settings.ControlMethod == DccControlMethod.NvidiaEfficiency
            ? DccLocalization.T("enable_tip_nvidia")
            : DccLocalization.T("enable_tip");
        ActiveHintText.Text = _session is null
            ? DccLocalization.T("dcc_off")
            : DccLocalization.T("dcc_waiting");
        ActivityIndicator.ToolTip = DccLocalization.T("lamp_tip");
        AutoStartText.Text = DccLocalization.T("autostart");
        AutoStartToggle.ToolTip = DccLocalization.T("autostart_tip");
        StartMinimizedText.Text = DccLocalization.T("start_minimized");
        StartMinimizedToggle.ToolTip = DccLocalization.T("start_minimized_tip");

        bool nvidiaMethod = _settings.ControlMethod == DccControlMethod.NvidiaEfficiency;
        TargetHeaderText.Text = nvidiaMethod
            ? DccLocalization.T("nvidia_target_fps")
            : DccLocalization.T("automatic_control");
        MethodHeaderText.Text = DccLocalization.T("method");
        MethodHintText.Text = DccLocalization.T("method_hint");
        MethodDccItem.Content = DccLocalization.T("method_dcc");
        MethodDccItem.ToolTip = DccLocalization.T("method_dcc_tip");
        MethodNvidiaItem.Content = DccLocalization.T("method_nvidia");
        MethodNvidiaItem.ToolTip = DccLocalization.T("method_nvidia_tip");
        MethodComboBox.ToolTip = _settings.ControlMethod == DccControlMethod.NvidiaEfficiency
            ? DccLocalization.T("method_nvidia_tip")
            : DccLocalization.T("method_dcc_tip");
        TargetHintText.Text = nvidiaMethod
            ? DccLocalization.T("target_hint_nvidia")
            : DccLocalization.T("target_hint_auto");
        string targetToolTip = DccLocalization.T("target_tip_nvidia");
        TargetFpsBox.ToolTip = targetToolTip;
        TargetFpsSlider.ToolTip = targetToolTip;
        UpdateAutomaticTargetDisplay();
        AutomaticTargetPanel.ToolTip = DccLocalization.T("automatic_gpu_load_tip");
        ActivationPersistentItem.Content = DccLocalization.T("activation_persistent");
        ActivationPerGameItem.Content = DccLocalization.T("activation_per_game");
        ActivationScopeComboBox.ToolTip = _settings.ActivationScope == DccActivationScope.PerGame
            ? DccLocalization.T("activation_per_game_tip")
            : DccLocalization.T("activation_persistent_tip");
        FrameLimiterCheckBox.Content = DccLocalization.T("frame_limiter");
        FrameLimiterCheckBox.ToolTip = DccLocalization.T("frame_limiter_tip");
        FrameLimiterFpsBox.ToolTip = DccLocalization.T("frame_limiter_value_tip");
        VsyncCheckBox.Content = DccLocalization.T("vsync");
        VsyncCheckBox.ToolTip = DccLocalization.T("vsync_tip");
        UpdateTargetControls();
        ResetButton.ToolTip = DccLocalization.T("reset_tip");
        MenuButton.ToolTip = DccLocalization.T("menu_tip");
        if (StatusText.Text == "Bereit" || StatusText.Text == "Ready" || StatusText.Text == "Готово" || StatusText.Text == "Listo")
            StatusText.Text = DccLocalization.T("ready");
        UpdateActivityIndicator();
        UpdateTrayUi();
    }

    private void InitializeTrayIcon()
    {
        try
        {
            _trayIcon = new NativeTrayIcon(
                () => Dispatcher.BeginInvoke(RestoreFromTray),
                () => Dispatcher.BeginInvoke(Close));
            UpdateTrayUi();
        }
        catch
        {
            DisposeTrayIcon();
            // DCC remains usable if Explorer's notification area is unavailable.
        }
    }

    private void UpdateTrayUi()
    {
        if (_trayIcon is null)
            return;

        string state = _recoveryRequired
            ? DccLocalization.T("tray_recovery")
            : _session is not null ? DccLocalization.T("tray_active") : DccLocalization.T("tray_off");
        string game = string.IsNullOrWhiteSpace(_trayGameName)
            ? DccLocalization.T("tray_no_game")
            : _trayGameName;
        string fpsState = _settings.FrameLimiterEnabled
            ? $"{_settings.FrameLimitFps} FPS cap"
            : _settings.ControlMethod == DccControlMethod.NvidiaEfficiency
                ? _settings.NvidiaTargetFpsManual
                    ? $"{DccLocalization.T("nvidia_target_fps")}: {EffectiveNvidiaTargetFps()}"
                    : _jpacApplied
                        ? $"{DccLocalization.T("nvidia_target_fps")}: AUTO · {EffectiveNvidiaTargetFps()}"
                        : $"{DccLocalization.T("nvidia_target_fps")}: AUTO"
                : _governor.DetectedFpsLimit is double detectedFpsLimit
                    ? $"DCC AUTO · {detectedFpsLimit:F0} FPS"
                    : "DCC AUTO";
        string text = $"{state} · {game} · {fpsState}";
        NativeTrayState trayState = _recoveryRequired
            ? NativeTrayState.Recovery
            : _session is not null ? NativeTrayState.Active : NativeTrayState.Off;
        _trayIcon.Update(
            trayState,
            text,
            DccLocalization.T("tray_show"),
            DccLocalization.T("tray_exit"));
    }

    private void UpdateActivityIndicator()
    {
        if (ActivityLamp is null)
            return;

        if (_recoveryRequired)
        {
            ActivityLamp.Fill = (Brush)FindResource("WarnOrange");
            ActivityLamp.Effect = FindResource("NvGlowSmall") as System.Windows.Media.Effects.Effect;
            ActivityLampText.Text = DccLocalization.T("lamp_recovery");
            ActivityLampText.Foreground = (Brush)FindResource("WarnOrange");
        }
        else if (_session is not null)
        {
            ActivityLamp.Fill = (Brush)FindResource("NvGreen");
            ActivityLamp.Effect = FindResource("NvGlowSmall") as System.Windows.Media.Effects.Effect;
            ActivityLampText.Text = DccLocalization.T("lamp_active");
            ActivityLampText.Foreground = (Brush)FindResource("NvGreen");
        }
        else
        {
            ActivityLamp.Fill = (Brush)FindResource("TextMuted");
            ActivityLamp.Effect = null;
            ActivityLampText.Text = DccLocalization.T("lamp_off");
            ActivityLampText.Foreground = (Brush)FindResource("TextMuted");
        }
    }

    private void RestoreFromTray()
    {
        ShowActivated = true;
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    private void DisposeTrayIcon()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private void ApplyUiScale(bool recenter)
    {
        double scale = _settings.UiScalePercent / 100d;
        double width = BaseWindowWidth * scale;
        double height = BaseWindowHeight * scale;
        double centerX = Left + (ActualWidth > 0 ? ActualWidth : Width) / 2d;
        double centerY = Top + (ActualHeight > 0 ? ActualHeight : Height) / 2d;

        Width = width;
        Height = height;
        if (!recenter || !IsLoaded)
            return;

        Rect area = SystemParameters.WorkArea;
        Left = Math.Clamp(centerX - width / 2d, area.Left, Math.Max(area.Left, area.Right - width));
        Top = Math.Clamp(centerY - height / 2d, area.Top, Math.Max(area.Top, area.Bottom - height));
    }

    internal void ApplyPreviewSettings(string? language, int? scale)
    {
        _loading = true;
        string previewLanguage = language is "de" or "en" or "ru" or "es" ? language : "en";
        _settings = (_settings with
        {
            Language = previewLanguage,
            UiScalePercent = scale ?? _settings.UiScalePercent,
            ControlMethod = DccControlMethod.DccGovernor,
            FrameLimiterEnabled = false,
            NvidiaVsyncEnabled = false,
            ActivationScope = DccActivationScope.Persistent
        }).WithValidatedValues();
        MethodComboBox.SelectedIndex = 0;
        ActivationScopeComboBox.SelectedIndex = 0;
        FrameLimiterCheckBox.IsChecked = false;
        VsyncCheckBox.IsChecked = false;
        _loading = false;
        DccLocalization.SetLanguage(_settings.Language);
        ApplyUiScale(recenter: false);
        ApplyLocalization();
    }

    internal void ApplyPreviewActiveState()
    {
        ActiveToggle.IsChecked = true;
        ActiveHintText.Text = DccLocalization.T("lamp_active");
        ActivityLamp.Fill = (Brush)FindResource("NvGreen");
        ActivityLamp.Effect = FindResource("NvGlowSmall") as System.Windows.Media.Effects.Effect;
        ActivityLampText.Text = DccLocalization.T("lamp_active");
        ActivityLampText.Foreground = (Brush)FindResource("NvGreen");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings.StartMinimizedToTray && _trayIcon is not null)
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            Hide();
            UpdateTrayUi();
        }

        _loading = true;
        bool restoreGovernor = _settings.GovernorEnabled;
        ConfigureTargetSlider();
        TargetFpsSlider.Value = NvidiaTargetSliderValue();
        TargetFpsBox.Text = _settings.NvidiaTargetFpsManual
            ? _settings.NvidiaTargetFps.ToString(CultureInfo.InvariantCulture)
            : "AUTO";
        FrameLimiterFpsBox.Text = _settings.FrameLimitFps.ToString(CultureInfo.InvariantCulture);
        MethodComboBox.SelectedIndex = _settings.ControlMethod == DccControlMethod.NvidiaEfficiency ? 1 : 0;
        ActivationScopeComboBox.SelectedIndex = _settings.ActivationScope == DccActivationScope.PerGame ? 1 : 0;
        FrameLimiterCheckBox.IsChecked = _settings.FrameLimiterEnabled;
        VsyncCheckBox.IsChecked = _settings.NvidiaVsyncEnabled;
        StartMinimizedToggle.IsChecked = _settings.StartMinimizedToTray;
        UpdateTargetControls();
        _loading = false;

        await LoadFoundationAsync();
        if (restoreGovernor && !_recoveryRequired)
            await ActivateAsync();
        if (_settings.PerformanceLoggingEnabled && _gpu is not null && _session is null)
        {
            SetBusy(true, "Starting baseline performance logging …");
            try
            {
                await EnsureBaselineSessionAsync();
                StatusText.Text = "Performance logging on · DCC control off";
            }
            catch (Exception ex)
            {
                _settings = (_settings with { PerformanceLoggingEnabled = false }).WithValidatedValues();
                DccUserSettingsStore.Save(_settings);
                DccLog.Ui("performance.baseline.start_failed", exception: ex);
                StatusText.Text = "Performance logging could not start";
            }
            finally
            {
                SetBusy(false);
            }
        }
        _controlTimer.Start();

    }

    private async Task LoadFoundationAsync()
    {
        SetBusy(true, DccLocalization.T("initializing"));
        DccLog.Ui("foundation.load.begin");
        try
        {
            RebuildGameDetector();

            DccActiveSessionState? recovery = DccActiveSessionStore.TryRead();
            DccFrameLimiterRecoveryState? frameLimiterRecovery = DccFrameLimiterRecoveryStore.TryRead();
            DccVsyncRecoveryState? vsyncRecovery = DccVsyncRecoveryStore.TryRead();
            DccJpacRecoveryState? jpacRecovery = DccJpacRecoveryStore.TryRead();
            IReadOnlyList<NvidiaGpuInfo> gpus = await _nvidia.QueryGpusAsync();
            _gpu = NvidiaGpuSelector.SelectPrimary(gpus, recovery?.GpuUuid);
            _clockCatalog = await _nvidia.QuerySupportedGraphicsClocksAsync(_gpu.Index);
            _jpacPrimaryDisplay = DccGameDisplayProbe.CapturePrimary();
            DccLog.Ui("foundation.load.complete",
                $"gpu_index={_gpu.Index}; gpu={_gpu.Name}; driver={_gpu.DriverVersion}; minimum_mhz={_clockCatalog.MinimumMHz}; maximum_mhz={_clockCatalog.MaximumMHz}; bins={_clockCatalog.Count}; history={_history.Entries.Count}; recovery={recovery is not null || frameLimiterRecovery is not null || vsyncRecovery is not null || jpacRecovery is not null}; " +
                $"primary_display={_jpacPrimaryDisplay.DisplayDeviceName}; primary_refresh_hz={_jpacPrimaryDisplay.DisplayRefreshLogValue}");
            GpuNameText.Text = ShortGpuName(_gpu.Name);
            GpuNameText.ToolTip = $"{_gpu.Name} · GPU {_gpu.Index} · {_gpu.PciBusId}";

            if (recovery is not null || frameLimiterRecovery is not null || vsyncRecovery is not null || jpacRecovery is not null)
            {
                _recoveryRequired = true;
                StatusText.Text = DccLocalization.T("recovery_needed");
                StatusText.Foreground = (Brush)FindResource("WarnOrange");
                UpdateActivityIndicator();
                UpdateTrayUi();
            }
            else
            {
                _recoveryRequired = false;
                SetInactiveVisuals(_history.Entries.Count == 0
                    ? DccLocalization.T("ready_profiles")
                    : DccLocalization.T("ready_history", _history.Entries.Count));
            }
        }
        catch (Exception ex)
        {
            ShowError(DccLocalization.T("error_initialization"), ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RebuildGameDetector()
    {
        var games = new List<DccGameDefinition>();
        foreach (DccDriverApplication application in _driverApplications)
            games.Add(application.ToGameDefinition());
        foreach (DccGameDefinition historical in _history.ToGameDefinitions())
            games.Add(historical);
        try
        {
            DccGameCatalog catalog = DccGameCatalog.Load();
            foreach (DccGameDefinition catalogGame in catalog.Games)
            {
                if (catalogGame.Ambiguous && !catalogGame.UserAdded)
                    continue;

                games.Add(catalogGame);
            }
        }
        catch (Exception ex)
        {
            DccLog.Ui("game.catalog.load_failed", exception: ex);
        }
        _gameDetector = new DccGameDetector(games);
    }

    private void RememberActivation(DccDetectedGame detected)
    {
        if (_lastGameDisplayProbeAt is null)
        {
            _gameDisplayInfo = DccGameDisplayProbe.Capture(detected.ProcessId);
            _lastGameDisplayProbeAt = DccMonotonicClock.Now;
        }
        if (!_rememberedProcessIds.Add(detected.ProcessId))
        {
            DccLog.Ui(
                "game.display.captured",
                $"game={detected.Game.Name}; executable={detected.Executable}; " +
                $"game_client={_gameDisplayInfo.GameClientLogValue}; display={_gameDisplayInfo.DisplayLogValue}; " +
                $"display_refresh_hz={_gameDisplayInfo.DisplayRefreshLogValue}; " +
                $"display_primary={_gameDisplayInfo.IsPrimary}; display_device={_gameDisplayInfo.DisplayDeviceName}");
            return;
        }
        _history.RememberActivation(
            detected.Game.Name,
            detected.Executable,
            detected.Game.NvidiaProfileName,
            _settings.ControlMethod,
            EffectiveControlTargetFps(),
            frameLimiterEnabled: _settings.FrameLimiterEnabled,
            frameLimitFps: _settings.FrameLimitFps,
            gameKey: detected.GameKey);
        DccLog.Ui(
            "game.history.remembered",
            $"game={detected.Game.Name}; executable={detected.Executable}; game_key={detected.GameKey}; " +
            $"identity={detected.GameIdentitySource}; method={_settings.ControlMethod}; " +
            $"control_target_fps={EffectiveControlTargetFps()}; frame_limiter={_settings.FrameLimiterEnabled}; " +
            $"frame_limit_fps={_settings.FrameLimitFps}; " +
            $"jpac_target_mode={(_settings.NvidiaTargetFpsManual ? "manual" : "auto")}; " +
            $"jpac_slider_fps={_settings.NvidiaTargetFps}; " +
            $"jpac_target_fps={EffectiveNvidiaTargetFps()}; " +
            $"game_client={_gameDisplayInfo.GameClientLogValue}; display={_gameDisplayInfo.DisplayLogValue}; " +
            $"display_refresh_hz={_gameDisplayInfo.DisplayRefreshLogValue}; " +
            $"display_primary={_gameDisplayInfo.IsPrimary}; display_device={_gameDisplayInfo.DisplayDeviceName}");
    }

    private void RefreshGameDisplayContext(DccDetectedGame detected)
    {
        DateTimeOffset now = DccMonotonicClock.Now;
        if (_lastGameDisplayProbeAt.HasValue &&
            now - _lastGameDisplayProbeAt.Value < TimeSpan.FromSeconds(10))
        {
            return;
        }
        _lastGameDisplayProbeAt = now;

        DccGameDisplayInfo updated = DccGameDisplayProbe.Capture(detected.ProcessId);
        if (updated == DccGameDisplayInfo.Unknown || updated == _gameDisplayInfo)
            return;

        DccGameDisplayInfo previous = _gameDisplayInfo;
        _gameDisplayInfo = updated;
        DccLog.Ui(
            "game.display.changed",
            $"game={detected.Game.Name}; executable={detected.Executable}; " +
            $"previous_game_client={previous.GameClientLogValue}; previous_display={previous.DisplayLogValue}; " +
            $"previous_display_refresh_hz={previous.DisplayRefreshLogValue}; " +
            $"game_client={updated.GameClientLogValue}; display={updated.DisplayLogValue}; " +
            $"display_refresh_hz={updated.DisplayRefreshLogValue}; " +
            $"display_primary={updated.IsPrimary}; display_device={updated.DisplayDeviceName}");
        StopPerformanceRecording();
    }

    private async void ControlTimer_Tick(object? sender, EventArgs e)
    {
        if (!await _controlGate.WaitAsync(0))
            return;

        try
        {
            DccDetectedGame? detected = _gameDetector?.DetectProcess();
            bool gameIsForeground = detected?.IsForeground == true;
            ElevatedClockCapSession? fpsSession = _session ?? _baselineSession;
            DccFpsSample fpsSample;
            if (fpsSession is not null && detected is not null && gameIsForeground)
            {
                (detected, fpsSample) = await QueryDetectedGameFpsAsync(fpsSession, detected);
                if (fpsSample.IsFresh)
                {
                    detected = detected with
                    {
                        FramesPerSecond = fpsSample.FramesPerSecond,
                        FpsFresh = true,
                        DetectionSource = fpsSample.Source
                    };
                }
            }
            else if (fpsSession is not null)
            {
                fpsSample = DccFpsSample.NoFrames(detected?.ProcessId ?? 0, "NV-UV ETW");
            }
            else
            {
                fpsSample = DccFpsSample.Unavailable(
                    detected?.ProcessId ?? 0,
                    "NV-UV ETW",
                    "DCC is not active.");
            }
            _lastFpsSample = fpsSample;

            NvidiaGpuInfo? telemetry = null;
            if (_gpu is not null)
            {
                try
                {
                    telemetry = await _nvidia.QueryGpuAsync(
                        _gpu.Index,
                        includeCoreVoltage: _settings.PerformanceLoggingEnabled);
                    if (!string.Equals(telemetry.Uuid, _gpu.Uuid, StringComparison.OrdinalIgnoreCase))
                        telemetry = null;
                    else if (!telemetry.TelemetryAvailable)
                        telemetry = null;
                }
                catch
                {
                    // UI falls back to placeholders; governor never acts without telemetry.
                }
            }

            UpdateLiveDisplay(detected, telemetry, fpsSample);
            if (detected is not null && gameIsForeground)
                RefreshGameDisplayContext(detected);
            if (_session is not null)
            {
                if (_settings.ControlMethod == DccControlMethod.NvidiaEfficiency)
                    await RunNvidiaEfficiencyAsync(detected);
                else if (telemetry is not null)
                    await RunGovernorAsync(detected, telemetry);
                else if (detected is null && (_governedGame is not null || _capApplied))
                {
                    if (_settings.ActivationScope == DccActivationScope.PerGame)
                        await DeactivateAsync(DccLocalization.T("per_game_complete"));
                    else
                        await EndGovernedGameAsync(DccLocalization.T("waiting_game"));
                }

            }
            TryRecordPerformance(detected, telemetry, fpsSample);
        }
        catch (Exception ex)
        {
            _controlTimer.Stop();
            try
            {
                await EndGovernedGameAsync(DccLocalization.T("control_stopped"));
                await DisposeSessionAsync();
                await DisposeBaselineSessionAsync();
            }
            catch
            {
                // The helper independently resets the cap when the pipe closes.
            }

            _settings = _settings with { GovernorEnabled = false };
            DccUserSettingsStore.Save(_settings);
            SetInactiveVisuals(DccLocalization.T("control_stopped"));
            ShowError(DccLocalization.T("error_control"), ex);
            _controlTimer.Start();
        }
        finally
        {
            _controlGate.Release();
        }
    }

    private static async Task<(DccDetectedGame Detected, DccFpsSample Fps)> QueryDetectedGameFpsAsync(
        ElevatedClockCapSession fpsSession,
        DccDetectedGame detected)
    {
        DccFpsSample gameFps = await fpsSession.QueryFpsAsync(detected.ProcessId);
        if (gameFps.IsFresh)
            return (detected, gameFps);

        foreach (DccRendererBridgeProcess bridge in
                 DccRendererBridgeResolver.FindForGameProcess(detected.ProcessId))
        {
            DccFpsSample bridgeFps = await fpsSession.QueryFpsAsync(bridge.ProcessId);
            if (!bridgeFps.IsFresh)
                continue;

            return (
                detected with
                {
                    PresentationProcessId = bridge.ProcessId,
                    PresentationExecutable = bridge.Executable
                },
                bridgeFps);
        }

        return (detected, gameFps);
    }

    private void UpdateLiveDisplay(DccDetectedGame? detected, NvidiaGpuInfo? telemetry, DccFpsSample fpsSample)
    {
        string detectionKey = detected is null
            ? "none"
            : $"{detected.GameKey}|{detected.DetectionSource}|fresh={detected.FpsFresh}|fps_pid={detected.FpsProcessId}";
        if (!string.Equals(detectionKey, _lastLoggedDetection, StringComparison.Ordinal))
        {
            DccLog.Ui("game.detection.changed",
                detected is null
                    ? "game=none"
                    : $"game={detected.Game.Name}; executable={detected.Game.Executable}; game_key={detected.GameKey}; " +
                      $"identity={detected.GameIdentitySource}; source={detected.DetectionSource}; " +
                      $"fresh={detected.FpsFresh}; game_pid={detected.ProcessId}; fps_pid={detected.FpsProcessId}; " +
                      $"fps_executable={detected.PresentationExecutable}");
            _lastLoggedDetection = detectionKey;
        }
        _trayGameName = detected?.Game.Name;
        GameNameText.Text = detected?.Game.Name ?? "—";
        GameNameText.ToolTip = detected is null
            ? DccLocalization.T("no_game")
            : string.IsNullOrWhiteSpace(detected.Game.NvidiaProfileName)
                ? $"{detected.Game.Name} · {detected.Executable}"
                : $"NVIDIA: {detected.Game.NvidiaProfileName} · {detected.Executable}" +
                  (detected.PresentationProcessId > 0
                      ? $" · FPS: {detected.PresentationExecutable}"
                      : string.Empty);
        CurrentFpsText.Text = detected?.FramesPerSecond is double fps
            ? fps.ToString("F0", CultureInfo.CurrentCulture)
            : "—";
        PowerText.Text = telemetry?.PowerWatts > 0
            ? telemetry.PowerWatts.ToString("F0", CultureInfo.CurrentCulture) + " W"
            : "—";
        LoadText.Text = telemetry is not null
            ? telemetry.UtilizationPercent.ToString("F0", CultureInfo.CurrentCulture) + " %"
            : "—";

        if (_session is not null && detected is not null && !detected.FpsFresh)
        {
            StatusText.Text = string.Equals(detected.DetectionSource, "Process", StringComparison.Ordinal)
                ? DccLocalization.T("waiting_game")
                : fpsSample.CollectorAvailable
                    ? DccLocalization.T("wait_fps")
                    : DccLocalization.T("fps_collector_required");
        }
        UpdateTrayUi();
    }

    private async Task RunGovernorAsync(DccDetectedGame? detected, NvidiaGpuInfo telemetry)
    {
        if (_clockCatalog is null || _gpu is null || _session is null)
            return;

        if (detected is null)
        {
            int? trackedProcessId = _perGameProcessId ?? _governedGame?.ProcessId;
            if (_jpacApplied &&
                trackedProcessId is int runningProcessId &&
                IsProcessRunning(runningProcessId))
            {
                // JPAC is a stable driver-side session setting. Keep it applied
                // while the game is merely backgrounded or temporarily absent
                // from foreground detection; resetting here causes needless
                // disable/enable churn on every Alt+Tab.
                StatusText.Text = DccLocalization.T("nvidia_efficiency_active");
                return;
            }

            if (_settings.ActivationScope == DccActivationScope.PerGame &&
                _perGameProcessId is int perGameProcessId &&
                !IsProcessRunning(perGameProcessId))
            {
                await DeactivateAsync(DccLocalization.T("per_game_complete"));
                return;
            }

            if (_governedGame is not null || _capApplied)
                await EndGovernedGameAsync(DccLocalization.T("waiting_game"));
            else
                StatusText.Text = DccLocalization.T("waiting_game");
            return;
        }

        if (!detected.IsForeground || !detected.FpsFresh || detected.FramesPerSecond is null)
        {
            bool graceExpired = _lastFreshGameAt is null ||
                DateTimeOffset.UtcNow - _lastFreshGameAt.Value >= TimeSpan.FromSeconds(5);
            if (_governedGame is not null && graceExpired)
                await EndGovernedGameAsync(DccLocalization.T("waiting_game"));
            return;
        }

        _lastFreshGameAt = DateTimeOffset.UtcNow;

        if (_settings.ActivationScope == DccActivationScope.PerGame)
        {
            if (_perGameProcessId is int perGameProcessId &&
                perGameProcessId != detected.ProcessId)
            {
                await DeactivateAsync(DccLocalization.T("per_game_complete"));
                return;
            }
            _perGameProcessId = detected.ProcessId;
        }
        else
        {
            _perGameProcessId = null;
        }

        string detectedKey = detected.GameKey;
        string? governedKey = _governedGame?.GameKey;
        if (!string.Equals(detectedKey, governedKey, StringComparison.OrdinalIgnoreCase))
        {
            if (_governedGame is not null)
            {
                if (_settings.ActivationScope == DccActivationScope.PerGame)
                {
                    await DeactivateAsync(DccLocalization.T("per_game_complete"));
                    return;
                }
                await EndGovernedGameAsync(DccLocalization.T("game_switch"));
            }

            _governedGame = detected;
            _lastClockChangeAssessment = null;
            RememberActivation(detected);
            DccLearnedProfile? learned = _profiles.Find(
                _gpu.Uuid,
                detectedKey,
                DccLearnedProfile.AutomaticTargetFps);
            int resumeConfidence = learned is not null &&
                                   string.Equals(
                                       learned.DriverVersion,
                                       _gpu.DriverVersion,
                                       StringComparison.OrdinalIgnoreCase)
                ? learned.EffectiveResumeConfidence
                : 0;
            DccGovernorDecision start = _governor.Start(
                _clockCatalog,
                learned?.LearnedClockMHz,
                learned?.LowLoadClockMHz,
                learned?.ResumeClockMHz,
                DccMonotonicClock.Now,
                deferLearnedApplication: true,
                requireLearningConfidence: true,
                resumeConfidence: resumeConfidence,
                clockChangeSensitivityScore:
                    learned?.EffectiveClockChangeSensitivityScore ?? 0);
            UpdateAutomaticTargetDisplay();
            if (start.ApplyClockMHz is int initialClockMHz)
            {
                await _session.SetAsync(_clockCatalog.MinimumMHz, initialClockMHz);
                _capApplied = true;
            }
            StatusText.Text = learned is null
                ? DccLocalization.GovernorStatus(
                    start.Status,
                    detected.FramesPerSecond.Value,
                    DccLearnedProfile.AutomaticTargetFps)
                : DccLocalization.T("profile_found");
            return;
        }

        _governedGame = detected;
        DccGovernorDecision decision = _governor.Observe(new DccGovernorSample(
            DccMonotonicClock.Now,
            detected.FramesPerSecond.Value,
            telemetry.UtilizationPercent,
            telemetry.GraphicsClockMHz,
            _lastFpsSample.P95FrameTimeMilliseconds,
            _lastFpsSample.P99FrameTimeMilliseconds,
            telemetry.PowerWatts));
        if (decision.ClockChangeAssessment is DccClockChangeAssessment assessment)
        {
            _lastClockChangeAssessment = assessment;
            DccLog.Ui(
                "governor.clock_change.assessed",
                $"game={detected.GameKey}; from_mhz={assessment.FromClockMHz}; to_mhz={assessment.ToClockMHz}; " +
                $"reference_fps={assessment.ReferenceFps.ToString("F2", CultureInfo.InvariantCulture)}; " +
                $"observed_fps={assessment.ObservedFps.ToString("F2", CultureInfo.InvariantCulture)}; " +
                $"reference_p95_ms={assessment.ReferenceP95FrameTimeMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}; " +
                $"observed_p95_ms={assessment.ObservedP95FrameTimeMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}; " +
                $"reference_p99_ms={assessment.ReferenceP99FrameTimeMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}; " +
                $"observed_p99_ms={assessment.ObservedP99FrameTimeMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}; " +
                $"reference_util={assessment.ReferenceGpuUtilizationPercent.ToString("F1", CultureInfo.InvariantCulture)}; " +
                $"observed_util={assessment.ObservedGpuUtilizationPercent.ToString("F1", CultureInfo.InvariantCulture)}; " +
                $"comparable={assessment.ComparableWorkload}; spike={assessment.CorrelatedFrameTimeSpike}; " +
                $"sensitivity_score={assessment.SensitivityScore}; sensitive={assessment.IsSensitive}; reason={assessment.Reason}");
        }
        UpdateAutomaticTargetDisplay();

        if (decision.ResetClock)
        {
            await _session.ResetAsync();
            _capApplied = false;
        }

        if (decision.ApplyClockMHz is int clockMHz)
        {
            await _session.SetAsync(_clockCatalog.MinimumMHz, clockMHz);
            _capApplied = true;
        }

        if (decision.ResumeValidation != DccResumeValidation.None)
        {
            _profiles.UpdateResumeConfidence(
                _gpu.Uuid,
                detectedKey,
                DccLearnedProfile.AutomaticTargetFps,
                _governor.ResumeConfidence);
        }

        bool profileSaveFailed = false;
        if (decision.PersistProfile && _governor.TrustedClockMHz is int trustedClock)
        {
            try
            {
                _profiles.Upsert(new DccLearnedProfile(
                    _gpu.Uuid,
                    _gpu.Name,
                    _gpu.DriverVersion,
                    detected.Game.Name,
                    detectedKey,
                    DccLearnedProfile.AutomaticTargetFps,
                    trustedClock,
                    decision.EvaluatedFps,
                    _governor.LearningWindows,
                    DateTimeOffset.UtcNow,
                    _governor.LowLoadClockMHz,
                    _governor.ResumeClockMHz,
                    _governor.ResumeConfidence,
                    _governor.ClockChangeSensitivityScore));
            }
            catch (Exception ex)
            {
                DccLog.Ui(
                    "profile.persistence.failed",
                    $"executable={detectedKey}; trusted_mhz={trustedClock}; " +
                    $"low_load_mhz={_governor.LowLoadClockMHz}; resume_mhz={_governor.ResumeClockMHz}; " +
                    $"fps={decision.EvaluatedFps}; windows={_governor.LearningWindows}",
                    ex);
                profileSaveFailed = true;
            }
        }

        StatusText.Text = profileSaveFailed
            ? DccLocalization.T("profile_save_failed")
            : DccLocalization.GovernorStatus(
                decision.Status,
                decision.EvaluatedFps,
                DccLearnedProfile.AutomaticTargetFps);
    }

    private async Task RunNvidiaEfficiencyAsync(DccDetectedGame? detected)
    {
        if (_session is null)
            return;

        if (detected is null)
        {
            if (_settings.ActivationScope == DccActivationScope.PerGame &&
                _perGameProcessId is int perGameProcessId &&
                !IsProcessRunning(perGameProcessId))
            {
                await DeactivateAsync(DccLocalization.T("per_game_complete"));
                return;
            }

            if (_governedGame is not null && !IsProcessRunning(_governedGame.ProcessId))
                ForgetNvidiaGameTracking();

            StatusText.Text = _jpacApplied
                ? DccLocalization.T("nvidia_efficiency_active")
                : DccLocalization.T("waiting_game");
            return;
        }

        if (!detected.IsForeground || !detected.FpsFresh)
        {
            // Unlike DCC's actively adjusted clock cap, JPAC does not need to
            // be withdrawn when focus or fresh FPS telemetry is temporarily
            // lost. The game-process lifetime governs cleanup instead.
            StatusText.Text = _jpacApplied
                ? DccLocalization.T("nvidia_efficiency_active")
                : DccLocalization.T("waiting_game");
            return;
        }

        _lastFreshGameAt = DateTimeOffset.UtcNow;

        DateTimeOffset? processStartUtc = TryGetProcessStartTimeUtc(detected.ProcessId);
        if (!_jpacArmedAtUtc.HasValue ||
            !processStartUtc.HasValue ||
            processStartUtc.Value <= _jpacArmedAtUtc.Value)
        {
            SetJpacRequirement(
                "restart|" + detected.ProcessId.ToString(CultureInfo.InvariantCulture),
                DccLocalization.T("nvidia_efficiency_restart_required"),
                detected,
                processStartUtc);
            return;
        }

        _lastJpacRequirement = null;

        _perGameProcessId = _settings.ActivationScope == DccActivationScope.PerGame
            ? detected.ProcessId
            : null;

        string detectedKey = detected.GameKey;
        string? governedKey = _governedGame?.GameKey;
        bool gameChanged = !string.Equals(detectedKey, governedKey, StringComparison.OrdinalIgnoreCase);
        _governedGame = detected;

        if (!_jpacApplied)
            throw new InvalidOperationException("NVIDIA Ultra Efficiency is not active.");

        if (gameChanged)
            RememberActivation(detected);

        StatusText.Text = DccLocalization.T("nvidia_efficiency_active");
    }

    private void SetJpacRequirement(
        string key,
        string status,
        DccDetectedGame detected,
        DateTimeOffset? processStartUtc,
        DccGameDisplayInfo? gameDisplay = null)
    {
        StatusText.Text = status;
        if (string.Equals(_lastJpacRequirement, key, StringComparison.Ordinal))
            return;

        DccGameDisplayInfo display = gameDisplay ?? _gameDisplayInfo;
        DccLog.Ui(
            "jpac.game.requirement",
            $"requirement={key.Split('|')[0]}; game={detected.Game.Name}; executable={detected.Executable}; " +
            $"pid={detected.ProcessId}; process_start_utc={processStartUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "unknown"}; " +
            $"jpac_armed_utc={_jpacArmedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "unknown"}; " +
            $"game_display={display.DisplayDeviceName}; game_display_primary={display.IsPrimary}; " +
            $"primary_display={_jpacPrimaryDisplay.DisplayDeviceName}; " +
            $"primary_refresh_hz={_jpacPrimaryDisplay.DisplayRefreshLogValue}; " +
            $"jpac_target_fps={EffectiveNvidiaTargetFps()}; driver_readback=unavailable");
        _lastJpacRequirement = key;
    }

    private void ForgetNvidiaGameTracking()
    {
        StopPerformanceRecording();
        _governedGame = null;
        _perGameProcessId = null;
        _gameDisplayInfo = DccGameDisplayInfo.Unknown;
        _lastGameDisplayProbeAt = null;
        _lastFreshGameAt = null;
    }

    private async Task EndGovernedGameAsync(string status)
    {
        StopPerformanceRecording();
        _governor.Stop();
        UpdateAutomaticTargetDisplay();
        if (_capApplied && _session is not null)
            await _session.ResetAsync();
        _capApplied = false;
        if (_jpacApplied && _session is not null)
        {
            _jpacVerification = await _session.SetNvidiaEfficiencyAsync(false, 0);
            if (!_jpacVerification.Hotfix7RestoreConfirmed)
                throw new InvalidOperationException("NVIDIA Ultra Efficiency could not be restored.");
        }
        _jpacApplied = false;
        _jpacArmedAtUtc = null;
        _lastJpacRequirement = null;
        _governedGame = null;
        _lastClockChangeAssessment = null;
        _gameDisplayInfo = DccGameDisplayInfo.Unknown;
        _lastGameDisplayProbeAt = null;
        _lastFreshGameAt = null;
        StatusText.Text = status;
    }

    private void TryRecordPerformance(
        DccDetectedGame? detected,
        NvidiaGpuInfo? telemetry,
        DccFpsSample fpsSample)
    {
        bool baseline = _session is null && _baselineSession is not null;
        if (!_settings.PerformanceLoggingEnabled || (_session is null && _baselineSession is null))
        {
            StopPerformanceRecording();
            return;
        }

        if (_gpu is null || detected is null || telemetry is null ||
            !detected.IsForeground || !detected.FpsFresh || !fpsSample.IsFresh)
        {
            if (baseline)
                StopPerformanceRecording();
            return;
        }

        DccControlMethod recordMethod = baseline ? DccControlMethod.Baseline : _settings.ControlMethod;
        bool recordFrameLimiter = !baseline && _settings.FrameLimiterEnabled;
        bool recordVsync = !baseline && _vsyncApplied;
        int recordTargetFps = baseline
            ? DccLearnedProfile.AutomaticTargetFps
            : EffectiveControlTargetFps();

        bool sameRun = _performanceRecorder is not null &&
                       _performanceRecorder.Metadata.ProcessId == detected.ProcessId &&
                       string.Equals(
                           _performanceRecorder.Metadata.GameKey,
                           detected.GameKey,
                           StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(
                           _performanceRecorder.Metadata.Executable,
                           detected.Executable,
                           StringComparison.OrdinalIgnoreCase) &&
                       _performanceRecorder.Metadata.ControlMethod == recordMethod &&
                       _performanceRecorder.Metadata.TargetFps == recordTargetFps &&
                       _performanceRecorder.Metadata.FrameLimiterEnabled == recordFrameLimiter &&
                       _performanceRecorder.Metadata.NvidiaVsyncEnabled == recordVsync &&
                       _performanceRecorder.Metadata.FrameLimitFps == _settings.FrameLimitFps;
        if (!sameRun)
        {
            StopPerformanceRecording();
            try
            {
                _performanceRecorder = DccPerformanceRecorder.Start(new DccPerformanceSessionMetadata(
                    AppVersion: CurrentAppVersion(),
                    GameName: detected.Game.Name,
                    Executable: detected.Executable,
                    ProcessId: detected.ProcessId,
                    NvidiaProfileName: detected.Game.NvidiaProfileName,
                    GpuName: _gpu.Name,
                    GpuFingerprint: DccDiagnosticReport.GpuFingerprint(_gpu.Uuid),
                    DriverVersion: _gpu.DriverVersion,
                    ControlMethod: recordMethod,
                    TargetFps: recordTargetFps,
                    FrameLimiterEnabled: recordFrameLimiter,
                    FrameLimitFps: _settings.FrameLimitFps,
                    ActivationScope: _settings.ActivationScope,
                    StartedAtUtc: DateTimeOffset.UtcNow,
                    NvidiaVsyncEnabled: recordVsync,
                    GameClientWidth: _gameDisplayInfo.GameClientWidth,
                    GameClientHeight: _gameDisplayInfo.GameClientHeight,
                    DisplayWidth: _gameDisplayInfo.DisplayWidth,
                    DisplayHeight: _gameDisplayInfo.DisplayHeight,
                    GameKey: detected.GameKey));
                DccLog.Ui("performance.recording.started",
                    $"game={detected.Game.Name}; executable={detected.Executable}; method={recordMethod}; " +
                    $"control_target_fps={recordTargetFps}; frame_limiter={recordFrameLimiter}; " +
                    $"frame_limit_fps={_settings.FrameLimitFps}; vsync={recordVsync}; fps_pid={detected.FpsProcessId}; " +
                    $"fps_executable={detected.PresentationExecutable}; " +
                    $"game_client={_gameDisplayInfo.GameClientLogValue}; display={_gameDisplayInfo.DisplayLogValue}; " +
                    $"display_refresh_hz={_gameDisplayInfo.DisplayRefreshLogValue}");
            }
            catch (Exception ex)
            {
                _performanceRecorder = null;
                DccLog.Ui("performance.recording.start_failed", exception: ex);
                return;
            }
        }

        string verification = baseline
            ? "baseline;dcc-control-off;graphics-clock-telemetry-readback"
            : _settings.ControlMethod == DccControlMethod.NvidiaEfficiency
            ? _jpacVerification.LogValue
            : _capApplied
                ? "dcc-active;clock-cap-requested" +
                  (_governor.DetectedFpsLimit is double detectedFpsLimit
                      ? $";detected-fps-lock={detectedFpsLimit.ToString("F1", CultureInfo.InvariantCulture)}"
                      : string.Empty) +
                  (_governor.ProfilePersistenceReady
                      ? ";profile-learning=ready"
                      : ";profile-learning=warming") +
                   (_governor.PowerValleyFloorMHz is int powerValleyFloor
                       ? $";power-valley-floor={powerValleyFloor.ToString(CultureInfo.InvariantCulture)}"
                       : string.Empty) +
                   $";clock-change-sensitivity={_governor.ClockChangeSensitivityScore}" +
                   (_governor.IsClockChangeSensitive ? ";clock-change-sensitive" : string.Empty) +
                   ";graphics-clock-telemetry-readback"
                : "dcc-active;warmup-no-cap" +
                  (_governor.ProfilePersistenceReady
                      ? ";profile-learning=ready"
                      : ";profile-learning=warming") +
                  ";graphics-clock-telemetry-readback";
        if (detected.PresentationProcessId > 0)
            verification += ";fps-source=nv-remix-bridge";
        string clockChangeAssessment = _lastClockChangeAssessment is null
            ? "none"
            : $"{_lastClockChangeAssessment.Reason};" +
              $"from={_lastClockChangeAssessment.FromClockMHz};" +
              $"to={_lastClockChangeAssessment.ToClockMHz};" +
              $"reference_p95_ms={_lastClockChangeAssessment.ReferenceP95FrameTimeMilliseconds.ToString("F3", CultureInfo.InvariantCulture)};" +
              $"observed_p95_ms={_lastClockChangeAssessment.ObservedP95FrameTimeMilliseconds.ToString("F3", CultureInfo.InvariantCulture)};" +
              $"reference_p99_ms={_lastClockChangeAssessment.ReferenceP99FrameTimeMilliseconds.ToString("F3", CultureInfo.InvariantCulture)};" +
              $"observed_p99_ms={_lastClockChangeAssessment.ObservedP99FrameTimeMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}";
        try
        {
            _performanceRecorder!.Append(new DccPerformanceSample(
                TimestampUtc: DateTimeOffset.UtcNow,
                FramesPerSecond: fpsSample.FramesPerSecond,
                P95FrameTimeMilliseconds: fpsSample.P95FrameTimeMilliseconds,
                FrameCount: fpsSample.FrameCount,
                FpsSampleAgeMilliseconds: fpsSample.SampleAge.TotalMilliseconds,
                PowerWatts: telemetry.PowerWatts,
                GpuUtilizationPercent: telemetry.UtilizationPercent,
                GraphicsClockMHz: telemetry.GraphicsClockMHz,
                MemoryClockMHz: telemetry.MemoryClockMHz,
                AppliedClockCapMHz: recordMethod == DccControlMethod.DccGovernor
                    ? _governor.CurrentCapMHz
                    : null,
                PState: telemetry.PState,
                ControlVerification: verification,
                P99FrameTimeMilliseconds: fpsSample.P99FrameTimeMilliseconds,
                CoreVoltageMillivolts: telemetry.CoreVoltageMillivolts,
                ClockChangeSensitivityScore: recordMethod == DccControlMethod.DccGovernor
                    ? _governor.ClockChangeSensitivityScore
                    : 0,
                ClockChangeSensitive: recordMethod == DccControlMethod.DccGovernor &&
                                      _governor.IsClockChangeSensitive,
                ClockChangeAssessment: recordMethod == DccControlMethod.DccGovernor
                    ? clockChangeAssessment
                    : "none"));
        }
        catch (Exception ex)
        {
            DccLog.Ui("performance.recording.sample_failed", exception: ex);
            StopPerformanceRecording();
        }
    }

    private void StopPerformanceRecording()
    {
        if (_performanceRecorder is null)
            return;
        try
        {
            string executable = _performanceRecorder.Metadata.Executable;
            _performanceRecorder.Dispose();
            DccLog.Ui("performance.recording.completed", $"executable={executable}");
        }
        catch (Exception ex)
        {
            DccLog.Ui("performance.recording.stop_failed", exception: ex);
        }
        finally
        {
            _performanceRecorder = null;
        }
    }

    private async void ActiveToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        if (ActiveToggle.IsChecked == true && _session is null)
            await ActivateAsync();
        else if (ActiveToggle.IsChecked != true && _session is not null)
            await DeactivateAsync();
    }

    private async Task ActivateAsync()
    {
        if (_gpu is null || _clockCatalog is null)
            return;
        if (DccActiveSessionStore.TryRead() is not null ||
            DccFrameLimiterRecoveryStore.TryRead() is not null ||
            DccVsyncRecoveryStore.TryRead() is not null ||
            DccJpacRecoveryStore.TryRead() is not null)
        {
            StatusText.Text = DccLocalization.T("recovery_first");
            return;
        }

        CommitTargetFps();
        CommitFrameLimitFps();
        DccLog.Ui("activation.requested",
            $"gpu_index={_gpu.Index}; method={_settings.ControlMethod}; control_target_fps={EffectiveControlTargetFps()}; " +
            $"frame_limiter={_settings.FrameLimiterEnabled}; frame_limit_fps={_settings.FrameLimitFps}; " +
            $"vsync={_settings.NvidiaVsyncEnabled}; activation_scope={_settings.ActivationScope}");
        SetBusy(true, AutoStartService.IsCurrentProcessElevated()
            ? DccLocalization.T("helper_starting")
            : DccLocalization.T("uac"));
        try
        {
            StopPerformanceRecording();
            _perGameProcessId = null;
            _jpacArmedAtUtc = null;
            _lastJpacRequirement = null;
            if (_baselineSession is not null)
            {
                _session = _baselineSession;
                _baselineSession = null;
            }
            else
            {
                _session = await ElevatedClockCapSession.StartAsync(_gpu);
            }
            if (_settings.FrameLimiterEnabled)
            {
                StatusText.Text = DccLocalization.T("frame_limiter_applying");
                await _session.ApplyGlobalFrameLimitAsync(_settings.FrameLimitFps);
                _frameLimiterApplied = true;
            }
            if (_settings.NvidiaVsyncEnabled)
            {
                StatusText.Text = DccLocalization.T("vsync_applying");
                await _session.ApplyVsyncAsync();
                _vsyncApplied = true;
            }
            StatusText.Text = "Reading NVIDIA driver profiles …";
            _driverApplications = await _session.QueryDriverApplicationsAsync();
            RebuildGameDetector();
            if (_settings.ControlMethod == DccControlMethod.NvidiaEfficiency)
            {
                _jpacPrimaryDisplay = DccGameDisplayProbe.CapturePrimary();
                if (!DccGameDisplayProbe.CanResolveNvidiaEfficiencyTarget(
                        _jpacPrimaryDisplay,
                        _settings.NvidiaTargetFpsManual))
                {
                    throw new InvalidOperationException(
                        DccLocalization.T("nvidia_efficiency_primary_unavailable"));
                }

                int effectiveTargetFps = EffectiveNvidiaTargetFps();
                StatusText.Text = DccLocalization.T("nvidia_efficiency_waiting_restart");
                _jpacArmedAtUtc = DateTimeOffset.UtcNow;
                _jpacVerification = await _session.SetNvidiaEfficiencyAsync(true, effectiveTargetFps);
                if (!_jpacVerification.Hotfix7EnableConfirmed)
                    throw new InvalidOperationException("NVIDIA Ultra Efficiency could not be enabled.");
                _jpacApplied = true;
                DccLog.Ui(
                    "jpac.armed",
                    $"jpac_target_mode={(_settings.NvidiaTargetFpsManual ? "manual" : "auto")}; " +
                    $"jpac_target_fps={effectiveTargetFps}; primary_display={_jpacPrimaryDisplay.DisplayDeviceName}; " +
                    $"primary_refresh_hz={_jpacPrimaryDisplay.DisplayRefreshLogValue}; " +
                    $"armed_utc={_jpacArmedAtUtc.Value.ToString("O", CultureInfo.InvariantCulture)}; " +
                    "driver_readback=unavailable; full_game_restart_required=true");
            }
            _settings = _settings with { GovernorEnabled = true };
            DccUserSettingsStore.Save(_settings);
            SetActiveVisuals();
            if (_jpacApplied)
                StatusText.Text = DccLocalization.T("nvidia_efficiency_waiting_restart");
            DccLog.Ui("activation.complete",
                $"control_target_fps={EffectiveControlTargetFps()}; frame_limiter={_settings.FrameLimiterEnabled}; " +
                $"frame_limit_fps={_settings.FrameLimitFps}; vsync={_settings.NvidiaVsyncEnabled}; " +
                $"activation_scope={_settings.ActivationScope}; driver_applications={_driverApplications.Count}; " +
                $"jpac_active={_jpacApplied}; driver_readback={(_jpacApplied ? "unavailable" : "not-requested")}");
        }
        catch (OperationCanceledException)
        {
            DccLog.Ui("activation.cancelled");
            SetInactiveVisuals(DccLocalization.T("uac_cancelled"));
        }
        catch (Exception ex)
        {
            ShowError(DccLocalization.T("error_activation"), ex);
            await DisposeSessionAsync();
            SetInactiveVisuals(DccLocalization.T("activation_failed"));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DeactivateAsync(string? finalStatus = null)
    {
        DccLog.Ui("deactivation.requested");
        SetBusy(true, DccLocalization.T("resetting"));
        try
        {
            await EndGovernedGameAsync(DccLocalization.T("driver_control"));
            if (_vsyncApplied && _session is not null)
                await _session.RemoveVsyncAsync();
            _vsyncApplied = false;
            if (_frameLimiterApplied && _session is not null)
                await _session.RemoveFrameLimitAsync();
            _frameLimiterApplied = false;
            _perGameProcessId = null;
            if (_settings.PerformanceLoggingEnabled && _session is not null)
            {
                _baselineSession = _session;
                _session = null;
            }
            else
            {
                await DisposeSessionAsync();
            }
            _rememberedProcessIds.Clear();
            _settings = _settings with { GovernorEnabled = false };
            DccUserSettingsStore.Save(_settings);
            SetInactiveVisuals(finalStatus ?? DccLocalization.T("dcc_disabled"));
            DccLog.Ui("deactivation.complete");
        }
        catch (Exception ex)
        {
            ShowError(DccLocalization.T("error_reset"), ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gpu is null || _busy)
            return;
        SetBusy(true, DccLocalization.T("emergency_reset"));
        DccLog.Ui("emergency_reset.requested", $"session_active={_session is not null}");
        try
        {
            await DisposeBaselineSessionAsync();
            if (_session is not null)
            {
                await EndGovernedGameAsync(DccLocalization.T("resetting"));
                await DisposeSessionAsync();
            }
            else
            {
                await ElevatedClockCapSession.RunEmergencyResetAsync(_gpu);
            }
            DccActiveSessionStore.Clear();
            DccFrameLimiterRecoveryStore.Clear();
            DccVsyncRecoveryStore.Clear();
            DccJpacRecoveryStore.Clear();
            _recoveryRequired = false;
            _settings = _settings with { GovernorEnabled = false };
            DccUserSettingsStore.Save(_settings);
            SetInactiveVisuals(DccLocalization.T("cap_removed"));
            DccLog.Ui("emergency_reset.complete");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = DccLocalization.T("uac_cancelled");
        }
        catch (Exception ex)
        {
            ShowError(DccLocalization.T("error_emergency_reset"), ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void TargetFpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TargetFpsBox is null)
            return;

        int sliderValue = (int)Math.Round(e.NewValue);
        TargetFpsBox.Text = sliderValue < DccFpsRange.NvidiaEfficiencyFloor
            ? "AUTO"
            : sliderValue.ToString(CultureInfo.InvariantCulture);
        if (_loading || !IsLoaded)
            return;

        _targetFpsCommitTimer.Stop();
        _targetFpsCommitTimer.Start();
    }

    private void TargetFpsBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        CommitTargetFps();

    private void TargetFpsBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        CommitTargetFps();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void CommitTargetFps()
    {
        if (_loading)
            return;
        _targetFpsCommitTimer.Stop();
        string text = TargetFpsBox.Text.Trim();
        if (string.Equals(text, "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            if (_settings.NvidiaTargetFpsManual)
            {
                _settings = _settings with { NvidiaTargetFpsManual = false };
                DccUserSettingsStore.Save(_settings);
                if (_settings.ControlMethod == DccControlMethod.NvidiaEfficiency)
                    _ = RestartControlForValueChangeAsync(EffectiveNvidiaTargetFps());
            }

            _loading = true;
            ConfigureTargetSlider();
            TargetFpsSlider.Value = NvidiaAutomaticSliderValue;
            TargetFpsBox.Text = "AUTO";
            _loading = false;
            UpdateTrayUi();
            return;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int target))
            target = _settings.NvidiaTargetFps;
        target = Math.Clamp(
            target,
            DccFpsRange.NvidiaEfficiencyFloor,
            DccFpsRange.NvidiaEfficiencyMaximum);

        if (target != _settings.NvidiaTargetFps || !_settings.NvidiaTargetFpsManual)
        {
            _settings = _settings with
            {
                TargetFps = target,
                NvidiaTargetFps = target,
                NvidiaTargetFpsManual = true
            };
            DccUserSettingsStore.Save(_settings);
            if (_settings.ControlMethod == DccControlMethod.NvidiaEfficiency)
                _ = RestartControlForValueChangeAsync(target);
        }

        _loading = true;
        ConfigureTargetSlider();
        TargetFpsSlider.Value = target;
        TargetFpsBox.Text = target.ToString(CultureInfo.InvariantCulture);
        _loading = false;
        UpdateTrayUi();
    }

    private void FrameLimiterFpsBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        CommitFrameLimitFps();

    private void FrameLimiterFpsBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        CommitFrameLimitFps();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void CommitFrameLimitFps()
    {
        if (_loading)
            return;
        if (!int.TryParse(
                FrameLimiterFpsBox.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int target))
        {
            target = _settings.FrameLimitFps;
        }
        target = Math.Clamp(target, DccFpsRange.Minimum, DccFpsRange.Maximum);

        if (target != _settings.FrameLimitFps)
        {
            _settings = _settings with { FrameLimitFps = target };
            DccUserSettingsStore.Save(_settings);
            if (_settings.FrameLimiterEnabled)
                _ = RestartControlForValueChangeAsync(target);
        }

        FrameLimiterFpsBox.Text = target.ToString(CultureInfo.InvariantCulture);
        UpdateTrayUi();
    }

    private void MethodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MethodComboBox.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse(tag, ignoreCase: false, out DccControlMethod method))
        {
            return;
        }

        if (_session is not null)
        {
            _loading = true;
            MethodComboBox.SelectedIndex = _settings.ControlMethod == DccControlMethod.NvidiaEfficiency ? 1 : 0;
            _loading = false;
            return;
        }

        _settings = (_settings with { ControlMethod = method }).WithValidatedValues();
        DccUserSettingsStore.Save(_settings);
        _loading = true;
        ConfigureTargetSlider();
        TargetFpsSlider.Value = NvidiaTargetSliderValue();
        TargetFpsBox.Text = _settings.NvidiaTargetFpsManual
            ? _settings.NvidiaTargetFps.ToString(CultureInfo.InvariantCulture)
            : "AUTO";
        UpdateTargetControls();
        _loading = false;
        ApplyLocalization();
        StatusText.Text = method == DccControlMethod.NvidiaEfficiency
            ? DccLocalization.T("method_nvidia_selected")
            : DccLocalization.T("method_dcc_selected");
    }

    private void ActivationScopeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ActivationScopeComboBox.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse(tag, ignoreCase: false, out DccActivationScope scope))
        {
            return;
        }

        if (_session is not null)
        {
            _loading = true;
            ActivationScopeComboBox.SelectedIndex =
                _settings.ActivationScope == DccActivationScope.PerGame ? 1 : 0;
            _loading = false;
            return;
        }

        _settings = (_settings with { ActivationScope = scope }).WithValidatedValues();
        DccUserSettingsStore.Save(_settings);
        ApplyLocalization();
        StatusText.Text = scope == DccActivationScope.PerGame
            ? DccLocalization.T("activation_per_game_selected")
            : DccLocalization.T("activation_persistent_selected");
    }

    private void FrameLimiterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        if (_session is not null)
        {
            _loading = true;
            FrameLimiterCheckBox.IsChecked = _settings.FrameLimiterEnabled;
            _loading = false;
            return;
        }

        bool enabled = FrameLimiterCheckBox.IsChecked == true;
        _settings = (_settings with { FrameLimiterEnabled = enabled }).WithValidatedValues();
        DccUserSettingsStore.Save(_settings);
        UpdateTargetControls();
        DccLog.Ui("frame_limiter.option_changed", $"enabled={enabled}");
        StatusText.Text = enabled
            ? DccLocalization.T("frame_limiter_selected_on")
            : DccLocalization.T("frame_limiter_selected_off");
    }

    private void VsyncCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        if (_session is not null)
        {
            _loading = true;
            VsyncCheckBox.IsChecked = _settings.NvidiaVsyncEnabled;
            _loading = false;
            return;
        }

        bool enabled = VsyncCheckBox.IsChecked == true;
        _settings = (_settings with { NvidiaVsyncEnabled = enabled }).WithValidatedValues();
        DccUserSettingsStore.Save(_settings);
        DccLog.Ui("vsync.option_changed", $"enabled={enabled}");
        StatusText.Text = enabled
            ? DccLocalization.T("vsync_selected_on")
            : DccLocalization.T("vsync_selected_off");
    }

    private void ConfigureTargetSlider()
    {
        TargetFpsSlider.Minimum = NvidiaAutomaticSliderValue;
        TargetFpsSlider.Maximum = DccFpsRange.NvidiaEfficiencyMaximum;
    }

    private int NvidiaTargetSliderValue() =>
        _settings.NvidiaTargetFpsManual
            ? _settings.NvidiaTargetFps
            : NvidiaAutomaticSliderValue;

    private void UpdateTargetControls()
    {
        bool nvidiaMethod = _settings.ControlMethod == DccControlMethod.NvidiaEfficiency;
        TargetControlRow.Visibility = nvidiaMethod ? Visibility.Visible : Visibility.Collapsed;
        TargetFpsSlider.Visibility = nvidiaMethod ? Visibility.Visible : Visibility.Collapsed;
        TargetFpsBox.Visibility = nvidiaMethod ? Visibility.Visible : Visibility.Collapsed;
        AutomaticTargetPanel.Visibility = Visibility.Collapsed;
        FrameLimiterFpsBox.Visibility = _settings.FrameLimiterEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        FrameLimiterFpsBox.Text = _settings.FrameLimitFps.ToString(CultureInfo.InvariantCulture);
    }

    private void UpdateAutomaticTargetDisplay()
    {
        if (AutomaticTargetText is null || AutomaticTargetPanel is null)
            return;

        string text = _governor.DetectedFpsLimit is double detectedFpsLimit
            ? DccLocalization.T("automatic_fps_lock", detectedFpsLimit)
            : DccLocalization.T("automatic_gpu_load");
        bool changed = !string.Equals(AutomaticTargetText.Text, text, StringComparison.Ordinal);
        AutomaticTargetText.Text = text;
        AutomaticTargetPanel.ToolTip = DccLocalization.T("automatic_gpu_load_tip");
        if (changed)
            UpdateTrayUi();
    }

    private int EffectiveControlTargetFps() =>
        _settings.ControlMethod == DccControlMethod.NvidiaEfficiency
            ? EffectiveNvidiaTargetFps()
            : DccLearnedProfile.AutomaticTargetFps;

    private int EffectiveNvidiaTargetFps() =>
        DccFpsRange.ResolveNvidiaEfficiencyTarget(
            _settings.NvidiaTargetFps,
            _jpacPrimaryDisplay.DisplayRefreshHz,
            _settings.NvidiaTargetFpsManual);

    private async Task RestartControlForValueChangeAsync(int targetFps)
    {
        await _controlGate.WaitAsync();
        try
        {
            if (_settings.ControlMethod == DccControlMethod.NvidiaEfficiency &&
                _jpacApplied &&
                _session is not null)
            {
                int effectiveTargetFps = EffectiveNvidiaTargetFps();
                if (_jpacVerification.RequestedTargetFps != effectiveTargetFps)
                {
                    _jpacArmedAtUtc = DateTimeOffset.UtcNow;
                    _jpacVerification = await _session.SetNvidiaEfficiencyAsync(true, effectiveTargetFps);
                    if (!_jpacVerification.Hotfix7EnableConfirmed)
                        throw new InvalidOperationException("The NVIDIA Ultra Efficiency target could not be changed.");
                    _governedGame = null;
                    _perGameProcessId = null;
                    _lastJpacRequirement = null;
                    DccLog.Ui(
                        "jpac.target_changed",
                        $"slider_fps={_settings.NvidiaTargetFps}; primary_display={_jpacPrimaryDisplay.DisplayDeviceName}; " +
                        $"primary_refresh_hz={_jpacPrimaryDisplay.DisplayRefreshLogValue}; " +
                        $"jpac_target_fps={effectiveTargetFps}; full_game_restart_required=true");
                }

                StatusText.Text = DccLocalization.T("nvidia_efficiency_waiting_restart");
            }
            else if (_governedGame is not null)
            {
                await EndGovernedGameAsync(DccLocalization.T("new_target", targetFps));
            }
        }
        finally
        {
            _controlGate.Release();
        }
    }

    private void GamesProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new GamesProfilesWindow(this);
        window.ShowDialog();
    }

    internal IReadOnlyCollection<DccGameHistoryEntry> BrowserHistory => _history.Entries;
    internal IReadOnlyCollection<DccLearnedProfile> BrowserProfiles => _gpu is null
        ? []
        : _profiles.Profiles
            .Where(profile => string.Equals(
                profile.GpuUuid,
                _gpu.Uuid,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    internal string BrowserLanguage => _settings.Language;

    internal int ResetLearnedProfileFromBrowser(string gameKey, Window owner)
    {
        if (_gpu is null || string.IsNullOrWhiteSpace(gameKey))
            return 0;

        string normalizedGameKey = DccGameCatalog.NormalizeExecutable(gameKey);
        if (_governedGame is not null &&
            string.Equals(
                _governedGame.GameKey,
                normalizedGameKey,
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                owner,
                DccLocalization.T("history_reset_profile_active"),
                DccLocalization.T("history_title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return 0;
        }

        try
        {
            DccProfileResetResult result = _profiles.RemoveForGame(_gpu.Uuid, normalizedGameKey);
            if (result.Removed <= 0)
                return 0;

            StatusText.Text = DccLocalization.T("history_reset_profile_done");
            DccLog.Ui(
                "profile.reset",
                $"game_key={normalizedGameKey}; removed={result.Removed}; backup={Path.GetFileName(result.BackupPath)}");
            return result.Removed;
        }
        catch (Exception ex)
        {
            DccLog.Ui("profile.reset.failed", $"game_key={normalizedGameKey}", ex);
            ShowError(DccLocalization.T("error_reset_profile"), ex, owner);
            return 0;
        }
    }

    internal void SetLanguageFromBrowser(string language)
    {
        DccUserSettings updated = (_settings with { Language = language }).WithValidatedValues();
        if (string.Equals(updated.Language, _settings.Language, StringComparison.Ordinal))
            return;

        _settings = updated;
        DccUserSettingsStore.Save(_settings);
        DccLocalization.SetLanguage(_settings.Language);
        ApplyLocalization();
        StatusText.Text = DccLocalization.T("language_changed");
        DccLog.Ui("language.changed", $"language={_settings.Language}");
    }

    internal void ExportProfilesFromBrowser(Window owner)
    {
        var dialog = new SaveFileDialog
        {
            Title = DccLocalization.T("dialog_export"),
            Filter = DccLocalization.T("dialog_profile_filter"),
            FileName = $"DCC-Profiles-{DateTime.Now:yyyy-MM-dd}.json"
        };
        if (dialog.ShowDialog(owner) != true)
            return;
        try
        {
            _profiles.Export(dialog.FileName);
            StatusText.Text = DccLocalization.T("profiles_exported", _profiles.Profiles.Count);
        }
        catch (Exception ex)
        {
            ShowError(DccLocalization.T("error_export"), ex, owner);
        }
    }

    internal void OpenDataFolderFromBrowser(Window owner)
    {
        try
        {
            Directory.CreateDirectory(DccPaths.DataDirectory);
            Process.Start(new ProcessStartInfo(DccPaths.DataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError(DccLocalization.T("error_folder"), ex, owner);
        }
    }

    internal void ExportDiagnosticsFromBrowser(Window owner)
    {
        var dialog = new SaveFileDialog
        {
            Title = DccLocalization.T("dialog_export_diagnostics"),
            Filter = DccLocalization.T("dialog_diagnostics_filter"),
            FileName = $"DCC-Diagnostics-{DateTime.Now:yyyy-MM-dd-HHmm}.zip"
        };
        if (dialog.ShowDialog(owner) != true)
            return;

        try
        {
            var snapshot = new DccDiagnosticSnapshot(
                AppVersion: CurrentAppVersion(),
                OsDescription: RuntimeInformation.OSDescription,
                OsArchitecture: RuntimeInformation.OSArchitecture.ToString(),
                ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
                GpuName: _gpu?.Name ?? "unavailable",
                GpuIndex: _gpu?.Index,
                GpuUuid: _gpu?.Uuid ?? string.Empty,
                DriverVersion: _gpu?.DriverVersion ?? "unavailable",
                PState: _gpu?.PState ?? "unavailable",
                PciBusId: _gpu?.PciBusId ?? "unavailable",
                MinimumClockMHz: _clockCatalog?.MinimumMHz,
                MaximumClockMHz: _clockCatalog?.MaximumMHz,
                ClockBinCount: _clockCatalog?.Count,
                RecoveryMarkerPresent: DccActiveSessionStore.TryRead() is not null ||
                                        DccFrameLimiterRecoveryStore.TryRead() is not null ||
                                        DccVsyncRecoveryStore.TryRead() is not null ||
                                        DccJpacRecoveryStore.TryRead() is not null,
                DccSessionActive: _session is not null,
                TargetFps: EffectiveControlTargetFps(),
                Language: _settings.Language,
                UiScalePercent: _settings.UiScalePercent,
                AutostartEnabled: AutoStartService.IsEnabled(),
                ProfileCount: _profiles.Profiles.Count,
                GameCount: _history.Entries.Count,
                FpsCollectorAvailable: _lastFpsSample.CollectorAvailable,
                FpsCollectorSource: _lastFpsSample.Source,
                FpsCollectorFrameCount: _lastFpsSample.FrameCount,
                FpsCollectorError: _lastFpsSample.Error,
                ControlMethod: _settings.ControlMethod.ToString(),
                FrameLimiterEnabled: _settings.FrameLimiterEnabled,
                ActivationScope: _settings.ActivationScope.ToString(),
                PerformanceLoggingEnabled: _settings.PerformanceLoggingEnabled,
                FrameLimitFps: _settings.FrameLimitFps,
                NvidiaVsyncEnabled: _settings.NvidiaVsyncEnabled);

            DccLog.Ui("diagnostics.export.begin");
            DccDiagnosticReport.Export(dialog.FileName, snapshot);
            DccLog.Ui("diagnostics.export.complete");
            StatusText.Text = DccLocalization.T("diagnostics_exported");
        }
        catch (Exception ex)
        {
            DccLog.Ui("diagnostics.export.failed", exception: ex);
            ShowError(DccLocalization.T("error_diagnostics"), ex, owner);
        }
    }

    internal void ExportPerformanceLogsFromBrowser(Window owner)
    {
        var dialog = new SaveFileDialog
        {
            Title = DccLocalization.T("dialog_export_performance"),
            Filter = DccLocalization.T("dialog_performance_filter"),
            FileName = $"DCC-Performance-{DateTime.Now:yyyy-MM-dd-HHmm}.zip"
        };
        if (dialog.ShowDialog(owner) != true)
            return;

        try
        {
            _performanceRecorder?.Flush();
            DccPerformanceArchive.Export(dialog.FileName);
            StatusText.Text = DccLocalization.T("performance_exported");
            DccLog.Ui("performance.export.complete");
        }
        catch (Exception ex)
        {
            DccLog.Ui("performance.export.failed", exception: ex);
            ShowError(DccLocalization.T("error_performance"), ex, owner);
        }
    }

    internal bool BrowserPerformanceLoggingEnabled => _settings.PerformanceLoggingEnabled;

    internal async Task SetPerformanceLoggingFromBrowserAsync(bool enabled, Window owner)
    {
        if (enabled == _settings.PerformanceLoggingEnabled)
            return;

        SetBusy(true, enabled ? "Starting baseline performance logging …" : "Stopping performance logging …");
        try
        {
            if (enabled)
                await EnsureBaselineSessionAsync();

            _settings = (_settings with { PerformanceLoggingEnabled = enabled }).WithValidatedValues();
            DccUserSettingsStore.Save(_settings);
            if (!enabled)
            {
                StopPerformanceRecording();
                await DisposeBaselineSessionAsync();
            }
            DccLog.Ui("performance.recording.option_changed", $"enabled={enabled}; baseline_monitor={_baselineSession is not null}");
            StatusText.Text = enabled
                ? _session is null
                    ? "Performance logging on · DCC control off"
                    : "Performance logging on"
                : "Performance logging off";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Performance logging was not enabled";
        }
        catch (Exception ex)
        {
            await DisposeBaselineSessionAsync();
            ShowError("Performance logging could not start", ex, owner);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void AutoStartToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _busy)
            return;

        bool requested = AutoStartToggle.IsChecked == true;
        SetBusy(true, DccLocalization.T("autostart_configuring"));
        try
        {
            await AutoStartService.SetEnabledAsync(requested);
            AutoStartToggle.IsChecked = AutoStartService.IsEnabled();
            StatusText.Text = requested
                ? DccLocalization.T("autostart_on")
                : DccLocalization.T("autostart_off");
        }
        catch (OperationCanceledException)
        {
            AutoStartToggle.IsChecked = AutoStartService.IsEnabled();
            StatusText.Text = DccLocalization.T("uac_cancelled");
        }
        catch (Exception ex)
        {
            AutoStartToggle.IsChecked = AutoStartService.IsEnabled();
            ShowError(DccLocalization.T("error_autostart"), ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void StartMinimizedToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        bool previous = _settings.StartMinimizedToTray;
        bool enabled = StartMinimizedToggle.IsChecked == true;
        try
        {
            _settings = (_settings with { StartMinimizedToTray = enabled }).WithValidatedValues();
            DccUserSettingsStore.Save(_settings);
            StatusText.Text = enabled
                ? DccLocalization.T("start_minimized_on")
                : DccLocalization.T("start_minimized_off");
        }
        catch (Exception ex)
        {
            _settings = _settings with { StartMinimizedToTray = previous };
            StartMinimizedToggle.IsChecked = previous;
            ShowError(DccLocalization.T("error_start_minimized"), ex);
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        _controlTimer.Stop();
        _targetFpsCommitTimer.Stop();
        if (_closingAfterReset || (_session is null && _baselineSession is null))
            return;

        e.Cancel = true;
        _controlTimer.Stop();
        SetBusy(true, DccLocalization.T("reset_on_exit"));
        if (_session is not null)
            await EndGovernedGameAsync(DccLocalization.T("resetting"));
        await DisposeSessionAsync();
        await DisposeBaselineSessionAsync();
        DccUserSettingsStore.Save(_settings);
        _closingAfterReset = true;
        Close();
    }

    private async Task DisposeSessionAsync()
    {
        if (_session is null)
            return;
        ElevatedClockCapSession session = _session;
        _session = null;
        try
        {
            await session.DisposeAsync();
        }
        finally
        {
            _vsyncApplied = false;
            _frameLimiterApplied = false;
            _jpacApplied = false;
            _jpacArmedAtUtc = null;
            _lastJpacRequirement = null;
        }
    }

    private async Task EnsureBaselineSessionAsync()
    {
        if (_session is not null || _baselineSession is not null)
            return;
        if (_gpu is null)
            throw new InvalidOperationException("No NVIDIA GPU is available for baseline logging.");

        DccLog.Ui("performance.baseline.session.begin", $"gpu_index={_gpu.Index}");
        _baselineSession = await ElevatedClockCapSession.StartAsync(_gpu);
        try
        {
            _driverApplications = await _baselineSession.QueryDriverApplicationsAsync();
            RebuildGameDetector();
            DccLog.Ui("performance.baseline.session.ready", $"driver_applications={_driverApplications.Count}");
        }
        catch
        {
            await DisposeBaselineSessionAsync();
            throw;
        }
    }

    private async Task DisposeBaselineSessionAsync()
    {
        if (_baselineSession is null)
            return;
        ElevatedClockCapSession session = _baselineSession;
        _baselineSession = null;
        await session.DisposeAsync();
    }

    private void SetActiveVisuals()
    {
        _recoveryRequired = false;
        ActiveToggle.IsChecked = true;
        ActiveHintText.Text = _settings.ControlMethod == DccControlMethod.NvidiaEfficiency
            ? DccLocalization.T("lamp_active")
            : DccLocalization.T("dcc_waiting");
        StatusText.Foreground = (Brush)FindResource("TextSecondary");
        StatusText.Text = DccLocalization.T("waiting_game");
        UpdateActivityIndicator();
        UpdateTrayUi();
    }

    private void SetInactiveVisuals(string status)
    {
        ActiveToggle.IsChecked = false;
        ActiveHintText.Text = DccLocalization.T("dcc_off");
        StatusText.Foreground = (Brush)FindResource("TextSecondary");
        StatusText.Text = status;
        UpdateActivityIndicator();
        UpdateTrayUi();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        ActiveToggle.IsEnabled = !busy && _gpu is not null && _clockCatalog is not null;
        ResetButton.IsEnabled = !busy && _gpu is not null;
        bool nvidiaTargetEnabled = !busy &&
            _settings.ControlMethod == DccControlMethod.NvidiaEfficiency;
        TargetFpsSlider.IsEnabled = nvidiaTargetEnabled;
        TargetFpsBox.IsEnabled = nvidiaTargetEnabled;
        FrameLimiterFpsBox.IsEnabled = !busy && _session is null && _settings.FrameLimiterEnabled;
        MethodComboBox.IsEnabled = !busy && _session is null;
        ActivationScopeComboBox.IsEnabled = !busy && _session is null;
        FrameLimiterCheckBox.IsEnabled = !busy && _session is null;
        VsyncCheckBox.IsEnabled = !busy && _session is null;
        MenuButton.IsEnabled = !busy;
        AutoStartToggle.IsEnabled = !busy;
        StartMinimizedToggle.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(status))
            StatusText.Text = status;
    }

    private void ShowError(string title, Exception ex, Window? owner = null)
    {
        DccLog.Ui("ui.error", $"title={title}", ex);
        StatusText.Text = ex.Message;
        MessageBox.Show(owner ?? this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string ShortGpuName(string name) =>
        name.Replace("NVIDIA GeForce ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("NVIDIA ", string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool IsProcessRunning(int processId)
    {
        if (processId <= 0)
            return false;

        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            // Access restrictions do not prove that the confirmed game exited.
            return true;
        }
    }

    private static DateTimeOffset? TryGetProcessStartTimeUtc(int processId)
    {
        if (processId <= 0)
            return null;

        try
        {
            using Process process = Process.GetProcessById(processId);
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            // A start time that cannot be verified must not authorize JPAC for
            // a process which may have been running before the mode was armed.
            return null;
        }
    }

    private static string CurrentAppVersion() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    private static string ShortAppVersion()
    {
        string version = CurrentAppVersion();
        int metadataIndex = version.IndexOf('+');
        if (metadataIndex >= 0)
            version = version[..metadataIndex];
        const string productPrefix = "0.1.0-";
        return version.StartsWith(productPrefix, StringComparison.OrdinalIgnoreCase)
            ? version[productPrefix.Length..]
            : version;
    }

    protected override void OnClosed(EventArgs e)
    {
        StopPerformanceRecording();
        DisposeTrayIcon();
        base.OnClosed(e);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void ScaleButton_Click(object sender, RoutedEventArgs e) =>
        SetUiScale(DccUiScale.Next(_settings.UiScalePercent));

    private void ScaleButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SetUiScale(DccUiScale.Previous(_settings.UiScalePercent));
    }

    private void SetUiScale(int percent)
    {
        int previous = _settings.UiScalePercent;
        try
        {
            _settings = (_settings with { UiScalePercent = percent }).WithValidatedValues();
            DccUserSettingsStore.Save(_settings);
            ScaleButton.Content = $"{_settings.UiScalePercent}%";
            ApplyUiScale(recenter: true);
            StatusText.Text = DccLocalization.T("scale_changed", _settings.UiScalePercent);
        }
        catch (Exception ex)
        {
            _settings = _settings with { UiScalePercent = previous };
            ScaleButton.Content = $"{previous}%";
            ShowError(DccLocalization.T("error_scale"), ex);
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
            return;
        ShowInTaskbar = false;
        Hide();
        UpdateTrayUi();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
