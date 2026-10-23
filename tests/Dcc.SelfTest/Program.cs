using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using NvUv.Dcc.Core;
using NvUv.Dcc.Helper;

string? fakeNvidiaSmiStart = Environment.GetEnvironmentVariable("DCC_SELFTEST_FAKE_NVIDIA_SMI_START");
if (!string.IsNullOrWhiteSpace(fakeNvidiaSmiStart))
{
    string completed = Environment.GetEnvironmentVariable("DCC_SELFTEST_FAKE_NVIDIA_SMI_COMPLETE")
        ?? throw new InvalidOperationException("Fake nvidia-smi completion marker unavailable.");
    int delayMilliseconds = int.TryParse(
        Environment.GetEnvironmentVariable("DCC_SELFTEST_FAKE_NVIDIA_SMI_DELAY_MS"),
        out int parsedDelay)
        ? parsedDelay
        : 1000;
    File.WriteAllText(fakeNvidiaSmiStart, Environment.ProcessId.ToString(), new UTF8Encoding(false));
    await Task.Delay(delayMilliseconds);
    File.WriteAllText(completed, "late-side-effect", new UTF8Encoding(false));
    return 0;
}

var failures = new List<string>();

Run("DRS DWORD setting keeps the requested setting ID", () =>
{
    const uint vsyncModeSettingId = 0x00A879CF;
    const uint vsyncForceOn = 0x47814940;
    (uint settingId, uint currentValue) =
        NvApiFrameLimiterClient.CreateDwordSettingForSelfTest(vsyncModeSettingId, vsyncForceOn);
    Equal(vsyncModeSettingId, settingId);
    Equal(vsyncForceOn, currentValue);
});

Run("elevated helper exposes no standalone diagnostic probe entry points", () =>
{
    Type helperProgram = typeof(NvApiFrameLimiterClient).Assembly.GetType(
        "NvUv.Dcc.Helper.Program",
        throwOnError: true)!;
    const System.Reflection.BindingFlags methods =
        System.Reflection.BindingFlags.Static |
        System.Reflection.BindingFlags.Public |
        System.Reflection.BindingFlags.NonPublic;
    foreach (string methodName in new[]
    {
        "RunEtwProbeAsync",
        "RunDrsProbeAsync",
        "RunDrsEnumerateAsync",
        "RunDrsRoundTripAsync",
        "ParseProbeInt",
        "ReadProbeValue"
    })
    {
        True(helperProgram.GetMethod(methodName, methods) is null);
    }

    True(typeof(NvApiFrameLimiterClient).GetMethod("Probe", methods) is null);
    True(typeof(NvApiFrameLimiterClient).Assembly.GetType(
        "NvUv.Dcc.Helper.NvApiFrameLimiterProbeResult",
        throwOnError: false) is null);
});

Run("supported clock CSV parsing", () =>
{
    const string csv = "14001, 1005\r\n14001, 997\r\n14001, 990\r\n405, 885\r\n405, 180\r\n";
    SupportedClockCatalog catalog = NvidiaSmiClient.ParseSupportedClockCsv(csv);
    Equal(180, catalog.MinimumMHz);
    Equal(1005, catalog.MaximumMHz);
    Equal(997, catalog.SnapNearest(1000));
    Equal(885, catalog.SnapNearest(887));
    Equal(5, catalog.GraphicsBinsMHz.Count);
});

Run("GPU discovery keeps idle records but marks unusable telemetry", () =>
{
    const string idle = "0, GPU-idle, GeForce Idle, 999.99, P8, N/A, N/A, N/A, N/A, Disabled, 00000000:01:00.0";
    IReadOnlyList<NvidiaGpuInfo> parsed = NvidiaSmiClient.ParseGpuCsv(idle);
    Equal(1, parsed.Count);
    Equal("GPU-idle", parsed[0].Uuid);
    Equal(0, parsed[0].GraphicsClockMHz);
    True(!parsed[0].TelemetryAvailable);
});

Run("FPS freshness rejects non-finite telemetry", () =>
{
    True(!new DccFpsSample(true, 1, double.NaN, TimeSpan.Zero, 10, "test").IsFresh);
    True(!new DccFpsSample(true, 1, 100, TimeSpan.Zero, 10, "test",
        P95FrameTimeMilliseconds: double.PositiveInfinity).IsFresh);
    True(new DccFpsSample(true, 1, 100, TimeSpan.Zero, 10, "test",
        P95FrameTimeMilliseconds: 10,
        P99FrameTimeMilliseconds: 12).IsFresh);
});

Run("monotonic application time never moves backward", () =>
{
    DateTimeOffset first = DccMonotonicClock.Now;
    DateTimeOffset second = DccMonotonicClock.Now;
    True(second >= first);
});

await RunAsync("cancelled nvidia-smi is terminated before cancellation returns", async () =>
{
    await VerifyCancelledNvidiaSmiCannotCompleteAsync(timeoutInsteadOfCancellation: false);
});

await RunAsync("timed-out nvidia-smi is terminated before timeout returns", async () =>
{
    await VerifyCancelledNvidiaSmiCannotCompleteAsync(timeoutInsteadOfCancellation: true);
});

Run("clock catalog deduplication", () =>
{
    var catalog = new SupportedClockCatalog([997, 180, 997, 802, 180]);
    Equal(3, catalog.GraphicsBinsMHz.Count);
    Equal(802, catalog.SnapNearest(800));
    True(catalog.Contains(997));
    True(!catalog.Contains(1000));
});

Run("automatic primary GPU selection", () =>
{
    NvidiaGpuInfo secondary = TestGpu(1, "GPU-B", displayActive: false, utilization: 90);
    NvidiaGpuInfo primary = TestGpu(0, "GPU-A", displayActive: true, utilization: 5);
    Equal("GPU-A", NvidiaGpuSelector.SelectPrimary([secondary, primary]).Uuid);
    Equal("GPU-B", NvidiaGpuSelector.SelectPrimary([secondary, primary], "gpu-b").Uuid);
});

Run("game display context selects the largest client area", () =>
{
    DccGameDisplayProbe.WindowCandidate selected = DccGameDisplayProbe.SelectLargestClient(
    [
        new DccGameDisplayProbe.WindowCandidate(new IntPtr(1), 1280, 720),
        new DccGameDisplayProbe.WindowCandidate(new IntPtr(2), 1920, 1080),
        new DccGameDisplayProbe.WindowCandidate(new IntPtr(3), 0, 2160)
    ]);
    Equal(new IntPtr(2), selected.Window);
    Equal(1920, selected.Width);
    Equal(1080, selected.Height);
    var context = new DccGameDisplayInfo(1920, 1080, 3840, 2160, 240, true, @"\\.\DISPLAY1");
    Equal("1920x1080", context.GameClientLogValue);
    Equal("3840x2160", context.DisplayLogValue);
    Equal("240", context.DisplayRefreshLogValue);
    True(context.IsPrimary);
    Equal(@"\\.\DISPLAY1", context.DisplayDeviceName);
    True(DccGameDisplayProbe.CanResolveNvidiaEfficiencyTarget(context, manualTarget: false));
    True(DccGameDisplayProbe.CanResolveNvidiaEfficiencyTarget(DccGameDisplayInfo.Unknown, manualTarget: true));
    True(!DccGameDisplayProbe.CanResolveNvidiaEfficiencyTarget(DccGameDisplayInfo.Unknown, manualTarget: false));

});

Run("NVIDIA efficiency AUTO target respects the Windows primary monitor refresh rate", () =>
{
    Equal(240, DccFpsRange.ResolveNvidiaEfficiencyTarget(1000, 240, manualTarget: false));
    Equal(240, DccFpsRange.ResolveNvidiaEfficiencyTarget(120, 240, manualTarget: false));
    Equal(120, DccFpsRange.ResolveNvidiaEfficiencyTarget(120, 240, manualTarget: true));
    Equal(60, DccFpsRange.ResolveNvidiaEfficiencyTarget(20, 240, manualTarget: true));
    Equal(60, DccFpsRange.ResolveNvidiaEfficiencyTarget(1000, 59, manualTarget: false));
    Equal(240, DccFpsRange.ResolveNvidiaEfficiencyTarget(240, 144, manualTarget: true));
    Equal(60, DccFpsRange.ResolveNvidiaEfficiencyTarget(1000, 0, manualTarget: false));
});

Run("dedicated local data directory", () =>
{
    Equal("NVUV-DCC", Path.GetFileName(DccPaths.DataDirectory));
    Equal(
        Path.GetFullPath(DccPaths.DataDirectory),
        Path.GetFullPath(Path.GetDirectoryName(DccPaths.SharedGameDatabaseFile)!));
});

Run("localized UI settings validation", () =>
{
    DccUserSettings settings = new DccUserSettings(
        TargetFps: 999,
        Language: "RU",
        UiScalePercent: 500).WithValidatedValues();
    Equal(999, settings.TargetFps);
    Equal("ru", settings.Language);
    Equal(300, settings.UiScalePercent);

    DccUserSettings method = new DccUserSettings(
        ControlMethod: (DccControlMethod)999).WithValidatedValues();
    Equal(DccControlMethod.DccGovernor, method.ControlMethod);
    DccUserSettings baselineCannotBecomeAControlChoice = new DccUserSettings(
        ControlMethod: DccControlMethod.Baseline).WithValidatedValues();
    Equal(DccControlMethod.DccGovernor, baselineCannotBecomeAControlChoice.ControlMethod);

    DccUserSettings defaults = new DccUserSettings().WithValidatedValues();
    Equal(DccControlMethod.DccGovernor, defaults.ControlMethod);
    True(!defaults.FrameLimiterEnabled);
    Equal(DccActivationScope.Persistent, defaults.ActivationScope);
    True(!defaults.PerformanceLoggingEnabled);
    True(!defaults.StartMinimizedToTray);
    True(!defaults.NvidiaVsyncEnabled);
    True(!defaults.NvidiaTargetFpsManual);

    DccUserSettings migrated = new DccUserSettings(
        SchemaVersion: 2,
        ControlMethod: DccControlMethod.DccGovernor,
        FrameLimiterEnabled: true,
        ActivationScope: DccActivationScope.PerGame).WithMigratedValues();
    Equal(10, migrated.SchemaVersion);
    Equal(DccControlMethod.DccGovernor, migrated.ControlMethod);
    True(!migrated.FrameLimiterEnabled);
    Equal(DccActivationScope.Persistent, migrated.ActivationScope);

    DccUserSettings current = new DccUserSettings(
        SchemaVersion: 3,
        ControlMethod: DccControlMethod.NvidiaEfficiency,
        FrameLimiterEnabled: true,
        ActivationScope: DccActivationScope.PerGame).WithMigratedValues();
    Equal(10, current.SchemaVersion);
    Equal(DccControlMethod.DccGovernor, current.ControlMethod);
    True(!current.FrameLimiterEnabled);
    Equal(DccActivationScope.Persistent, current.ActivationScope);

    DccUserSettings currentSchema = new DccUserSettings(
        SchemaVersion: 4,
        ControlMethod: DccControlMethod.NvidiaEfficiency,
        FrameLimiterEnabled: true,
        ActivationScope: DccActivationScope.PerGame,
        PerformanceLoggingEnabled: true).WithMigratedValues();
    Equal(10, currentSchema.SchemaVersion);
    Equal(DccControlMethod.DccGovernor, currentSchema.ControlMethod);
    True(!currentSchema.FrameLimiterEnabled);
    Equal(DccActivationScope.PerGame, currentSchema.ActivationScope);
    True(!currentSchema.PerformanceLoggingEnabled);

    DccUserSettings previousLocalSchema = new DccUserSettings(
        SchemaVersion: 5,
        ControlMethod: DccControlMethod.NvidiaEfficiency,
        FrameLimiterEnabled: true,
        PerformanceLoggingEnabled: true).WithMigratedValues();
    Equal(DccControlMethod.DccGovernor, previousLocalSchema.ControlMethod);
    True(!previousLocalSchema.FrameLimiterEnabled);
    True(!previousLocalSchema.PerformanceLoggingEnabled);

    DccUserSettings currentChoices = new DccUserSettings(
        SchemaVersion: 6,
        ControlMethod: DccControlMethod.NvidiaEfficiency,
        FrameLimiterEnabled: true,
        PerformanceLoggingEnabled: true).WithMigratedValues();
    Equal(DccControlMethod.NvidiaEfficiency, currentChoices.ControlMethod);
    True(currentChoices.FrameLimiterEnabled);
    True(currentChoices.PerformanceLoggingEnabled);
    True(!currentChoices.StartMinimizedToTray);

    DccUserSettings trayPreference = new DccUserSettings(
        SchemaVersion: 7,
        GovernorEnabled: true,
        StartMinimizedToTray: true).WithMigratedValues();
    True(trayPreference.GovernorEnabled);
    True(trayPreference.StartMinimizedToTray);
    Equal(120, trayPreference.NvidiaTargetFps);
    Equal(120, trayPreference.FrameLimitFps);

    DccUserSettings migratedTrayPreference = new DccUserSettings(
        SchemaVersion: 6,
        StartMinimizedToTray: true).WithMigratedValues();
    True(!migratedTrayPreference.StartMinimizedToTray);

    DccUserSettings lowerBounds = new DccUserSettings(
        TargetFps: 1,
        Language: "fr",
        UiScalePercent: 1,
        ControlMethod: DccControlMethod.DccGovernor).WithValidatedValues();
    Equal(20, lowerBounds.TargetFps);
    True(lowerBounds.Language is "de" or "en" or "ru" or "es");
    Equal(100, lowerBounds.UiScalePercent);
    Equal(110, DccUiScale.Next(100));
    Equal(100, DccUiScale.Next(300));
    Equal(300, DccUiScale.Previous(100));
    Equal(250, DccUiScale.Previous(300));

    DccUserSettings nvidiaLowerBound = new DccUserSettings(
        TargetFps: 1,
        ControlMethod: DccControlMethod.NvidiaEfficiency).WithValidatedValues();
    Equal(60, nvidiaLowerBound.TargetFps);

    DccUserSettings nvidiaUpperBound = new DccUserSettings(
        TargetFps: 1001,
        ControlMethod: DccControlMethod.NvidiaEfficiency).WithValidatedValues();
    Equal(1000, nvidiaUpperBound.TargetFps);

    DccUserSettings separatedTargets = new DccUserSettings(
        NvidiaTargetFps: 1001,
        FrameLimitFps: 1).WithValidatedValues();
    Equal(1000, separatedTargets.NvidiaTargetFps);
    Equal(20, separatedTargets.FrameLimitFps);

    DccUserSettings migratedTargets = new DccUserSettings(
        SchemaVersion: 7,
        TargetFps: 225).WithMigratedValues();
    Equal(225, migratedTargets.NvidiaTargetFps);
    Equal(225, migratedTargets.FrameLimitFps);

    DccUserSettings previousSchemaVsync = new DccUserSettings(
        SchemaVersion: 8,
        NvidiaVsyncEnabled: true).WithMigratedValues();
    True(!previousSchemaVsync.NvidiaVsyncEnabled);

    DccUserSettings currentVsync = new DccUserSettings(
        SchemaVersion: 9,
        NvidiaVsyncEnabled: true).WithMigratedValues();
    True(currentVsync.NvidiaVsyncEnabled);
    True(!currentVsync.NvidiaTargetFpsManual);

    DccUserSettings migratedManualTarget = new DccUserSettings(
        SchemaVersion: 9,
        NvidiaTargetFps: 144).WithMigratedValues();
    True(migratedManualTarget.NvidiaTargetFpsManual);
    DccUserSettings migratedAutomaticMaximum = new DccUserSettings(
        SchemaVersion: 9,
        NvidiaTargetFps: 1000).WithMigratedValues();
    True(!migratedAutomaticMaximum.NvidiaTargetFpsManual);
});

Run("autostart task is elevated, user-bound, and path-bound", () =>
{
    const string executable = @"C:\Program Files\DCC & Tools\DCC-by-NV-UV.exe";
    const string sid = "S-1-5-21-1000-2000-3000-4000";
    string xml = DccAutoStartTaskDefinition.BuildXml(executable, sid);

    True(xml.Contains("HighestAvailable", StringComparison.Ordinal));
    True(xml.Contains("InteractiveToken", StringComparison.Ordinal));
    True(xml.Contains("DCC &amp; Tools", StringComparison.Ordinal));
    True(xml.Contains(DccAutoStartTaskDefinition.TaskNameForUser(sid), StringComparison.Ordinal));
    True(DccAutoStartTaskDefinition.Matches(xml, executable, sid));
    True(!DccAutoStartTaskDefinition.Matches(
        xml,
        @"C:\Program Files\Different DCC\DCC-by-NV-UV.exe",
        sid));
    True(!DccAutoStartTaskDefinition.Matches(
        xml,
        executable,
        "S-1-5-21-9999-9999-9999-9999"));

    XDocument normalized = XDocument.Parse(xml);
    foreach (XElement element in normalized.Descendants().Where(
                 element => element.Name.LocalName is "Enabled" or "URI").ToArray())
    {
        element.Remove();
    }
    XElement workingDirectory = normalized.Descendants().Single(
        element => element.Name.LocalName == "WorkingDirectory");
    workingDirectory.Value += @"\";
    True(DccAutoStartTaskDefinition.Matches(
        normalized.ToString(SaveOptions.DisableFormatting), executable, sid));

    string downgraded = xml.Replace(
        "HighestAvailable",
        "LeastPrivilege",
        StringComparison.Ordinal);
    True(DccAutoStartTaskDefinition.ValidationFailures(downgraded, executable, sid)
        .Contains("run-level", StringComparer.Ordinal));
    Throws<ArgumentException>(() => DccAutoStartTaskDefinition.TaskNameForUser("not-a-sid"));
});

Run("NVIDIA efficiency request state remains explicit", () =>
{
    DccJpacRecoveryState recovery = DccJpacRecoveryState.Create(100);
    Equal(100, recovery.TargetFps);
    Equal(60, DccJpacRecoveryState.Create(1).TargetFps);
    Equal(999, DccJpacRecoveryState.Create(999).TargetFps);
    Equal(1000, DccJpacRecoveryState.Create(1001).TargetFps);

    var accepted = new DccJpacVerification(
        RequestedEnabled: true,
        RequestedTargetFps: 100,
        DriverCommandAccepted: true,
        HelperStateMatches: true,
        HelperTargetMatches: true,
        DriverReadbackAvailable: false,
        DriverEnabled: null);
    True(!accepted.Confirmed);
    True(accepted.CommandPathConfirmed);
    True(accepted.Hotfix7EnableConfirmed);
    True(accepted.LogValue.Contains("jpac_target_fps=100", StringComparison.Ordinal));
    True(accepted.LogValue.Contains("driver-readback-unavailable", StringComparison.Ordinal));

    var confirmed = accepted with { DriverReadbackAvailable = true, DriverEnabled = true };
    True(confirmed.Confirmed);
    True(confirmed.Hotfix7EnableConfirmed);
    True(confirmed.LogValue.StartsWith("driver-readback-confirmed:enabled", StringComparison.Ordinal));

    var contradicted = accepted with { DriverReadbackAvailable = true, DriverEnabled = false };
    True(!contradicted.Hotfix7EnableConfirmed);
});

await RunAsync("emergency reset attempts clock reset after independent restore failures", async () =>
{
    var calls = new List<string>();
    var observedFailures = new List<string>();
    try
    {
        await DccEmergencyResetCoordinator.RunAllAsync(
        [
            new DccEmergencyResetOperation("vsync", () =>
            {
                calls.Add("vsync");
                throw new InvalidOperationException("injected VSync failure");
            }),
            new DccEmergencyResetOperation("frame_limiter", () =>
            {
                calls.Add("frame_limiter");
                throw new InvalidOperationException("injected limiter failure");
            }),
            new DccEmergencyResetOperation("gpu_clock", () =>
            {
                calls.Add("gpu_clock");
                return Task.CompletedTask;
            })
        ],
        (stage, _) => observedFailures.Add(stage));
        throw new InvalidOperationException("Expected AggregateException.");
    }
    catch (AggregateException ex)
    {
        Equal(2, ex.InnerExceptions.Count);
    }

    Equal("vsync,frame_limiter,gpu_clock", string.Join(',', calls));
    Equal("vsync,frame_limiter", string.Join(',', observedFailures));
});

Run("bundled NV-UV game catalog", () =>
{
    string bundled = Path.Combine(AppContext.BaseDirectory, "Data", "GameDatabase.json");
    True(File.Exists(bundled));
    Equal(647, GameCatalogUpdateService.ValidatePayload(File.ReadAllBytes(bundled)));
    True(typeof(DccGameCatalog).Assembly.GetManifestResourceNames().Contains(
        "NvUv.Dcc.Core.Data.GameDatabase.json",
        StringComparer.Ordinal));
    DccGameCatalog catalog = DccGameCatalog.Load(bundled);
    True(catalog.Games.Count >= 647);
    Equal("Grim Dawn", catalog.FindByExecutable("GRIM DAWN.EXE")!.Name);
    DccGameDefinition kcd2 = catalog.FindCandidatesByExecutable("kingdomcome.exe")
        .Single(game => game.Name.Contains("Deliverance II", StringComparison.OrdinalIgnoreCase));
    Equal(
        DccGameCatalog.CreateStableGameKey("kingdom-come-deliverance-ii"),
        kcd2.StableGameKey);
    True(kcd2.EffectiveInstallPathHints.Contains(
        "KingdomComeDeliverance2",
        StringComparer.OrdinalIgnoreCase));
    True(DccGameCatalog.IsUnsupportedExecutable("CrashReportClient.exe"));
    True(DccGameCatalog.IsUnsupportedExecutable("explorer.exe"));
    True(DccGameCatalog.IsUnsupportedExecutable("steamwebhelper.exe"));
    True(DccGameCatalog.IsUnsupportedExecutable("battle.net.exe"));
    True(DccGameCatalog.IsUnsupportedExecutable("NVIDIA App.exe"));
    True(DccGameCatalog.IsUnsupportedExecutable("NVCPLUI.EXE"));
    True(DccGameCatalog.IsUnsupportedExecutable("MSIAfterburner.exe"));
    True(DccGameCatalog.IsUnsupportedExecutable("EpicGamesLauncher.exe"));
    True(DccGameCatalog.IsUnsupportedExecutable("GalaxyClient.exe"));
    True(DccGameCatalog.IsUnsupportedExecutable("XboxPcApp.exe"));
    True(!DccGameCatalog.IsUnsupportedExecutable("launcher.exe"));
    True(!DccGameCatalog.IsUnsupportedExecutable("IcarusLauncher.exe"));
    True(!DccGameCatalog.IsUnsupportedExecutable("grim dawn.exe"));
});

Run("NV-UV FPS sample freshness", () =>
{
    var fresh = new DccFpsSample(
        true,
        4242,
        120,
        TimeSpan.FromMilliseconds(100),
        150,
        "NV-UV ETW/DXGI",
        P95FrameTimeMilliseconds: 8.4,
        P99FrameTimeMilliseconds: 10.2);
    var stale = fresh with { SampleAge = TimeSpan.FromSeconds(4) };
    var noFrames = DccFpsSample.NoFrames(4242, "NV-UV ETW");

    True(fresh.IsFresh);
    True(!stale.IsFresh);
    True(!noFrames.IsFresh);
    Equal(0d, noFrames.FramesPerSecond);
    Equal(8.4, fresh.P95FrameTimeMilliseconds);
    Equal(10.2, fresh.P99FrameTimeMilliseconds);
});

Run("ETW FPS keeps parallel swap chains separate", () =>
{
    var events = new List<(string Runtime, ulong SwapChainId, double TimestampMilliseconds)>();
    for (ulong swapChain = 1; swapChain <= 6; swapChain++)
    {
        for (int frame = 0; frame <= 150; frame++)
            events.Add(("D3D9", swapChain, frame * (1000d / 120d)));
    }

    DccFpsSample sample = DccEtwFpsCollector.ReadSyntheticForSelfTest(4242, events);

    True(sample.IsFresh);
    True(Math.Abs(sample.FramesPerSecond - 120) < 0.01);
    Equal(151, sample.FrameCount);
    True(Math.Abs(sample.P95FrameTimeMilliseconds - (1000d / 120d)) < 0.01);
    Equal("NV-UV ETW/D3D9", sample.Source);
});

Run("ETW FPS selects a sustained dominant present stream", () =>
{
    var events = new List<(string Runtime, ulong SwapChainId, double TimestampMilliseconds)>();
    for (int frame = 0; frame <= 75; frame++)
        events.Add(("DXGI", 10, frame * (1000d / 60d)));
    for (int frame = 0; frame <= 150; frame++)
        events.Add(("DXGI", 20, frame * (1000d / 120d)));
    for (int frame = 0; frame <= 200; frame++)
        events.Add(("DXGI", 30, 1150 + frame * 0.5));

    DccFpsSample selected = DccEtwFpsCollector.ReadSyntheticForSelfTest(4242, events);
    DccFpsSample single = DccEtwFpsCollector.ReadSyntheticForSelfTest(
        4242,
        Enumerable.Range(0, 76)
            .Select(frame => ("DXGI", 0UL, frame * (1000d / 60d))));

    True(Math.Abs(selected.FramesPerSecond - 120) < 0.01);
    Equal(151, selected.FrameCount);
    True(Math.Abs(single.FramesPerSecond - 60) < 0.01);
    Equal(76, single.FrameCount);
});

Run("background process detection cannot invent FPS", () =>
{
    var detected = new DccDetectedGame(
        new DccGameDefinition("Grim Dawn", "grim dawn.exe", [], false),
        "grim dawn.exe",
        4242,
        null,
        false,
        "Process");
    True(!detected.FpsFresh);
    True(detected.FramesPerSecond is null);
    Equal("grim dawn.exe", detected.GameKey);
});

Run("background processes are never promoted to active games", () =>
{
    using Process current = Process.GetCurrentProcess();
    string executable = DccGameCatalog.NormalizeExecutable(current.ProcessName);
    var detector = new DccGameDetector([
        new DccGameDefinition("Self-test", executable, [], false)
    ]);

    True(detector.DetectProcess(foregroundProcessId: 0) is null);
});

Run("shared ClientG executable is disambiguated by the CnC Remastered path", () =>
{
    var detector = new DccGameDetector([
        new DccGameDefinition(
            "8-Bit Hordes",
            "clientg.exe",
            ["clientlauncherg.exe"],
            false,
            NvidiaProfileName: "8-Bit Hordes")
    ]);

    DccDetectedGame? remastered = detector.DetectProcess(
        100,
        "ClientG.exe",
        @"D:\SteamLibrary\steamapps\common\CnCRemastered\ClientG.exe",
        []);
    DccDetectedGame? hordes = detector.DetectProcess(
        200,
        "ClientG.exe",
        @"D:\SteamLibrary\steamapps\common\8BitHordes\ClientG.exe",
        []);

    True(remastered is not null);
    Equal("Command & Conquer Remastered Collection", remastered!.Game.Name);
    Equal("8-Bit Hordes", remastered.Game.NvidiaProfileName);
    Equal("clientg.exe", remastered.Executable);
    Equal("clientg.exe", remastered.Game.Executable);
    Equal("dcc-cncremastered-clientg.exe", remastered.GameKey);
    True(hordes is not null);
    Equal("8-Bit Hordes", hordes!.Game.Name);
    Equal("clientg.exe", hordes.Executable);
    Equal("clientg.exe", hordes.Game.Executable);
    Equal("dcc-8bithordes-clientg.exe", hordes.GameKey);
    True(!string.Equals(remastered.GameKey, hordes.GameKey, StringComparison.OrdinalIgnoreCase));
    True(!string.Equals(
        DccLearnedProfile.CreateKey("GPU-A", remastered.GameKey, 0),
        DccLearnedProfile.CreateKey("GPU-A", hordes.GameKey, 0),
        StringComparison.OrdinalIgnoreCase));
});

Run("shared KingdomCome executable resolves KCD2 by install path", () =>
{
    string kcd2Key = DccGameCatalog.CreateStableGameKey("kingdom-come-deliverance-ii");
    var detector = new DccGameDetector([
        new DccGameDefinition(
            "Kingdom Come: Deliverance",
            "kingdomcome.exe",
            [],
            false,
            NvidiaProfileName: "Kingdom Come: Deliverance"),
        new DccGameDefinition(
            "Kingdom Come: Deliverance II",
            "kingdomcome.exe",
            [],
            false,
            NvidiaProfileName: "Kingdom Come: Deliverance II",
            StableGameKey: kcd2Key,
            InstallPathHints: ["KingdomComeDeliverance2"])
    ]);

    DccDetectedGame? first = detector.DetectProcess(
        100,
        "KingdomCome.exe",
        @"D:\SteamLibrary\steamapps\common\KingdomComeDeliverance\Bin\Win64\KingdomCome.exe",
        []);
    DccDetectedGame? second = detector.DetectProcess(
        200,
        "KingdomCome.exe",
        @"D:\SteamLibrary\steamapps\common\KingdomComeDeliverance2\Bin\Win64MasterMasterSteamPGO\KingdomCome.exe",
        []);

    True(first is not null);
    Equal("Kingdom Come: Deliverance", first!.Game.Name);
    Equal("kingdomcome.exe", first.GameKey);
    True(second is not null);
    Equal("Kingdom Come: Deliverance II", second!.Game.Name);
    Equal(kcd2Key, second.GameKey);
    Equal("catalog-path", second.GameIdentitySource);
    True(!string.Equals(first.GameKey, second.GameKey, StringComparison.OrdinalIgnoreCase));
});

Run("detected game key override stays internal and normalized", () =>
{
    var detected = new DccDetectedGame(
        new DccGameDefinition("Visible game", "clientg.exe", [], false),
        "clientg.exe",
        4242,
        null,
        false,
        "NVIDIA DRS",
        GameKeyOverride: @"D:\Internal Keys\CNC-Learned-Key");

    Equal("clientg.exe", detected.Executable);
    Equal("clientg.exe", detected.Game.Executable);
    Equal("cnc-learned-key.exe", detected.GameKey);
});

Run("foreground Shipping process inherits its known game parent", () =>
{
    var detector = new DccGameDetector([
        new DccGameDefinition(
            "Golf It!", "golfit.exe", [], false,
            NvidiaProfileName: "Golf It!")
    ]);
    DccProcessTreeEntry[] processes =
    [
        new(100, 1, "GolfIt.exe"),
        new(200, 100, "GolfIt-Win64-Shipping.exe")
    ];

    DccDetectedGame? detected = detector.DetectProcess(
        200,
        "GolfIt-Win64-Shipping.exe",
        @"D:\Games\Golf It!\GolfIt\Binaries\Win64\GolfIt-Win64-Shipping.exe",
        processes);

    True(detected is not null);
    Equal("golfit-win64-shipping.exe", detected!.Executable);
    Equal("golfit-win64-shipping.exe", detected.Game.Executable);
    Equal("Golf It!", detected.Game.Name);
    Equal("NVIDIA DRS child process", detected.DetectionSource);
    Equal(200, detected.ProcessId);
    Equal(200, detected.FpsProcessId);
});

Run("unsupported child process cannot inherit a game parent", () =>
{
    var detector = new DccGameDetector([
        new DccGameDefinition(
            "Known game", "known-game.exe", [], false,
            NvidiaProfileName: "Known game")
    ]);
    DccProcessTreeEntry[] processes =
    [
        new(100, 1, "known-game.exe"),
        new(200, 100, "chrome.exe")
    ];

    True(detector.DetectProcess(200, "chrome.exe", @"C:\Browser\chrome.exe", processes) is null);
});

Run("arbitrary child process cannot inherit a game parent", () =>
{
    var detector = new DccGameDetector([
        new DccGameDefinition(
            "Known game", "known-game.exe", [], false,
            NvidiaProfileName: "Known game")
    ]);
    DccProcessTreeEntry[] processes =
    [
        new(100, 1, "known-game.exe"),
        new(200, 100, "unknown-child.exe")
    ];

    True(detector.DetectProcess(
        200, "unknown-child.exe", @"D:\Games\Known\unknown-child.exe", processes) is null);
});

Run("RTX Remix bridge must belong to the confirmed game process tree", () =>
{
    DccProcessTreeEntry[] processes =
    [
        new(100, 1, "hl2.exe"),
        new(110, 100, "remix-bootstrap.exe"),
        new(120, 110, "NvRemixBridge.exe"),
        new(200, 1, "other-game.exe"),
        new(220, 200, "NvRemixBridge.exe")
    ];

    IReadOnlyList<DccProcessTreeEntry> bridges =
        DccRendererBridgeResolver.FindForGameProcess(100, processes);
    Equal(1, bridges.Count);
    Equal(120, bridges.Single().ProcessId);
});

Run("launcher processes cannot enter game history", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "dcc-launcher-history-selftest-" + Guid.NewGuid().ToString("N"));
    string historyFile = Path.Combine(directory, "game-history.json");
    try
    {
        DccGameHistoryStore history = DccGameHistoryStore.Load(historyFile);
        Throws<InvalidDataException>(() => history.RememberActivation(
            "Battle.net", "battle.net.exe", "Battle.net",
            DccControlMethod.NvidiaEfficiency, 120));
        Throws<InvalidDataException>(() => history.RememberActivation(
            "NVIDIA Control Panel", "nvcplui.exe", "NVIDIA Control Panel",
            DccControlMethod.NvidiaEfficiency, 120));
        True(history.Entries.Count == 0);
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
});

Run("LIMIT protocol preserves missing paths and Unicode names", () =>
{
    DccFrameLimitCommand created = DccFrameLimitCommand.Create(
        executablePath: string.Empty,
        executableName: "ManorLords-Win64-Shipping.exe",
        friendlyName: "Manor Lords – Überprüfung",
        targetFps: 120);
    string serialized = created.Serialize();
    string[] parts = serialized.Split(
        ' ',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    DccFrameLimitCommand parsed = DccFrameLimitCommand.Parse(parts);

    Equal(5, parts.Length);
    Equal("~", parts[2]);
    Equal(string.Empty, parsed.ExecutablePath);
    Equal("manorlords-win64-shipping.exe", parsed.ExecutableName);
    Equal("Manor Lords – Überprüfung", parsed.FriendlyName);
    Equal(120, parsed.TargetFps);
});

Run("LIMIT protocol derives missing optional game data", () =>
{
    DccFrameLimitCommand command = DccFrameLimitCommand.Create(
        @"C:\Games\Example Game\ExampleGame.exe",
        executableName: string.Empty,
        friendlyName: string.Empty,
        targetFps: 100);
    DccFrameLimitCommand parsed = DccFrameLimitCommand.Parse(command.Serialize().Split(' '));

    Equal("examplegame.exe", parsed.ExecutableName);
    Equal("examplegame", parsed.FriendlyName);
    True(parsed.ExecutablePath.EndsWith("ExampleGame.exe", StringComparison.Ordinal));
});

Run("LIMIT protocol rejects incomplete commands", () =>
{
    Throws<InvalidDataException>(() => DccFrameLimitCommand.Parse(["LIMIT", "120", "~", "~"]));
    Throws<InvalidDataException>(() => DccFrameLimitCommand.Parse(["LIMIT", "120", "~", "~", "~"]));
    Throws<InvalidDataException>(() => DccFrameLimitCommand.Parse(["LIMIT", "1001", "~", "Z2FtZS5leGU=", "Z2FtZQ=="]));
});

Run("NVIDIA driver application index", () =>
{
    var application = new DccDriverApplication(
        "Grim Dawn",
        @"C:\Games\Grim Dawn\grim dawn.exe",
        "Grim Dawn",
        true);
    DccGameDefinition game = application.ToGameDefinition();
    Equal("grim dawn.exe", game.Executable);
    Equal("Grim Dawn", game.NvidiaProfileName);

    var catalog = new DccGameCatalog();
    catalog.AddRuntimeGames([game]);
    Equal("Grim Dawn", catalog.FindByExecutable("GRIM DAWN.EXE")!.Name);

    DccGameDefinition controlPanel = new DccDriverApplication(
        "NVIDIA Control Panel",
        @"C:\Program Files\NVIDIA Corporation\Control Panel Client\nvcplui.exe",
        "NVIDIA Control Panel",
        true).ToGameDefinition();
    catalog.AddRuntimeGames([controlPanel]);
    True(catalog.FindByExecutable("nvcplui.exe") is null);
});

Run("activated game history persists and updates", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "dcc-history-selftest-" + Guid.NewGuid().ToString("N"));
    string historyFile = Path.Combine(directory, "game-history.json");
    try
    {
        DateTimeOffset first = DateTimeOffset.UtcNow.AddMinutes(-5);
        DccGameHistoryStore history = DccGameHistoryStore.Load(historyFile);
        history.RememberActivation(
            "Grim Dawn", "grim dawn.exe", "Grim Dawn",
            DccControlMethod.DccGovernor, 120, first);
        Equal(DccLearnedProfile.AutomaticTargetFps, history.Entries.Single().LastTargetFps);
        history.RememberActivation(
            "Grim Dawn", "GRIM DAWN.EXE", "Grim Dawn",
            DccControlMethod.NvidiaEfficiency, 100,
            activatedAtUtc: first.AddMinutes(1), frameLimiterEnabled: true,
            frameLimitFps: 144);

        DccGameHistoryEntry entry = DccGameHistoryStore.Load(historyFile).Entries.Single();
        Equal(2, entry.ActivationCount);
        Equal(100, entry.LastTargetFps);
        Equal(DccControlMethod.NvidiaEfficiency, entry.LastMethod);
        True(entry.LastFrameLimiterEnabled);
        Equal(144, entry.LastFrameLimitFps);
        Equal("Grim Dawn", entry.NvidiaProfileName);
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
});
Run("game history separates stable identities sharing one executable", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "dcc-shared-game-history-selftest-" + Guid.NewGuid().ToString("N"));
    string historyFile = Path.Combine(directory, "game-history.json");
    try
    {
        string kcd2Key = DccGameCatalog.CreateStableGameKey("kingdom-come-deliverance-ii");
        DccGameHistoryStore history = DccGameHistoryStore.Load(historyFile);
        history.RememberActivation(
            "Kingdom Come: Deliverance",
            "kingdomcome.exe",
            "Kingdom Come: Deliverance",
            DccControlMethod.DccGovernor,
            0,
            gameKey: "kingdomcome.exe");
        history.RememberActivation(
            "Kingdom Come: Deliverance II",
            "kingdomcome.exe",
            "Kingdom Come: Deliverance II",
            DccControlMethod.DccGovernor,
            0,
            gameKey: kcd2Key);

        Equal(2, history.Entries.Count);
        DccGameHistoryStore reloaded = DccGameHistoryStore.Load(historyFile);
        Equal(2, reloaded.Entries.Count);
        True(reloaded.Entries.Any(entry => entry.EffectiveGameKey == "kingdomcome.exe"));
        True(reloaded.Entries.Any(entry => entry.EffectiveGameKey == kcd2Key));
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
});

Run("target-FPS governor learning", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    governor.Start(120, catalog, learnedClockMHz: null, now);
    int? appliedCap = null;
    bool persisted = false;

    for (int second = 1; second <= 180 && !persisted; second++)
    {
        int observedClock = appliedCap ?? 2505;
        double fps = observedClock >= 1050 ? 120 : 120d * observedClock / 1050;
        DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), fps, 98, observedClock));
        if (decision.ResetClock)
        {
            True(governor.CurrentCapMHz is null);
            appliedCap = null;
        }
        if (decision.ApplyClockMHz.HasValue)
            appliedCap = decision.ApplyClockMHz;
        persisted = decision.PersistProfile;
    }

    True(persisted);
    Equal(DccGovernorPhase.Locked, governor.Phase);
    True(governor.CurrentCapMHz is >= 1050 and <= 1200);
});

Run("governor retries an initially unreachable target", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    governor.Start(120, catalog, learnedClockMHz: null, now);

    for (int second = 1; second <= 6; second++)
        governor.Observe(new DccGovernorSample(now.AddSeconds(second), 30, 20, 500));
    Equal(DccGovernorPhase.TargetUnreachable, governor.Phase);

    for (int second = 7; second <= 12; second++)
        governor.Observe(new DccGovernorSample(now.AddSeconds(second), 120, 95, 2505));
    Equal(DccGovernorPhase.Baseline, governor.Phase);
});

Run("governor reset decisions clear the reported clock cap", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    int maximumClock = catalog.MaximumMHz;
    governor.Start(120, catalog, learnedClockMHz: maximumClock, now);
    DccGovernorDecision decision = new(DccGovernorPhase.Idle, DccGovernorStatus.WaitingForGame);

    for (int second = 1; second <= 5; second++)
    {
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 30, 99, maximumClock, 35));
        if (decision.ResetClock)
            break;
    }

    True(decision.ResetClock);
    Equal(DccGovernorPhase.TargetUnreachable, decision.Phase);
    True(governor.CurrentCapMHz is null);
});

Run("governor restores headroom for GPU-bound frametime spikes", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(120, catalog, learnedClockMHz: 1050, now);
    Equal(DccGovernorPhase.VerifyLearned, start.Phase);
    Equal(1050, start.ApplyClockMHz!.Value);
    int currentClock = start.ApplyClockMHz.Value;

    for (int second = 1; second <= 5; second++)
    {
        DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 95, currentClock, 8.5));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }
    Equal(DccGovernorPhase.Locked, governor.Phase);

    bool recoveryRaisedClock = false;
    for (int second = 6; second <= 10; second++)
    {
        DccGovernorDecision recovery = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 95, currentClock, 20));
        recoveryRaisedClock |= recovery.ApplyClockMHz > 1050;
        currentClock = recovery.ApplyClockMHz ?? currentClock;
    }
    True(recoveryRaisedClock);
    True(currentClock > 1050);
    True(governor.Phase is DccGovernorPhase.Recovery or DccGovernorPhase.Locked);
});

Run("learned profile survives low-utilization loading screens", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(120, catalog, learnedClockMHz: 1050, now);
    Equal(1050, start.ApplyClockMHz!.Value);
    bool resetRequested = false;
    bool loadingStatusReported = false;

    for (int second = 1; second <= 8; second++)
    {
        DccGovernorDecision loading = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 35, 5, 1050, 30));
        resetRequested |= loading.ResetClock;
        loadingStatusReported |= loading.Status == DccGovernorStatus.LoadingOrCpuLimited;
    }

    True(!resetRequested);
    True(loadingStatusReported);
    Equal(DccGovernorPhase.VerifyLearned, governor.Phase);
    Equal(1050, governor.CurrentCapMHz!.Value);

    for (int second = 9; second <= 13; second++)
        governor.Observe(new DccGovernorSample(now.AddSeconds(second), 120, 90, 1050, 8.5));

    Equal(DccGovernorPhase.Locked, governor.Phase);
    Equal(1050, governor.CurrentCapMHz!.Value);
});

Run("governor slowly revisits efficiency after sustained headroom", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(100, catalog, learnedClockMHz: 1560, now);
    int currentClock = start.ApplyClockMHz ?? 2505;
    DccGovernorDecision decision = new(DccGovernorPhase.Idle, DccGovernorStatus.WaitingForGame);

    for (int second = 1; second <= 45 && governor.Phase != DccGovernorPhase.EfficiencyProbe; second++)
    {
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 100, 94, currentClock, 10));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }

    Equal(DccGovernorPhase.EfficiencyProbe, governor.Phase);
    int lowerClock = decision.ApplyClockMHz ??
        throw new InvalidOperationException("The efficiency probe did not apply a lower clock.");
    True(lowerClock < 1560);
    bool persistedLowerClock = false;

    for (int second = 46; second <= 51; second++)
    {
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 100, 65, lowerClock, 10));
        persistedLowerClock |= decision.PersistProfile;
    }

    Equal(DccGovernorPhase.Locked, governor.Phase);
    Equal(lowerClock, governor.CurrentCapMHz!.Value);
    True(persistedLowerClock);

    var saturatedGovernor = new DccClockGovernor();
    DccGovernorDecision saturatedStart = saturatedGovernor.Start(
        100, catalog, learnedClockMHz: 1560, now);
    int saturatedClock = saturatedStart.ApplyClockMHz ?? 2505;
    bool probedBelowKnownClock = false;
    for (int second = 1; second <= 60; second++)
    {
        DccGovernorDecision saturated = saturatedGovernor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 100, 96, saturatedClock, 10));
        if (saturated.ApplyClockMHz is int appliedClock)
        {
            saturatedClock = appliedClock;
            probedBelowKnownClock |= appliedClock < 1560;
        }
    }
    True(!probedBelowKnownClock);
    Equal(1560, saturatedGovernor.CurrentCapMHz!.Value);

    var uncappedGovernor = new DccClockGovernor();
    DccGovernorDecision uncappedStart = uncappedGovernor.Start(
        100, catalog, learnedClockMHz: 1560, now);
    int uncappedClock = uncappedStart.ApplyClockMHz ?? 2505;
    for (int second = 1; second <= 45 &&
         uncappedGovernor.Phase != DccGovernorPhase.EfficiencyProbe; second++)
    {
        DccGovernorDecision uncapped = uncappedGovernor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 99, uncappedClock, 8.5));
        uncappedClock = uncapped.ApplyClockMHz ?? uncappedClock;
    }
    Equal(DccGovernorPhase.EfficiencyProbe, uncappedGovernor.Phase);
    True(uncappedClock < 1560);
});

Run("governor immediately restores the known-good clock after a failed downshift", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(100, catalog, learnedClockMHz: 1560, now);
    int currentClock = start.ApplyClockMHz ?? 2505;
    DccGovernorDecision decision = new(DccGovernorPhase.Idle, DccGovernorStatus.WaitingForGame);
    int second = 0;

    while (second < 45 && governor.Phase != DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 100, 60, currentClock, 10));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }
    Equal(DccGovernorPhase.EfficiencyProbe, governor.Phase);
    int probeClock = currentClock;

    while (second < 55 && governor.Phase == DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 93, 98, probeClock, 14));
    }

    Equal(DccGovernorPhase.Locked, governor.Phase);
    Equal(1560, decision.ApplyClockMHz!.Value);
    Equal(1560, governor.CurrentCapMHz!.Value);
});

Run("automatic governor learns from GPU utilization without an FPS target", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    governor.Start(catalog, learnedClockMHz: null, now);
    int? currentCap = null;
    int persistedProfiles = 0;

    for (int second = 1; second <= 300; second++)
    {
        int effectiveClock = currentCap ?? 2505;
        double utilization = Math.Min(100, 60d * 2505 / effectiveClock);
        DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, utilization, effectiveClock, 8.5));
        if (decision.ResetClock)
            currentCap = null;
        if (decision.ApplyClockMHz.HasValue)
            currentCap = decision.ApplyClockMHz;
        if (decision.PersistProfile)
            persistedProfiles++;
    }

    True(persistedProfiles > 0);
    True(governor.CurrentCapMHz is >= 1500 and <= 1750);
    Equal(DccGovernorPhase.Locked, governor.Phase);
});

Run("automatic governor leaves an uncapped saturated GPU alone", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    governor.Start(catalog, learnedClockMHz: null, now);
    bool changedDriverState = false;

    for (int second = 1; second <= 60; second++)
    {
        DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 225, 99, 2505, 4.5));
        changedDriverState |= decision.ChangesDriverState;
    }

    True(!changedDriverState);
    True(governor.CurrentCapMHz is null);
    Equal(DccGovernorPhase.Locked, governor.Phase);
});

Run("automatic governor keeps lowering a stable low-load game", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    governor.Start(catalog, learnedClockMHz: null, now);
    int? currentCap = null;
    bool persisted = false;

    for (int second = 1; second <= 300; second++)
    {
        int effectiveClock = currentCap ?? 2505;
        double utilization = Math.Min(100, 40d * 2505 / effectiveClock);
        DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, utilization, effectiveClock, 8.5));
        if (decision.ResetClock)
            currentCap = null;
        if (decision.ApplyClockMHz.HasValue)
            currentCap = decision.ApplyClockMHz;
        persisted |= decision.PersistProfile;
    }

    True(persisted);
    True(governor.CurrentCapMHz < 1500);
    Equal(2505, governor.TrustedClockMHz!.Value);
    Equal(governor.CurrentCapMHz!.Value, governor.LowLoadClockMHz!.Value);
});

Run("automatic governor restores a learned clock after a failed utilization probe", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(catalog, learnedClockMHz: 1560, now);
    int currentClock = start.ApplyClockMHz!.Value;
    int second = 0;

    while (second < 30 && governor.Phase != DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 85, currentClock, 8.5));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }

    Equal(DccGovernorPhase.EfficiencyProbe, governor.Phase);
    True(currentClock < 1560);
    DccGovernorDecision restored = new(DccGovernorPhase.Idle, DccGovernorStatus.WaitingForGame);
    while (second < 40 && governor.Phase == DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        restored = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 110, 99, currentClock, 12));
    }

    Equal(DccGovernorStatus.StableClockRestored, restored.Status);
    Equal(1560, restored.ApplyClockMHz!.Value);
    Equal(1560, governor.CurrentCapMHz!.Value);
});

Run("automatic governor confirms and blocks a power-regressive downshift", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 61).Select(index => 300 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision decision = governor.Start(catalog, learnedClockMHz: 600, now);
    int currentClock = decision.ApplyClockMHz!.Value;
    int second = 0;

    while (second < 10 && governor.Phase != DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 80, currentClock, 8.5, 9.5, 80));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }

    Equal(570, currentClock);
    while (second < 20 && currentClock < 600)
    {
        second++;
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 84, currentClock, 8.5, 9.5, 86));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }

    Equal(DccGovernorPhase.EfficiencyProbe, governor.Phase);
    Equal(DccGovernorStatus.RecheckingEfficiency, decision.Status);
    Equal(600, decision.ApplyClockMHz!.Value);
    currentClock = decision.ApplyClockMHz.Value;

    while (second < 30 && governor.Phase == DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 80, currentClock, 8.5, 9.5, 80));
    }

    Equal(DccGovernorStatus.StableClockRestored, decision.Status);
    Equal(600, governor.CurrentCapMHz!.Value);
    Equal(600, governor.PowerValleyFloorMHz!.Value);

    governor.Start(catalog, learnedClockMHz: 600, now.AddMinutes(1));
    True(governor.PowerValleyFloorMHz is null);
});

Run("automatic governor accepts a downshift that also lowers board power", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 61).Select(index => 300 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision decision = governor.Start(catalog, learnedClockMHz: 600, now);
    int currentClock = decision.ApplyClockMHz!.Value;
    int second = 0;

    while (second < 10 && governor.Phase != DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 80, currentClock, 8.5, 9.5, 80));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }

    while (second < 20 && governor.Phase == DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 84, currentClock, 8.5, 9.5, 75));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }

    Equal(DccGovernorStatus.LowerClockConfirmed, decision.Status);
    Equal(570, governor.CurrentCapMHz!.Value);
    True(governor.PowerValleyFloorMHz is null);
});

Run("automatic governor does not confirm a power floor across a scene change", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 61).Select(index => 300 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision decision = governor.Start(catalog, learnedClockMHz: 600, now);
    int currentClock = decision.ApplyClockMHz!.Value;
    int second = 0;

    while (second < 10 && governor.Phase != DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 80, currentClock, 8.5, 9.5, 80));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }

    while (second < 20 && currentClock < 600)
    {
        second++;
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 84, currentClock, 8.5, 9.5, 86));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }

    while (second < 30 && governor.Phase == DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 88, currentClock, 8.5, 9.5, 88));
    }

    Equal(DccGovernorStatus.StableClockRestored, decision.Status);
    True(governor.PowerValleyFloorMHz is null);
});

Run("automatic governor ignores a single board-power outlier", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 61).Select(index => 300 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision decision = governor.Start(catalog, learnedClockMHz: 600, now);
    int currentClock = decision.ApplyClockMHz!.Value;
    int second = 0;

    while (second < 10 && governor.Phase != DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 80, currentClock, 8.5, 9.5, 80));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }

    double[] candidatePowers = [79, 200, 79];
    foreach (double candidatePower in candidatePowers)
    {
        second++;
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 84, currentClock, 8.5, 9.5, candidatePower));
    }

    Equal(DccGovernorStatus.LowerClockConfirmed, decision.Status);
    Equal(570, governor.CurrentCapMHz!.Value);
    True(governor.PowerValleyFloorMHz is null);
});

Run("automatic governor has no fixed low-utilization learning barrier", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    governor.Start(catalog, learnedClockMHz: 1560, now);
    bool persisted = false;
    bool reset = false;

    for (int second = 1; second <= 30; second++)
    {
        DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 30, 10, 1560, 33));
        persisted |= decision.PersistProfile;
        reset |= decision.ResetClock;
    }

    True(persisted);
    True(!reset);
    True(governor.CurrentCapMHz < 1560);
    Equal(1560, governor.TrustedClockMHz!.Value);
    Equal(governor.CurrentCapMHz!.Value, governor.LowLoadClockMHz!.Value);
});

Run("automatic governor raises a low-load clock in bounded recovery steps", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(catalog, learnedClockMHz: 1770, now);
    int currentClock = start.ApplyClockMHz!.Value;
    int second = 0;

    while (second < 180 && !(currentClock <= 900 && governor.Phase == DccGovernorPhase.Locked))
    {
        second++;
        DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 50, currentClock, 8.5));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }

    True(currentClock <= 900);
    int lowLoadClock = currentClock;
    DccGovernorDecision recovery = new(DccGovernorPhase.Idle, DccGovernorStatus.WaitingForGame);
    for (int index = 0; index < 6 && recovery.ApplyClockMHz is null; index++)
    {
        second++;
        recovery = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 90, 99, currentClock, 11));
        currentClock = recovery.ApplyClockMHz ?? currentClock;
    }

    Equal(DccGovernorStatus.IncreasingFpsHeadroom, recovery.Status);
    Equal(catalog.StepByMHz(lowLoadClock, 150), recovery.ApplyClockMHz!.Value);
    True(recovery.ApplyClockMHz < 1770);
    Equal(DccGovernorPhase.Recovery, governor.Phase);
    Equal(1770, governor.TrustedClockMHz!.Value);
    Equal(lowLoadClock, governor.LowLoadClockMHz!.Value);
});

Run("automatic governor resumes near a legacy low-load clock", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(
        catalog,
        learnedClockMHz: 1560,
        learnedLowLoadClockMHz: 1200,
        now);
    int currentClock = start.ApplyClockMHz!.Value;
    DccGovernorDecision decision = start;
    int second = 0;

    Equal(1380, currentClock);
    while (second < 5 && governor.Phase == DccGovernorPhase.VerifyLearned)
    {
        second++;
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 50, currentClock, 8.5));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }

    Equal(DccGovernorPhase.EfficiencyProbe, governor.Phase);
    Equal(1320, currentClock);

    bool persisted = false;
    while (second < 10 && governor.Phase == DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 80, currentClock, 8.5));
        persisted |= decision.PersistProfile;
    }

    True(persisted);
    Equal(1560, governor.TrustedClockMHz!.Value);
    Equal(1320, governor.LowLoadClockMHz!.Value);
    Equal(1320, governor.ResumeClockMHz!.Value);
});

Run("automatic governor reuses a confirmed per-game resume clock", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;

    DccGovernorDecision start = governor.Start(
        catalog,
        learnedClockMHz: 2730,
        learnedLowLoadClockMHz: 1290,
        learnedResumeClockMHz: 1290,
        now);

    Equal(1350, start.ApplyClockMHz!.Value);
    Equal(1290, governor.ResumeClockMHz!.Value);
    Equal(DccGovernorPhase.VerifyLearned, governor.Phase);
});

Run("automatic governor defers resume until startup frames are stable", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(
        catalog,
        learnedClockMHz: 2730,
        learnedLowLoadClockMHz: 1290,
        learnedResumeClockMHz: 1290,
        now,
        deferLearnedApplication: true);

    True(start.ApplyClockMHz is null);
    True(governor.CurrentCapMHz is null);
    DccGovernorDecision decision = start;
    double[] startupFps = [30, 60, 90, 120, 120];
    for (int second = 1; second <= startupFps.Length; second++)
    {
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), startupFps[second - 1], 30, 2500, 10, 20));
    }
    True(decision.ApplyClockMHz is null);
    True(governor.CurrentCapMHz is null);

    for (int second = 6; second <= 10; second++)
    {
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 30, 2500, 8.5, 9));
    }

    Equal(2355, decision.ApplyClockMHz!.Value);
    Equal(2355, governor.CurrentCapMHz!.Value);

    for (int second = 11; second <= 13; second++)
    {
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 30, 2355, 8.5, 9));
    }
    Equal(DccGovernorPhase.EfficiencyProbe, governor.Phase);
    Equal(2205, decision.ApplyClockMHz!.Value);
});

Run("automatic governor scales resume speed with proven game starts", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    DateTimeOffset now = DateTimeOffset.UtcNow;

    int FirstStagedClock(int resumeConfidence)
    {
        var governor = new DccAutomaticClockGovernor();
        DccGovernorDecision decision = governor.Start(
            catalog,
            learnedClockMHz: 2730,
            learnedLowLoadClockMHz: 1290,
            learnedResumeClockMHz: 1290,
            now,
            deferLearnedApplication: true,
            resumeConfidence: resumeConfidence);

        for (int second = 1; second <= 5; second++)
        {
            decision = governor.Observe(new DccGovernorSample(
                now.AddSeconds(second), 120, 30, 2500, 8.5, 9));
        }

        return decision.ApplyClockMHz!.Value;
    }

    Equal(2355, FirstStagedClock(0));
    Equal(2205, FirstStagedClock(1));
    Equal(2055, FirstStagedClock(2));
    Equal(1905, FirstStagedClock(3));
});

Run("automatic governor promotes a stable resumed profile", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision decision = governor.Start(
        catalog,
        learnedClockMHz: 2730,
        learnedLowLoadClockMHz: 1290,
        learnedResumeClockMHz: 1290,
        now,
        deferLearnedApplication: true,
        resumeConfidence: 1);
    int currentClock = 2500;
    bool resumeConfirmed = false;

    for (int second = 1; second <= 40 && !resumeConfirmed; second++)
    {
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 30, currentClock, 8.5, 9));
        currentClock = decision.ApplyClockMHz ?? currentClock;
        resumeConfirmed = decision.ResumeValidation == DccResumeValidation.Succeeded;
    }

    True(resumeConfirmed);
    Equal(2, governor.ResumeConfidence);
    True(governor.CurrentCapMHz <= 1350);
});

Run("automatic governor demotes an unstable resumed profile", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision decision = governor.Start(
        catalog,
        learnedClockMHz: 2730,
        learnedLowLoadClockMHz: 1290,
        learnedResumeClockMHz: 1290,
        now,
        deferLearnedApplication: true,
        resumeConfidence: 2);
    int currentClock = 2500;

    int second = 0;
    while (second < 15 && governor.Phase != DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 30, currentClock, 8.5, 9));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }
    Equal(DccGovernorPhase.EfficiencyProbe, governor.Phase);

    while (second < 20 && decision.ResumeValidation == DccResumeValidation.None)
    {
        second++;
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 30, currentClock, 8.5, 30));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }

    Equal(DccResumeValidation.Failed, decision.ResumeValidation);
    Equal(1, governor.ResumeConfidence);
    Equal(DccGovernorStatus.StableClockRestored, decision.Status);
});

Run("automatic governor can learn a stable menu recovery clock", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(
        catalog,
        learnedClockMHz: 2730,
        learnedLowLoadClockMHz: 1290,
        learnedResumeClockMHz: 1290,
        now);
    int currentClock = start.ApplyClockMHz!.Value;
    DccGovernorDecision decision = start;

    for (int second = 1; second <= 3; second++)
    {
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 10, currentClock, 8.5, 9));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }
    Equal(DccGovernorPhase.EfficiencyProbe, governor.Phase);

    for (int second = 4; second <= 6; second++)
    {
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 10, currentClock, 8.5, 20));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }

    Equal(DccGovernorStatus.StableClockRestored, decision.Status);
    Equal(1350, governor.ResumeClockMHz!.Value);
});

Run("automatic governor requires one continuous minute before persisting a profile", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(
        catalog,
        learnedClockMHz: 1560,
        learnedLowLoadClockMHz: null,
        learnedResumeClockMHz: null,
        now,
        requireLearningConfidence: true);
    int currentClock = start.ApplyClockMHz!.Value;
    bool persistedTooEarly = false;
    bool persistedAfterMinute = false;

    for (int second = 1; second <= 30; second++)
    {
        DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 80, currentClock, 8.5, 9));
        currentClock = decision.ApplyClockMHz ?? currentClock;
        persistedTooEarly |= decision.PersistProfile;
    }

    for (int second = 35; second <= 94; second++)
    {
        DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 80, currentClock, 8.5, 9));
        currentClock = decision.ApplyClockMHz ?? currentClock;
        persistedTooEarly |= decision.PersistProfile;
    }

    for (int second = 95; second <= 100; second++)
    {
        DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 80, currentClock, 8.5, 9));
        currentClock = decision.ApplyClockMHz ?? currentClock;
        persistedAfterMinute |= decision.PersistProfile;
    }

    True(!persistedTooEarly);
    True(persistedAfterMinute);
    True(governor.ProfilePersistenceReady);
});

Run("automatic governor uses finer steps as GPU load rises", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    DateTimeOffset now = DateTimeOffset.UtcNow;

    int FirstProbe(double utilization)
    {
        var governor = new DccAutomaticClockGovernor();
        DccGovernorDecision start = governor.Start(catalog, learnedClockMHz: 1560, now);
        int currentClock = start.ApplyClockMHz!.Value;
        for (int second = 1; second <= 5 && currentClock == 1560; second++)
        {
            DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
                now.AddSeconds(second), 120, utilization, currentClock, 8.5));
            currentClock = decision.ApplyClockMHz ?? currentClock;
        }
        return currentClock;
    }

    Equal(1500, FirstProbe(50));
    Equal(1530, FirstProbe(75));
    Equal(1545, FirstProbe(88));
});

Run("automatic governor parks between probes once GPU load is relevant", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(catalog, learnedClockMHz: 1560, now);
    int currentClock = start.ApplyClockMHz!.Value;
    var appliedClocks = new List<int>();

    for (int second = 1; second <= 14; second++)
    {
        DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 80, currentClock, 8.5, 10));
        if (decision.ApplyClockMHz is int appliedClock)
        {
            appliedClocks.Add(appliedClock);
            currentClock = appliedClock;
        }
    }

    Equal(1, appliedClocks.Count);
    Equal(1530, appliedClocks[0]);
    DccGovernorDecision nextProbe = governor.Observe(new DccGovernorSample(
        now.AddSeconds(15), 120, 80, currentClock, 8.5, 10));
    Equal(1500, nextProbe.ApplyClockMHz!.Value);
});

Run("automatic governor learns clock-change sensitivity and suppresses ping-pong", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(
        catalog,
        learnedClockMHz: 1560,
        learnedLowLoadClockMHz: null,
        learnedResumeClockMHz: null,
        now,
        clockChangeSensitivityScore: 2);
    int currentClock = start.ApplyClockMHz!.Value;
    DccGovernorDecision decision = start;

    for (int second = 1; second <= 3; second++)
    {
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 80, currentClock, 8.5, 10));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }
    Equal(1530, currentClock);

    for (int second = 4; second <= 6; second++)
    {
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 84, currentClock, 14, 18));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }

    Equal(1560, currentClock);
    True(decision.ClockChangeAssessment is not null);
    True(decision.ClockChangeAssessment!.CorrelatedFrameTimeSpike);
    Equal(3, governor.ClockChangeSensitivityScore);
    True(governor.IsClockChangeSensitive);

    bool repeatedRejectedProbe = false;
    for (int second = 7; second <= 120; second++)
    {
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 80, currentClock, 8.5, 10));
        if (decision.ApplyClockMHz is int appliedClock)
        {
            repeatedRejectedProbe |= appliedClock < 1560;
            currentClock = appliedClock;
        }
    }
    True(!repeatedRejectedProbe);
    Equal(1560, currentClock);
});

Run("clock-change sensitivity never blocks urgent recovery", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(
        catalog,
        learnedClockMHz: 1200,
        learnedLowLoadClockMHz: null,
        learnedResumeClockMHz: null,
        now,
        clockChangeSensitivityScore: DccAutomaticClockGovernor.ClockChangeSensitivityThreshold);
    int currentClock = start.ApplyClockMHz!.Value;
    DccGovernorDecision decision = start;
    for (int second = 1; second <= 3; second++)
    {
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 90, 99, currentClock, 12, 15));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }
    True(currentClock > 1200);
    Equal(DccGovernorStatus.IncreasingFpsHeadroom, decision.Status);
});

Run("automatic governor rejects a downshift that hurts one-percent lows", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(catalog, learnedClockMHz: 1560, now);
    int currentClock = start.ApplyClockMHz!.Value;
    DccGovernorDecision decision = start;

    for (int second = 1; second <= 3; second++)
    {
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 80, currentClock, 8.5, 10));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }

    Equal(DccGovernorPhase.EfficiencyProbe, governor.Phase);
    Equal(1530, currentClock);

    for (int second = 4; second <= 6; second++)
    {
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 84, currentClock, 8.5, 18));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }

    Equal(DccGovernorStatus.StableClockRestored, decision.Status);
    Equal(1560, currentClock);
});

Run("automatic governor detects a stable FPS lock", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    governor.Start(catalog, learnedClockMHz: 1560, now);

    for (int second = 1; second <= 10; second++)
    {
        governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 119.8, 92, 1560, 8.5));
    }

    True(governor.DetectedFpsLimit is >= 119 and <= 121);
});

Run("automatic governor protects a detected FPS lock from cumulative drift", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(catalog, learnedClockMHz: 1560, now);
    int currentClock = start.ApplyClockMHz!.Value;
    int second = 0;

    for (; second < 10; second++)
    {
        governor.Observe(new DccGovernorSample(
            now.AddSeconds(second + 1), 120, 92, currentClock, 8.5));
    }
    True(governor.DetectedFpsLimit is >= 119 and <= 121);

    while (second < 16 && governor.Phase != DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 80, currentClock, 8.5));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }
    Equal(1530, currentClock);

    while (second < 22 && governor.Phase == DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 118.5, 84, currentClock, 8.6));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }
    Equal(DccGovernorPhase.Locked, governor.Phase);

    while (second < 28 && governor.Phase != DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 118.5, 80, currentClock, 8.6));
        currentClock = decision.ApplyClockMHz ?? currentClock;
    }
    Equal(1500, currentClock);

    DccGovernorDecision restored = new(DccGovernorPhase.Idle, DccGovernorStatus.WaitingForGame);
    while (second < 34 && governor.Phase == DccGovernorPhase.EfficiencyProbe)
    {
        second++;
        restored = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 116.8, 84, currentClock, 8.7));
        currentClock = restored.ApplyClockMHz ?? currentClock;
    }

    Equal(DccGovernorStatus.StableClockRestored, restored.Status);
    Equal(1530, currentClock);

    for (int index = 0; index < 12; index++)
    {
        second++;
        governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 118.5, 92, currentClock, 8.6));
    }
    True(governor.DetectedFpsLimit is >= 119 and <= 121);
});

Run("automatic governor recognizes a changed external FPS lock", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    governor.Start(catalog, learnedClockMHz: 1560, now);

    for (int second = 1; second <= 10; second++)
    {
        governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 92, 1560, 8.5));
    }
    True(governor.DetectedFpsLimit is >= 119 and <= 121);

    for (int second = 11; second <= 22; second++)
    {
        governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 60, 92, 1560, 16.7));
    }

    True(governor.DetectedFpsLimit is >= 59 and <= 61);
});

Run("automatic governor quickly raises an overloaded learned profile", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(catalog, learnedClockMHz: 1200, now);
    DccGovernorDecision decision = start;
    bool raisedClock = false;

    for (int second = 1; second <= 5; second++)
    {
        decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 100, 99, 1200, 10));
        raisedClock |= decision.ApplyClockMHz > 1200;
    }

    True(raisedClock);
    Equal(DccGovernorPhase.Recovery, governor.Phase);
});

Run("automatic governor restores five-percent reserve after sustained load", () =>
{
    var catalog = new SupportedClockCatalog(Enumerable.Range(0, 189).Select(index => 180 + index * 15));
    var governor = new DccAutomaticClockGovernor();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DccGovernorDecision start = governor.Start(catalog, learnedClockMHz: 1500, now);
    int currentClock = start.ApplyClockMHz!.Value;
    bool raisedClock = false;

    for (int second = 1; second <= 15; second++)
    {
        DccGovernorDecision decision = governor.Observe(new DccGovernorSample(
            now.AddSeconds(second), 120, 96, currentClock, 8.5));
        if (decision.ApplyClockMHz.HasValue)
        {
            currentClock = decision.ApplyClockMHz.Value;
            raisedClock |= currentClock > 1500;
        }
    }

    True(raisedClock);
});

Run("learned profile keys remain game-specific", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "dcc-game-profile-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        DccProfileStore profiles = DccProfileStore.Load(
            Path.Combine(directory, "profiles.json"),
            Path.Combine(directory, "backups"));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        profiles.Upsert(new DccLearnedProfile(
            "GPU-TEST", "Test GPU", "1.0", "Manor Lords",
            "manorlords-win64-shipping.exe", 120, 2002, 120, 10, now));
        profiles.Upsert(new DccLearnedProfile(
            "GPU-TEST", "Test GPU", "1.0", "Kingdom Come Deliverance II",
            "kingdomcome.exe", 120, 1950, 120, 12, now.AddSeconds(1)));
        profiles.Upsert(new DccLearnedProfile(
            "GPU-TEST", "Test GPU", "1.0", "Manor Lords",
            "manorlords-win64-shipping.exe", DccLearnedProfile.AutomaticTargetFps,
            1875, 117, 14, now.AddSeconds(2), 1500, 1560));

        Equal(3, profiles.Profiles.Count);
        Equal(2002, profiles.Find("GPU-TEST", "manorlords-win64-shipping.exe", 120)!.LearnedClockMHz);
        Equal(1875, profiles.Find(
            "GPU-TEST",
            "manorlords-win64-shipping.exe",
            DccLearnedProfile.AutomaticTargetFps)!.LearnedClockMHz);
        Equal(1500, profiles.Find(
            "GPU-TEST",
            "manorlords-win64-shipping.exe",
            DccLearnedProfile.AutomaticTargetFps)!.LowLoadClockMHz!.Value);
        Equal(1560, profiles.Find(
            "GPU-TEST",
            "manorlords-win64-shipping.exe",
            DccLearnedProfile.AutomaticTargetFps)!.ResumeClockMHz!.Value);
        Equal(1950, profiles.Find("GPU-TEST", "kingdomcome.exe", 120)!.LearnedClockMHz);
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
});

Run("learned profile reset removes only the selected GPU and game", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "dcc-profile-reset-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        string profileFile = Path.Combine(directory, "profiles.json");
        string backups = Path.Combine(directory, "backups");
        DccProfileStore profiles = DccProfileStore.Load(profileFile, backups);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        profiles.Upsert(new DccLearnedProfile(
            "GPU-A", "Test GPU A", "1.0", "Shared Game", "dcc-path-a.exe",
            DccLearnedProfile.AutomaticTargetFps, 1800, 120, 20, now));
        profiles.Upsert(new DccLearnedProfile(
            "GPU-A", "Test GPU A", "1.0", "Shared Game", "dcc-path-a.exe",
            120, 1900, 120, 18, now.AddSeconds(1)));
        profiles.Upsert(new DccLearnedProfile(
            "GPU-B", "Test GPU B", "1.0", "Shared Game", "dcc-path-a.exe",
            DccLearnedProfile.AutomaticTargetFps, 1700, 120, 16, now.AddSeconds(2)));
        profiles.Upsert(new DccLearnedProfile(
            "GPU-A", "Test GPU A", "1.0", "Shared Game", "dcc-path-b.exe",
            DccLearnedProfile.AutomaticTargetFps, 1750, 120, 14, now.AddSeconds(3)));

        DccProfileResetResult result = profiles.RemoveForGame("gpu-a", "DCC-PATH-A.EXE");
        Equal(2, result.Removed);
        True(File.Exists(result.BackupPath));

        DccProfileStore reloaded = DccProfileStore.Load(profileFile, backups);
        True(reloaded.Find("GPU-A", "dcc-path-a.exe", DccLearnedProfile.AutomaticTargetFps) is null);
        True(reloaded.Find("GPU-A", "dcc-path-a.exe", 120) is null);
        True(reloaded.Find("GPU-B", "dcc-path-a.exe", DccLearnedProfile.AutomaticTargetFps) is not null);
        True(reloaded.Find("GPU-A", "dcc-path-b.exe", DccLearnedProfile.AutomaticTargetFps) is not null);
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
});

Run("learned profile persistence normalizes transient governor state", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "dcc-profile-normalize-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        string profileFile = Path.Combine(directory, "profiles.json");
        string backups = Path.Combine(directory, "backups");
        DccProfileStore profiles = DccProfileStore.Load(profileFile, backups);
        profiles.Upsert(new DccLearnedProfile(
            " GPU-TEST ", "Test GPU", "1.0", "Cyberpunk 2077", "CYBERPUNK2077.EXE",
            DccLearnedProfile.AutomaticTargetFps, 2400, double.NaN, 1_000_001,
            DateTimeOffset.UtcNow, 2400, 2700, 99, 99));

        DccLearnedProfile reloaded = DccProfileStore.Load(profileFile, backups).Find(
            "GPU-TEST",
            "cyberpunk2077.exe",
            DccLearnedProfile.AutomaticTargetFps)!;
        True(!reloaded.LowLoadClockMHz.HasValue);
        Equal(2400, reloaded.ResumeClockMHz!.Value);
        Equal(3, reloaded.ResumeConfidence!.Value);
        Equal(DccAutomaticClockGovernor.MaximumClockChangeSensitivityScore,
            reloaded.ClockChangeSensitivityScore!.Value);
        Equal(0d, reloaded.LastVerifiedFps);
        Equal(1_000_000, reloaded.LearningWindows);
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
});

Run("resume confidence remains backward compatible and updates independently", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "dcc-resume-confidence-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        DccProfileStore profiles = DccProfileStore.Load(
            Path.Combine(directory, "profiles.json"),
            Path.Combine(directory, "backups"));
        DateTimeOffset updatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var legacy = new DccLearnedProfile(
            "GPU-TEST", "Test GPU", "1.0", "Golf It", "golfit-win64-shipping.exe",
            DccLearnedProfile.AutomaticTargetFps, 817, 120, 35, updatedAt, 802, 802);
        var explicitlyUnconfirmed = legacy with
        {
            Executable = "newgame.exe",
            ResumeConfidence = 0
        };

        Equal(1, legacy.EffectiveResumeConfidence);
        Equal(0, explicitlyUnconfirmed.EffectiveResumeConfidence);
        profiles.Upsert(legacy);
        profiles.UpdateResumeConfidence(
            "gpu-test",
            "GOLFIT-WIN64-SHIPPING.EXE",
            DccLearnedProfile.AutomaticTargetFps,
            2);

        DccLearnedProfile reloaded = DccProfileStore.Load(
                Path.Combine(directory, "profiles.json"),
                Path.Combine(directory, "backups"))
            .Find(
                "GPU-TEST",
                "golfit-win64-shipping.exe",
                DccLearnedProfile.AutomaticTargetFps)!;
        Equal(2, reloaded.ResumeConfidence!.Value);
        Equal(817, reloaded.LearnedClockMHz);
        Equal(802, reloaded.LowLoadClockMHz!.Value);
        Equal(802, reloaded.ResumeClockMHz!.Value);
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
});

Run("profile export, backup, and import", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "dcc-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        string firstFile = Path.Combine(directory, "first", "profiles.json");
        string exportFile = Path.Combine(directory, "export.json");
        DccProfileStore first = DccProfileStore.Load(firstFile, Path.Combine(directory, "first-backups"));
        first.Upsert(new DccLearnedProfile(
            "GPU-TEST", "Test GPU", "1.0", "Grim Dawn", "grim dawn.exe",
            120, 1110, 120, 12, DateTimeOffset.UtcNow));
        first.Export(exportFile);
        True(File.Exists(exportFile));
        True(File.Exists(first.CreateBackup()));

        string secondFile = Path.Combine(directory, "second", "profiles.json");
        DccProfileStore second = DccProfileStore.Load(secondFile, Path.Combine(directory, "second-backups"));
        DccProfileImportResult result = second.Import(exportFile);
        Equal(1, result.Added);
        True(File.Exists(result.BackupPath));
        Equal(1110, DccProfileStore.Load(secondFile, Path.Combine(directory, "second-backups"))
            .Find("gpu-test", "GRIM DAWN.EXE", 120)!.LearnedClockMHz);
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
});

Run("diagnostic log privacy and ZIP export", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "dcc-diagnostics-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(directory);
        string userPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Secret Games",
            "grim dawn.exe");
        const string token = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        string sanitized = DccLog.Sanitize($"{userPath}\r\nAUTH {token} nvuv-dcc-0123456789abcdef0123456789abcdef");
        True(!sanitized.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase));
        True(!sanitized.Contains(token, StringComparison.Ordinal));
        True(!sanitized.Contains("nvuv-dcc-0123456789abcdef0123456789abcdef", StringComparison.OrdinalIgnoreCase));
        string stackPath = DccLog.Sanitize("at Test() in S:\\private build\\Program.cs:line 42     at Main()");
        True(!stackPath.Contains("S:\\private build", StringComparison.OrdinalIgnoreCase));

        string sourceLog = Path.Combine(directory, "source.log");
        File.WriteAllText(sourceLog, $"failure at {userPath} token={token}", Encoding.UTF8);
        string report = Path.Combine(directory, "diagnostics.zip");
        var snapshot = new DccDiagnosticSnapshot(
            "0.1.0-test", "Test OS", "X64", "X64", "Test GPU", 0,
            "GPU-PRIVATE-IDENTIFIER", "999.99", "P8", "0000:01:00.0",
            180, 3000, 189, false, false, 120, "en", 100, false,
            1, 647, false, "unavailable", 0, userPath);
        DccDiagnosticReport.Export(report, snapshot, [new DccDiagnosticLogSource("ui.log", sourceLog)]);

        using ZipArchive archive = ZipFile.OpenRead(report);
        Equal(3, archive.Entries.Count);
        string combined = string.Join("\n", archive.Entries.Select(entry =>
        {
            using StreamReader reader = new(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }));
        True(!combined.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase));
        True(!combined.Contains(token, StringComparison.Ordinal));
        True(!combined.Contains("GPU-PRIVATE-IDENTIFIER", StringComparison.Ordinal));
        True(combined.Contains("GPU fingerprint:", StringComparison.Ordinal));
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
});

Run("optional per-game performance log and comparison export", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "dcc-performance-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        string logs = Path.Combine(directory, "logs");
        DateTimeOffset started = DateTimeOffset.UtcNow.AddSeconds(-2);
        using (DccPerformanceRecorder recorder = DccPerformanceRecorder.Start(
                   new DccPerformanceSessionMetadata(
                        "0.1.0-test", "Grim Dawn", "Grim Dawn.exe", 1234, "Grim Dawn",
                        "Test GPU", "ABC123", "999.99", DccControlMethod.DccGovernor,
                        0, false, 120, DccActivationScope.Persistent, started,
                        GameClientWidth: 1920,
                        GameClientHeight: 1080,
                        DisplayWidth: 3840,
                        DisplayHeight: 2160,
                        GameKey: "dcc-game-grim-dawn.exe"),
                   logs))
        {
            recorder.Append(new DccPerformanceSample(
                started.AddSeconds(1), 100, 11, 100, 10, 70, 50, 990, 1000, 990, "P2",
                "clock-cap-requested;graphics-clock-telemetry-readback",
                P99FrameTimeMilliseconds: 12,
                CoreVoltageMillivolts: 800,
                ClockChangeSensitivityScore: 3,
                ClockChangeSensitive: true,
                ClockChangeAssessment: "correlated-frametime-spike;from=1020;to=990"));
            recorder.Append(new DccPerformanceSample(
                started.AddSeconds(2), 98, 13, 98, 20, 80, 60, 1050, 1000, 1050, "P2",
                "clock-cap-requested;graphics-clock-telemetry-readback",
                P99FrameTimeMilliseconds: 14,
                CoreVoltageMillivolts: 820));
        }

        using (DccPerformanceRecorder recorder = DccPerformanceRecorder.Start(
                   new DccPerformanceSessionMetadata(
                        "0.1.0-test", "Grim Dawn", "Grim Dawn.exe", 1234, "Grim Dawn",
                        "Test GPU", "ABC123", "999.99", DccControlMethod.Baseline,
                        0, false, 120, DccActivationScope.Persistent, started.AddMinutes(1),
                        GameKey: "dcc-game-grim-dawn.exe"),
                   logs))
        {
            recorder.Append(new DccPerformanceSample(
                started.AddMinutes(1).AddSeconds(1), 100, 10, 100, 10, 120, 80, 2700, 14001,
                null, "P0", "baseline;dcc-control-off;graphics-clock-telemetry-readback"));
        }

        True(DccPerformanceArchive.HasLogs(logs));
        string[] runs = Directory.EnumerateFiles(logs, "*.csv", SearchOption.AllDirectories).ToArray();
        Equal(2, runs.Length);
        string run = runs.Single(path => Path.GetFileName(path).Contains("DccGovernor", StringComparison.Ordinal));
        string raw = File.ReadAllText(run, Encoding.UTF8);
        True(raw.Contains("power_w", StringComparison.Ordinal));
        True(raw.Contains("# DCC performance log schema=5", StringComparison.Ordinal));
        True(raw.Contains("# game_key=dcc-game-grim-dawn.exe", StringComparison.Ordinal));
        True(raw.Contains("gpu_core_voltage_mv", StringComparison.Ordinal));
        True(raw.Contains("clock_change_sensitivity_score", StringComparison.Ordinal));
        True(raw.Contains("correlated-frametime-spike", StringComparison.Ordinal));
        True(raw.Contains("# game_client_resolution=1920x1080", StringComparison.Ordinal));
        True(raw.Contains("# display_resolution=3840x2160", StringComparison.Ordinal));
        True(raw.Contains(",800", StringComparison.Ordinal));
        True(raw.Contains(",820", StringComparison.Ordinal));
        True(raw.Contains("# control_target_fps=automatic", StringComparison.Ordinal));
        True(raw.Contains("# frame_limit_fps=120", StringComparison.Ordinal));
        True(raw.Contains("100,10,70,50,990", StringComparison.Ordinal));

        string export = Path.Combine(directory, "performance.zip");
        DccPerformanceArchive.Export(export, logs);
        using ZipArchive archive = ZipFile.OpenRead(export);
        True(archive.Entries.Any(entry => entry.FullName == "comparison-summary.csv"));
        ZipArchiveEntry comparison = archive.GetEntry("comparison-summary.csv")!;
        using StreamReader reader = new(comparison.Open(), Encoding.UTF8);
        string summary = reader.ReadToEnd();
        True(summary.Contains("Grim Dawn", StringComparison.Ordinal));
        True(summary.Contains("DccGovernor", StringComparison.Ordinal));
        True(summary.Contains("Baseline", StringComparison.Ordinal));
        True(summary.Contains("baseline;dcc-control-off", StringComparison.Ordinal));
        True(summary.Contains(",99,98,", StringComparison.Ordinal));
        True(summary.Contains("average_gpu_core_voltage_mv", StringComparison.Ordinal));
        True(summary.Contains(",810,800,820", StringComparison.Ordinal));
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
});

if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
{
    await RunAsync("live read-only NVIDIA discovery", async () =>
    {
        var client = new NvidiaSmiClient();
        IReadOnlyList<NvidiaGpuInfo> gpus = await client.QueryGpusAsync();
        True(gpus.Count > 0);
        foreach (NvidiaGpuInfo gpu in gpus)
        {
            SupportedClockCatalog catalog = await client.QuerySupportedGraphicsClocksAsync(gpu.Index);
            True(catalog.MinimumMHz > 0);
            True(catalog.MaximumMHz >= catalog.MinimumMHz);
            Console.WriteLine($"  GPU {gpu.Index}: {gpu.Name} | {catalog.MinimumMHz}–{catalog.MaximumMHz} MHz | {catalog.GraphicsBinsMHz.Count} bins");
        }
    });

    Run("live process-only game discovery", () =>
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "Data", "GameDatabase.json");
        DccGameCatalog catalog = DccGameCatalog.Load(bundled);
        DccDetectedGame? detected = new DccGameDetector(catalog).DetectProcess();
        Console.WriteLine(detected is null
            ? "  Detector: no game"
            : $"  Detector: {detected.Game.Name} | {detected.Executable} | source={detected.DetectionSource} | PID={detected.ProcessId}");
    });
}

if (failures.Count > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"DCC self-test failed ({failures.Count}):");
    foreach (string failure in failures)
        Console.Error.WriteLine("- " + failure);
    return 1;
}

Console.WriteLine();
Console.WriteLine("DCC self-test passed. No GPU state was modified.");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine("PASS " + name);
    }
    catch (Exception ex)
    {
        failures.Add(name + ": " + ex.Message);
        Console.WriteLine("FAIL " + name);
    }
}

async Task RunAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine("PASS " + name);
    }
    catch (Exception ex)
    {
        failures.Add(name + ": " + ex.Message);
        Console.WriteLine("FAIL " + name);
    }
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void True(bool condition)
{
    if (!condition)
        throw new InvalidOperationException("Condition was false.");
}

static void Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static NvidiaGpuInfo TestGpu(int index, string uuid, bool displayActive, double utilization) =>
    new(index, uuid, "Test GPU", "1", "P8", 210, 405, 10, utilization, displayActive, "0000:00:00.0");

static async Task VerifyCancelledNvidiaSmiCannotCompleteAsync(bool timeoutInsteadOfCancellation)
{
    string directory = Path.Combine(
        Path.GetTempPath(),
        "dcc-nvidia-smi-cancel-selftest-" + Guid.NewGuid().ToString("N"));
    string started = Path.Combine(directory, "started.txt");
    string completed = Path.Combine(directory, "completed.txt");
    string? previousStart = Environment.GetEnvironmentVariable("DCC_SELFTEST_FAKE_NVIDIA_SMI_START");
    string? previousComplete = Environment.GetEnvironmentVariable("DCC_SELFTEST_FAKE_NVIDIA_SMI_COMPLETE");
    string? previousDelay = Environment.GetEnvironmentVariable("DCC_SELFTEST_FAKE_NVIDIA_SMI_DELAY_MS");
    try
    {
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("DCC_SELFTEST_FAKE_NVIDIA_SMI_START", started);
        Environment.SetEnvironmentVariable("DCC_SELFTEST_FAKE_NVIDIA_SMI_COMPLETE", completed);
        Environment.SetEnvironmentVariable("DCC_SELFTEST_FAKE_NVIDIA_SMI_DELAY_MS", "900");
        var client = new NvidiaSmiClient(
            Environment.ProcessPath,
            timeoutInsteadOfCancellation ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        Task operation = client.ApplyClockRangeAsync(0, 300, 900, cancellation.Token);

        var startDeadline = Stopwatch.StartNew();
        while (!File.Exists(started) && startDeadline.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(20);
        True(File.Exists(started));
        int processId = int.Parse(File.ReadAllText(started, Encoding.UTF8));
        if (!timeoutInsteadOfCancellation)
            cancellation.Cancel();

        if (timeoutInsteadOfCancellation)
        {
            try
            {
                await operation;
                throw new InvalidOperationException("Expected NvidiaSmiException.");
            }
            catch (NvidiaSmiException)
            {
            }
        }
        else
        {
            try
            {
                await operation;
                throw new InvalidOperationException("Expected OperationCanceledException.");
            }
            catch (OperationCanceledException)
            {
            }
        }

        bool childStillRunning;
        try
        {
            using Process child = Process.GetProcessById(processId);
            childStillRunning = !child.HasExited;
        }
        catch (ArgumentException)
        {
            childStillRunning = false;
        }
        True(!childStillRunning);
        await Task.Delay(1000);
        True(!File.Exists(completed));
    }
    finally
    {
        Environment.SetEnvironmentVariable("DCC_SELFTEST_FAKE_NVIDIA_SMI_START", previousStart);
        Environment.SetEnvironmentVariable("DCC_SELFTEST_FAKE_NVIDIA_SMI_COMPLETE", previousComplete);
        Environment.SetEnvironmentVariable("DCC_SELFTEST_FAKE_NVIDIA_SMI_DELAY_MS", previousDelay);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
