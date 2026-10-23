namespace NvUv.Dcc.Core;

public enum DccGovernorPhase
{
    Idle,
    Baseline,
    VerifyLearned,
    Learning,
    Bracketing,
    Locked,
    EfficiencyProbe,
    Recovery,
    TargetUnreachable
}

public enum DccGovernorStatus
{
    WaitingForGame,
    Baseline,
    InvalidFps,
    VerifyLearnedProfile,
    Learning,
    FineTuning,
    ReducingClockHeadroom,
    RecheckingEfficiency,
    LowerClockConfirmed,
    StableClockRestored,
    IncreasingFpsHeadroom,
    Rebaseline,
    CpuOrGameLimited,
    TargetUnreachableWithoutDcc,
    WaitingForReachableTarget,
    TargetUnstable,
    ProfileLearned,
    ProfileAdjusted,
    Regulated,
    LoadingOrCpuLimited
}

public enum DccResumeValidation
{
    None,
    Succeeded,
    Failed
}

public sealed record DccGovernorSample(
    DateTimeOffset Timestamp,
    double FramesPerSecond,
    double GpuUtilizationPercent,
    int CurrentGraphicsClockMHz,
    double P95FrameTimeMilliseconds = 0,
    double P99FrameTimeMilliseconds = 0,
    double BoardPowerWatts = 0);

public sealed record DccClockChangeAssessment(
    int FromClockMHz,
    int ToClockMHz,
    double ReferenceFps,
    double ObservedFps,
    double ReferenceP95FrameTimeMilliseconds,
    double ObservedP95FrameTimeMilliseconds,
    double ReferenceP99FrameTimeMilliseconds,
    double ObservedP99FrameTimeMilliseconds,
    double ReferenceGpuUtilizationPercent,
    double ObservedGpuUtilizationPercent,
    bool ComparableWorkload,
    bool CorrelatedFrameTimeSpike,
    int SensitivityScore,
    bool IsSensitive,
    string Reason);

public sealed record DccGovernorDecision(
    DccGovernorPhase Phase,
    DccGovernorStatus Status,
    int? ApplyClockMHz = null,
    bool ResetClock = false,
    bool PersistProfile = false,
    double EvaluatedFps = 0,
    DccResumeValidation ResumeValidation = DccResumeValidation.None,
    DccClockChangeAssessment? ClockChangeAssessment = null)
{
    public bool ChangesDriverState => ApplyClockMHz.HasValue || ResetClock;
}

public sealed class DccClockGovernor
{
    private static readonly TimeSpan BaselineWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LearningWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan LockedWindow = TimeSpan.FromSeconds(3);
    private const int CoarseStepMHz = 120;
    private const int RecoveryStepMHz = 150;
    private const int DownshiftStepMHz = 60;
    private const int SafetyMarginBins = 4;
    private const int SustainedHeadroomWindows = 4;
    private const int SuccessfulProbeCooldownWindows = 2;
    private const int FailedProbeCooldownWindows = 12;
    private const double DownshiftUtilizationThreshold = 95;

    private readonly List<DccGovernorSample> _samples = [];
    private SupportedClockCatalog? _catalog;
    private DateTimeOffset _windowStarted;
    private int _targetFps;
    private int? _learnedClockMHz;
    private int? _currentCapMHz;
    private int? _lastGoodMHz;
    private int? _badLowMHz;
    private int _learningWindows;
    private bool _learnedRejected;
    private bool _windowPacingAccepted = true;
    private int _headroomWindows;
    private int _downshiftCooldownWindows;
    private int? _probePreviousClockMHz;

    public DccGovernorPhase Phase { get; private set; } = DccGovernorPhase.Idle;
    public int? CurrentCapMHz => _currentCapMHz;
    public int LearningWindows => _learningWindows;

    public DccGovernorDecision Start(
        int targetFps,
        SupportedClockCatalog catalog,
        int? learnedClockMHz,
        DateTimeOffset now)
    {
        _targetFps = Math.Clamp(targetFps, DccFpsRange.Minimum, DccFpsRange.Maximum);
        _catalog = catalog;
        _learnedClockMHz = learnedClockMHz.HasValue
            ? catalog.SnapNearest(learnedClockMHz.Value)
            : null;
        _currentCapMHz = null;
        _lastGoodMHz = null;
        _badLowMHz = null;
        _learningWindows = 0;
        _learnedRejected = false;
        _headroomWindows = 0;
        _downshiftCooldownWindows = 0;
        _probePreviousClockMHz = null;

        if (_learnedClockMHz.HasValue)
        {
            _currentCapMHz = _learnedClockMHz;
            _lastGoodMHz = _learnedClockMHz;
            BeginWindow(DccGovernorPhase.VerifyLearned, now);
            return Decision(
                DccGovernorStatus.VerifyLearnedProfile,
                _currentCapMHz);
        }

        BeginWindow(DccGovernorPhase.Baseline, now);
        return Decision(DccGovernorStatus.Baseline);
    }

    public DccGovernorDecision Stop()
    {
        bool hadCap = _currentCapMHz.HasValue;
        _samples.Clear();
        _currentCapMHz = null;
        Phase = DccGovernorPhase.Idle;
        return new DccGovernorDecision(Phase, DccGovernorStatus.WaitingForGame, ResetClock: hadCap);
    }

    public DccGovernorDecision Observe(DccGovernorSample sample)
    {
        if (_catalog is null || Phase == DccGovernorPhase.Idle)
            return Decision(DccGovernorStatus.WaitingForGame);
        if (sample.FramesPerSecond <= 0 || sample.FramesPerSecond > 10_000)
            return Decision(DccGovernorStatus.InvalidFps);

        _samples.Add(sample);
        if (sample.Timestamp - _windowStarted < CurrentWindowDuration() || _samples.Count < 3)
            return Decision(StatusForCurrentPhase());

        double evaluatedFps = Percentile(_samples.Select(item => item.FramesPerSecond), 0.25);
        double averageUtilization = _samples.Average(item => item.GpuUtilizationPercent);
        double[] validP95FrameTimes = _samples
            .Select(item => item.P95FrameTimeMilliseconds)
            .Where(value => value > 0)
            .ToArray();
        double evaluatedP95FrameTime = validP95FrameTimes.Length == 0
            ? 0
            : Percentile(validP95FrameTimes, 0.75);
        double pacingLimit = (1000d / _targetFps) * 1.35 + 0.5;
        _windowPacingAccepted = averageUtilization < 80 ||
                                evaluatedP95FrameTime <= 0 ||
                                evaluatedP95FrameTime <= pacingLimit;
        int observedClock = _catalog.SnapNearest(_samples.Max(item => item.CurrentGraphicsClockMHz));
        _learningWindows++;

        return Phase switch
        {
            DccGovernorPhase.Baseline => EvaluateBaseline(sample.Timestamp, evaluatedFps, observedClock),
            DccGovernorPhase.VerifyLearned => EvaluateVerification(sample.Timestamp, evaluatedFps, averageUtilization),
            DccGovernorPhase.Learning => EvaluateLearning(sample.Timestamp, evaluatedFps),
            DccGovernorPhase.Bracketing => EvaluateBracket(sample.Timestamp, evaluatedFps),
            DccGovernorPhase.Locked => EvaluateLocked(sample.Timestamp, evaluatedFps, averageUtilization),
            DccGovernorPhase.EfficiencyProbe => EvaluateEfficiencyProbe(sample.Timestamp, evaluatedFps),
            DccGovernorPhase.Recovery => EvaluateRecovery(sample.Timestamp, evaluatedFps, averageUtilization),
            DccGovernorPhase.TargetUnreachable => EvaluateTargetUnreachable(sample.Timestamp, evaluatedFps),
            _ => Decision(DccGovernorStatus.WaitingForGame, evaluatedFps: evaluatedFps)
        };
    }

    private DccGovernorDecision EvaluateBaseline(DateTimeOffset now, double fps, int observedClock)
    {
        if (!MeetsTarget(fps))
        {
            bool resetClock = _currentCapMHz.HasValue;
            _currentCapMHz = null;
            BeginWindow(DccGovernorPhase.TargetUnreachable, now);
            return new DccGovernorDecision(
                Phase,
                DccGovernorStatus.TargetUnreachableWithoutDcc,
                ResetClock: resetClock,
                EvaluatedFps: fps);
        }

        if (_learnedClockMHz.HasValue && !_learnedRejected)
        {
            _currentCapMHz = _learnedClockMHz;
            _lastGoodMHz = _learnedClockMHz;
            BeginWindow(DccGovernorPhase.VerifyLearned, now);
            return Decision(DccGovernorStatus.VerifyLearnedProfile, _currentCapMHz, evaluatedFps: fps);
        }

        int baselineGood = Math.Clamp(observedClock, _catalog!.MinimumMHz, _catalog.MaximumMHz);
        _lastGoodMHz = baselineGood;
        int candidate = _catalog.StepByMHz(baselineGood, -CoarseStepMHz);
        if (candidate >= baselineGood)
            return Lock(now, baselineGood, fps, DccGovernorStatus.ProfileLearned);

        _currentCapMHz = candidate;
        BeginWindow(DccGovernorPhase.Learning, now);
        return Decision(DccGovernorStatus.Baseline, candidate, evaluatedFps: fps);
    }

    private DccGovernorDecision EvaluateVerification(
        DateTimeOffset now,
        double fps,
        double utilization)
    {
        if (MeetsTarget(fps))
            return Lock(now, _currentCapMHz!.Value, fps, DccGovernorStatus.ProfileAdjusted);

        if (utilization >= 80)
        {
            _learnedRejected = true;
            _learnedClockMHz = null;
            return RaiseForRecovery(now, fps, DccGovernorStatus.IncreasingFpsHeadroom);
        }

        // Menus, loading screens, focus transitions, and CPU-limited moments can
        // miss the FPS target without the learned clock being the bottleneck.
        // Keep the persisted cap and wait for representative gameplay instead
        // of discarding a valid per-game profile and relearning from stock.
        BeginWindow(DccGovernorPhase.VerifyLearned, now);
        return Decision(
            DccGovernorStatus.LoadingOrCpuLimited,
            evaluatedFps: fps);
    }

    private DccGovernorDecision EvaluateLearning(DateTimeOffset now, double fps)
    {
        if (MeetsTarget(fps))
        {
            _lastGoodMHz = _currentCapMHz;
            int lower = _catalog!.StepByMHz(_currentCapMHz!.Value, -CoarseStepMHz);
            if (lower >= _currentCapMHz.Value)
                return Lock(now, _currentCapMHz.Value, fps, DccGovernorStatus.ProfileLearned);

            _currentCapMHz = lower;
            BeginWindow(DccGovernorPhase.Learning, now);
            return Decision(DccGovernorStatus.ReducingClockHeadroom, lower, evaluatedFps: fps);
        }

        _badLowMHz = _currentCapMHz;
        if (!_lastGoodMHz.HasValue || _lastGoodMHz <= _badLowMHz)
            return RaiseForRecovery(now, fps, DccGovernorStatus.IncreasingFpsHeadroom);

        return NextBracketCandidate(now, fps);
    }

    private DccGovernorDecision EvaluateBracket(DateTimeOffset now, double fps)
    {
        if (MeetsTarget(fps))
            _lastGoodMHz = _currentCapMHz;
        else
            _badLowMHz = _currentCapMHz;
        return NextBracketCandidate(now, fps);
    }

    private DccGovernorDecision NextBracketCandidate(DateTimeOffset now, double fps)
    {
        int lowIndex = _catalog!.IndexOfNearest(_badLowMHz!.Value);
        int highIndex = _catalog.IndexOfNearest(_lastGoodMHz!.Value);
        if (highIndex - lowIndex <= SafetyMarginBins + 1)
        {
            int safeIndex = Math.Min(_catalog.Count - 1, highIndex + SafetyMarginBins);
            return Lock(now, _catalog.At(safeIndex), fps, DccGovernorStatus.ProfileLearned);
        }

        int middleIndex = lowIndex + (highIndex - lowIndex) / 2;
        _currentCapMHz = _catalog.At(middleIndex);
        BeginWindow(DccGovernorPhase.Bracketing, now);
        return Decision(DccGovernorStatus.FineTuning, _currentCapMHz, evaluatedFps: fps);
    }

    private DccGovernorDecision EvaluateLocked(
        DateTimeOffset now,
        double fps,
        double utilization)
    {
        if (!MeetsRecoveryTarget(fps) && utilization >= 82)
            return RaiseForRecovery(now, fps, DccGovernorStatus.IncreasingFpsHeadroom);

        if (_downshiftCooldownWindows > 0)
        {
            _downshiftCooldownWindows--;
            _headroomWindows = 0;
        }
        else if (MeetsTarget(fps) &&
                 (utilization <= DownshiftUtilizationThreshold || HasSubstantialFpsOvershoot(fps)))
        {
            _headroomWindows++;
        }
        else if (utilization >= 84 || !MeetsTarget(fps))
        {
            _headroomWindows = 0;
        }
        else
        {
            _headroomWindows = Math.Max(0, _headroomWindows - 1);
        }

        if (_headroomWindows >= SustainedHeadroomWindows && _currentCapMHz.HasValue)
        {
            int lower = _catalog!.StepByMHz(_currentCapMHz.Value, -DownshiftStepMHz);
            if (lower < _currentCapMHz.Value)
            {
                _probePreviousClockMHz = _currentCapMHz;
                _currentCapMHz = lower;
                _headroomWindows = 0;
                BeginWindow(DccGovernorPhase.EfficiencyProbe, now);
                return Decision(DccGovernorStatus.RecheckingEfficiency, lower, evaluatedFps: fps);
            }
        }

        BeginWindow(DccGovernorPhase.Locked, now);
        return Decision(DccGovernorStatus.Regulated, evaluatedFps: fps);
    }

    private DccGovernorDecision EvaluateEfficiencyProbe(DateTimeOffset now, double fps)
    {
        if (MeetsTarget(fps))
        {
            int acceptedClock = _currentCapMHz!.Value;
            _lastGoodMHz = acceptedClock;
            _probePreviousClockMHz = null;
            _downshiftCooldownWindows = SuccessfulProbeCooldownWindows;
            BeginWindow(DccGovernorPhase.Locked, now);
            return new DccGovernorDecision(
                Phase,
                DccGovernorStatus.LowerClockConfirmed,
                PersistProfile: true,
                EvaluatedFps: fps);
        }

        int restoredClock = _probePreviousClockMHz ?? _lastGoodMHz ?? _catalog!.MaximumMHz;
        _currentCapMHz = _catalog!.SnapNearest(restoredClock);
        _lastGoodMHz = _currentCapMHz;
        _probePreviousClockMHz = null;
        _headroomWindows = 0;
        _downshiftCooldownWindows = FailedProbeCooldownWindows;
        BeginWindow(DccGovernorPhase.Locked, now);
        return Decision(DccGovernorStatus.StableClockRestored, _currentCapMHz, evaluatedFps: fps);
    }

    private DccGovernorDecision EvaluateRecovery(
        DateTimeOffset now,
        double fps,
        double utilization)
    {
        if (MeetsTarget(fps))
            return Lock(now, _currentCapMHz!.Value, fps, DccGovernorStatus.ProfileAdjusted);

        if (utilization < 70)
        {
            BeginWindow(DccGovernorPhase.Locked, now);
            return Decision(DccGovernorStatus.CpuOrGameLimited, evaluatedFps: fps);
        }

        return RaiseForRecovery(now, fps, DccGovernorStatus.IncreasingFpsHeadroom);
    }

    private DccGovernorDecision EvaluateTargetUnreachable(DateTimeOffset now, double fps)
    {
        if (MeetsTarget(fps))
        {
            BeginWindow(DccGovernorPhase.Baseline, now);
            return Decision(DccGovernorStatus.Rebaseline, evaluatedFps: fps);
        }

        BeginWindow(DccGovernorPhase.TargetUnreachable, now);
        return Decision(DccGovernorStatus.TargetUnreachableWithoutDcc, evaluatedFps: fps);
    }

    private DccGovernorDecision RaiseForRecovery(
        DateTimeOffset now,
        double fps,
        DccGovernorStatus status)
    {
        _headroomWindows = 0;
        _probePreviousClockMHz = null;
        _downshiftCooldownWindows = 8;
        int startingClock = _currentCapMHz ?? _lastGoodMHz ?? _catalog!.MaximumMHz;
        int raised = _catalog!.StepByMHz(startingClock, Math.Max(RecoveryStepMHz, startingClock / 12));
        if (raised <= startingClock && startingClock >= _catalog.MaximumMHz)
        {
            _currentCapMHz = null;
            BeginWindow(DccGovernorPhase.TargetUnreachable, now);
            return new DccGovernorDecision(
                Phase,
                DccGovernorStatus.TargetUnstable,
                ResetClock: true,
                EvaluatedFps: fps);
        }

        _currentCapMHz = raised;
        BeginWindow(DccGovernorPhase.Recovery, now);
        return Decision(status, raised, evaluatedFps: fps);
    }

    private DccGovernorDecision Lock(
        DateTimeOffset now,
        int clockMHz,
        double fps,
        DccGovernorStatus status)
    {
        int safeClock = _catalog!.SnapNearest(clockMHz);
        bool clockChanged = _currentCapMHz != safeClock;
        _currentCapMHz = safeClock;
        _lastGoodMHz = safeClock;
        BeginWindow(DccGovernorPhase.Locked, now);
        return new DccGovernorDecision(
            Phase,
            status,
            ApplyClockMHz: clockChanged ? safeClock : null,
            PersistProfile: true,
            EvaluatedFps: fps);
    }

    private bool MeetsTarget(double fps) =>
        _windowPacingAccepted &&
        fps >= _targetFps - Math.Max(1.2, _targetFps * 0.0125);

    private bool HasSubstantialFpsOvershoot(double fps) =>
        fps >= _targetFps + Math.Max(3, _targetFps * 0.03);

    private bool MeetsRecoveryTarget(double fps) =>
        _windowPacingAccepted &&
        fps >= _targetFps - Math.Max(2.0, _targetFps * 0.025);

    private void BeginWindow(DccGovernorPhase phase, DateTimeOffset now)
    {
        Phase = phase;
        _windowStarted = now;
        _samples.Clear();
        _windowPacingAccepted = true;
    }

    private TimeSpan CurrentWindowDuration() => Phase switch
    {
        DccGovernorPhase.Baseline => BaselineWindow,
        DccGovernorPhase.TargetUnreachable => BaselineWindow,
        DccGovernorPhase.Locked => LockedWindow,
        _ => LearningWindow
    };

    private DccGovernorDecision Decision(
        DccGovernorStatus status,
        int? applyClockMHz = null,
        bool persist = false,
        double evaluatedFps = 0) =>
        new(Phase, status, applyClockMHz, PersistProfile: persist, EvaluatedFps: evaluatedFps);

    private DccGovernorStatus StatusForCurrentPhase() => Phase switch
    {
        DccGovernorPhase.Baseline => DccGovernorStatus.Baseline,
        DccGovernorPhase.VerifyLearned => DccGovernorStatus.VerifyLearnedProfile,
        DccGovernorPhase.Learning => DccGovernorStatus.Learning,
        DccGovernorPhase.Bracketing => DccGovernorStatus.FineTuning,
        DccGovernorPhase.Locked => DccGovernorStatus.Regulated,
        DccGovernorPhase.EfficiencyProbe => DccGovernorStatus.RecheckingEfficiency,
        DccGovernorPhase.Recovery => DccGovernorStatus.IncreasingFpsHeadroom,
        DccGovernorPhase.TargetUnreachable => DccGovernorStatus.WaitingForReachableTarget,
        _ => DccGovernorStatus.WaitingForGame
    };

    private static double Percentile(IEnumerable<double> source, double percentile)
    {
        double[] sorted = source.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
            return 0;
        double position = Math.Clamp(percentile, 0, 1) * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return sorted[lower];
        double fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }
}
