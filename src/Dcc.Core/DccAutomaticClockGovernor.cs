namespace NvUv.Dcc.Core;

/// <summary>
/// Automatically searches for GPU clock headroom without requiring an FPS target.
/// GPU utilization is the primary control signal. FPS, P95 and P99 frame time reject a
/// clock reduction that measurably hurts the immediately following workload window.
/// </summary>
public sealed class DccAutomaticClockGovernor
{
    private static readonly TimeSpan BaselineWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResumeWarmupWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProfileLearningConfidenceWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaximumFreshObservationGap = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan EvaluationWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LockedWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PowerReturnValidationWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EstablishedMinimumProbeInterval = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RejectedMinimumProbeInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RejectedProbeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SensitiveMinimumProbeInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SensitiveRejectedProbeInterval = TimeSpan.FromMinutes(3);
    private const int CoarseProbeStepMHz = 60;
    private const int MediumProbeStepMHz = 30;
    private const int FineProbeStepMHz = 15;
    private const int RecoveryStepMHz = 150;
    private const int UnconfirmedResumeRampStepMHz = 150;
    private const int LearnedResumeRampStepMHz = 300;
    private const int ConfirmedResumeRampStepMHz = 450;
    private const int ProvenResumeRampStepMHz = 600;
    private const int ResumeVerificationMarginMHz = 60;
    private const int LegacyResumeSafetyMarginMHz = 180;
    private const int SustainedHeadroomWindows = 1;
    private const int FailedProbeCooldownWindows = 4;
    private const int PowerRegressionCooldownWindows = 20;
    private const int FpsLockConfirmationWindows = 3;
    private const int CleanClockChangesBeforeSensitivityDecay = 8;
    private const double HeadroomUtilizationCeiling = 90;
    private const double DesiredUtilizationCeiling = 95;
    private const double EfficiencyParkingUtilizationFloor = 70;
    private const double FpsLockWindowTolerance = 0.015;
    private const double FpsLockMatchTolerance = 0.02;
    private const double FpsRegressionTolerance = 0.02;
    private const double P95RegressionTolerance = 0.125;
    private const double P95AbsoluteToleranceMilliseconds = 0.35;
    private const double P99RegressionTolerance = 0.10;
    private const double P99AbsoluteToleranceMilliseconds = 0.75;
    private const double PowerRegressionTolerance = 0.03;
    private const double PowerRegressionAbsoluteToleranceWatts = 3;
    private const double PowerReturnMatchTolerance = 0.05;
    private const double PowerReturnAbsoluteToleranceWatts = 4;
    private const double PowerReturnUtilizationTolerance = 0.08;
    private const double PowerReturnUtilizationAbsoluteTolerance = 5;
    private const double ClockChangeP95SpikeTolerance = 0.20;
    private const double ClockChangeP95SpikeAbsoluteMilliseconds = 1.0;
    private const double ClockChangeP99SpikeTolerance = 0.20;
    private const double ClockChangeP99SpikeAbsoluteMilliseconds = 1.5;
    public const int MaximumClockChangeSensitivityScore = 6;
    public const int ClockChangeSensitivityThreshold = 3;

    private readonly List<DccGovernorSample> _samples = [];
    private SupportedClockCatalog? _catalog;
    private DateTimeOffset _windowStarted;
    private int? _currentCapMHz;
    private int? _trustedClockMHz;
    private int? _lowLoadClockMHz;
    private int? _resumeClockMHz;
    private int? _pendingInitialClockMHz;
    private int? _resumeRampTargetMHz;
    private int? _resumeValidationTargetMHz;
    private int? _lastGoodMHz;
    private int? _probePreviousClockMHz;
    private bool _probeStartedUncapped;
    private bool _probingLowLoad;
    private double _probeReferenceFps;
    private double _probeReferenceP95FrameTime;
    private double _probeReferenceP99FrameTime;
    private double _probeReferenceUtilization;
    private PowerWindow _probeReferencePower;
    private bool _powerReturnValidationPending;
    private int? _powerCandidateClockMHz;
    private PowerWindow _powerCandidateWindow;
    private int? _powerValleyFloorMHz;
    private double? _detectedFpsLimit;
    private double? _fpsLockCandidate;
    private int _learningWindows;
    private int _headroomWindows;
    private int _probeCooldownWindows;
    private int _fpsLockCandidateWindows;
    private DateTimeOffset? _continuousFreshFramesSince;
    private DateTimeOffset? _previousFreshObservationAt;
    private bool _requireLearningConfidence;
    private bool _profilePersistenceAnnounced;
    private bool _resumeValidationPending;
    private bool _resumeValidationFailedThisSession;
    private int _resumeConfidence;
    private int _clockChangeSensitivityScore;
    private int _cleanClockChangeAssessments;
    private DateTimeOffset? _lastDriverChangeAt;
    private int? _lastRejectedProbeClockMHz;
    private DateTimeOffset? _lastRejectedProbeAt;

    public DccGovernorPhase Phase { get; private set; } = DccGovernorPhase.Idle;
    public int? CurrentCapMHz => _currentCapMHz;
    public int? TrustedClockMHz => _trustedClockMHz;
    public int? LowLoadClockMHz => _lowLoadClockMHz;
    public int? ResumeClockMHz => _resumeClockMHz;
    public double? DetectedFpsLimit => _detectedFpsLimit;
    public int LearningWindows => _learningWindows;
    public int ResumeConfidence => _resumeConfidence;
    public int? PowerValleyFloorMHz => _powerValleyFloorMHz;
    public int ClockChangeSensitivityScore => _clockChangeSensitivityScore;
    public bool IsClockChangeSensitive =>
        _clockChangeSensitivityScore >= ClockChangeSensitivityThreshold;
    public bool ProfilePersistenceReady =>
        !_requireLearningConfidence ||
        (_continuousFreshFramesSince.HasValue &&
         _previousFreshObservationAt - _continuousFreshFramesSince >= ProfileLearningConfidenceWindow);

    public DccGovernorDecision Start(
        SupportedClockCatalog catalog,
        int? learnedClockMHz,
        DateTimeOffset now) =>
        Start(catalog, learnedClockMHz, learnedLowLoadClockMHz: null, learnedResumeClockMHz: null, now: now);

    public DccGovernorDecision Start(
        SupportedClockCatalog catalog,
        int? learnedClockMHz,
        int? learnedLowLoadClockMHz,
        DateTimeOffset now) =>
        Start(catalog, learnedClockMHz, learnedLowLoadClockMHz, learnedResumeClockMHz: null, now);

    public DccGovernorDecision Start(
        SupportedClockCatalog catalog,
        int? learnedClockMHz,
        int? learnedLowLoadClockMHz,
        int? learnedResumeClockMHz,
        DateTimeOffset now,
        bool deferLearnedApplication = false,
        bool requireLearningConfidence = false,
        int resumeConfidence = 0,
        int clockChangeSensitivityScore = 0)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        int? trustedClock = learnedClockMHz.HasValue
            ? catalog.SnapNearest(learnedClockMHz.Value)
            : null;
        int? lowLoadClock =
            trustedClock.HasValue && learnedLowLoadClockMHz.HasValue
                ? catalog.SnapNearest(learnedLowLoadClockMHz.Value)
                : null;
        if (lowLoadClock.HasValue && lowLoadClock.Value >= trustedClock!.Value)
            lowLoadClock = null;
        int? resumeClock =
            trustedClock.HasValue && learnedResumeClockMHz.HasValue
                ? catalog.SnapNearest(learnedResumeClockMHz.Value)
                : lowLoadClock;
        if (resumeClock.HasValue && resumeClock.Value > trustedClock!.Value)
            resumeClock = trustedClock;

        _currentCapMHz = null;
        _trustedClockMHz = trustedClock;
        _lowLoadClockMHz = lowLoadClock;
        _resumeClockMHz = resumeClock;
        _pendingInitialClockMHz = null;
        _resumeRampTargetMHz = null;
        _resumeValidationTargetMHz = null;
        _lastGoodMHz = null;
        _probePreviousClockMHz = null;
        _probeStartedUncapped = false;
        _probingLowLoad = false;
        _probeReferenceFps = 0;
        _probeReferenceP95FrameTime = 0;
        _probeReferenceP99FrameTime = 0;
        _probeReferenceUtilization = 0;
        _probeReferencePower = default;
        _powerReturnValidationPending = false;
        _powerCandidateClockMHz = null;
        _powerCandidateWindow = default;
        _powerValleyFloorMHz = null;
        _detectedFpsLimit = null;
        _fpsLockCandidate = null;
        _learningWindows = 0;
        _headroomWindows = 0;
        _probeCooldownWindows = 0;
        _fpsLockCandidateWindows = 0;
        _continuousFreshFramesSince = null;
        _previousFreshObservationAt = null;
        _requireLearningConfidence = requireLearningConfidence;
        _profilePersistenceAnnounced = false;
        _resumeConfidence = Math.Clamp(
            resumeConfidence,
            0,
            DccLearnedProfile.MaximumResumeConfidence);
        _resumeValidationPending = resumeClock.HasValue;
        _resumeValidationFailedThisSession = false;
        _clockChangeSensitivityScore = Math.Clamp(
            clockChangeSensitivityScore,
            0,
            MaximumClockChangeSensitivityScore);
        _cleanClockChangeAssessments = 0;
        _lastDriverChangeAt = null;
        _lastRejectedProbeClockMHz = null;
        _lastRejectedProbeAt = null;

        if (trustedClock.HasValue)
        {
            int initialClock = trustedClock.Value;
            if (resumeClock.HasValue)
            {
                int safetyMargin = learnedResumeClockMHz.HasValue
                    ? ResumeVerificationMarginMHz
                    : LegacyResumeSafetyMarginMHz;
                initialClock = Math.Min(
                    trustedClock.Value,
                    catalog.StepByMHz(resumeClock.Value, safetyMargin));
            }
            _currentCapMHz = initialClock;
            _lastGoodMHz = initialClock;
            _resumeValidationTargetMHz = resumeClock.HasValue ? initialClock : null;
            if (deferLearnedApplication)
            {
                _currentCapMHz = null;
                _lastGoodMHz = null;
                _pendingInitialClockMHz = initialClock;
            }
            else
            {
                _lastDriverChangeAt = now;
            }
            BeginWindow(DccGovernorPhase.VerifyLearned, now);
            return Decision(
                DccGovernorStatus.VerifyLearnedProfile,
                deferLearnedApplication ? null : initialClock);
        }

        BeginWindow(DccGovernorPhase.Baseline, now);
        return Decision(DccGovernorStatus.Baseline);
    }

    public DccGovernorDecision Stop()
    {
        bool hadCap = _currentCapMHz.HasValue;
        _samples.Clear();
        _currentCapMHz = null;
        _pendingInitialClockMHz = null;
        _resumeRampTargetMHz = null;
        _resumeValidationTargetMHz = null;
        _resumeValidationPending = false;
        _powerReturnValidationPending = false;
        _powerCandidateClockMHz = null;
        _powerCandidateWindow = default;
        _powerValleyFloorMHz = null;
        _cleanClockChangeAssessments = 0;
        _lastDriverChangeAt = null;
        _lastRejectedProbeClockMHz = null;
        _lastRejectedProbeAt = null;
        _continuousFreshFramesSince = null;
        _previousFreshObservationAt = null;
        Phase = DccGovernorPhase.Idle;
        return new DccGovernorDecision(
            Phase,
            DccGovernorStatus.WaitingForGame,
            ResetClock: hadCap);
    }

    public DccGovernorDecision Observe(DccGovernorSample sample)
    {
        if (_catalog is null || Phase == DccGovernorPhase.Idle)
            return Decision(DccGovernorStatus.WaitingForGame);
        if (sample.FramesPerSecond <= 0 ||
            !double.IsFinite(sample.FramesPerSecond) ||
            sample.FramesPerSecond > 10_000 ||
            !double.IsFinite(sample.GpuUtilizationPercent) ||
            sample.GpuUtilizationPercent is < 0 or > 100 ||
            sample.CurrentGraphicsClockMHz <= 0 ||
            !double.IsFinite(sample.P95FrameTimeMilliseconds) ||
            !double.IsFinite(sample.P99FrameTimeMilliseconds) ||
            !double.IsFinite(sample.BoardPowerWatts))
        {
            return Decision(DccGovernorStatus.InvalidFps);
        }
        TrackFreshObservation(sample.Timestamp);

        _samples.Add(sample);
        if (sample.Timestamp - _windowStarted < CurrentWindowDuration() || _samples.Count < 3)
            return Decision(StatusForCurrentPhase());

        double evaluatedFps = Percentile(_samples.Select(item => item.FramesPerSecond), 0.25);
        double averageUtilization = _samples.Average(item => item.GpuUtilizationPercent);
        double[] validP95FrameTimes = _samples
            .Select(item => item.P95FrameTimeMilliseconds)
            .Where(value => double.IsFinite(value) && value > 0)
            .ToArray();
        double evaluatedP95FrameTime = validP95FrameTimes.Length == 0
            ? 0
            : Percentile(validP95FrameTimes, 0.75);
        double peakP95FrameTime = validP95FrameTimes.Length == 0
            ? 0
            : validP95FrameTimes.Max();
        double[] validP99FrameTimes = _samples
            .Select(item => item.P99FrameTimeMilliseconds)
            .Where(value => double.IsFinite(value) && value > 0)
            .ToArray();
        double evaluatedP99FrameTime = validP99FrameTimes.Length == 0
            ? 0
            : Percentile(validP99FrameTimes, 0.75);
        double peakP99FrameTime = validP99FrameTimes.Length == 0
            ? 0
            : validP99FrameTimes.Max();
        PowerWindow evaluatedPower = MeasurePowerWindow(_samples);
        double lowFps = Percentile(_samples.Select(item => item.FramesPerSecond), 0.10);
        double highFps = Percentile(_samples.Select(item => item.FramesPerSecond), 0.90);
        bool stableFpsPlateau = highFps - lowFps <=
                                Math.Max(1, evaluatedFps * FpsLockWindowTolerance);
        int observedClock = _catalog.SnapNearest(_samples.Max(item => item.CurrentGraphicsClockMHz));
        _learningWindows++;
        TrackFpsLimit(evaluatedFps, stableFpsPlateau, Phase != DccGovernorPhase.EfficiencyProbe);

        DccGovernorDecision decision = Phase switch
        {
            DccGovernorPhase.Baseline => EvaluateBaseline(
                sample.Timestamp,
                evaluatedFps,
                evaluatedP95FrameTime,
                evaluatedP99FrameTime,
                averageUtilization,
                observedClock,
                evaluatedPower),
            DccGovernorPhase.VerifyLearned => EvaluateLearnedProfile(
                sample.Timestamp,
                evaluatedFps,
                evaluatedP95FrameTime,
                evaluatedP99FrameTime,
                averageUtilization,
                observedClock,
                stableFpsPlateau,
                evaluatedPower),
            DccGovernorPhase.Locked => EvaluateLocked(
                sample.Timestamp,
                evaluatedFps,
                evaluatedP95FrameTime,
                evaluatedP99FrameTime,
                averageUtilization,
                observedClock,
                evaluatedPower),
            DccGovernorPhase.EfficiencyProbe => EvaluateProbe(
                sample.Timestamp,
                evaluatedFps,
                evaluatedP95FrameTime,
                evaluatedP99FrameTime,
                peakP95FrameTime,
                peakP99FrameTime,
                averageUtilization,
                evaluatedPower),
            DccGovernorPhase.Recovery => EvaluateRecovery(
                sample.Timestamp,
                evaluatedFps,
                averageUtilization),
            _ => Decision(DccGovernorStatus.WaitingForGame, evaluatedFps: evaluatedFps)
        };
        decision = ApplyProfilePersistenceGate(decision);
        if (decision.ChangesDriverState)
            _lastDriverChangeAt = sample.Timestamp;
        return decision;
    }

    private DccGovernorDecision EvaluateBaseline(
        DateTimeOffset now,
        double fps,
        double p95FrameTime,
        double p99FrameTime,
        double utilization,
        int observedClock,
        PowerWindow power)
    {
        if (utilization > DesiredUtilizationCeiling)
        {
            BeginWindow(DccGovernorPhase.Locked, now);
            return Decision(DccGovernorStatus.Regulated, evaluatedFps: fps);
        }

        if (utilization >= HeadroomUtilizationCeiling)
            return ApplyObservedProfile(now, fps, observedClock);

        return BeginProbe(
            now,
            fps,
            p95FrameTime,
            p99FrameTime,
            utilization,
            power,
            observedClock,
            ProbeStepFor(utilization),
            lowLoadProbe: true);
    }

    private DccGovernorDecision EvaluateLearnedProfile(
        DateTimeOffset now,
        double fps,
        double p95FrameTime,
        double p99FrameTime,
        double utilization,
        int observedClock,
        bool stableFpsPlateau,
        PowerWindow power)
    {
        if (_pendingInitialClockMHz is int pendingClock)
        {
            if (!stableFpsPlateau)
            {
                BeginWindow(DccGovernorPhase.VerifyLearned, now);
                return Decision(DccGovernorStatus.VerifyLearnedProfile, evaluatedFps: fps);
            }

            int stagedClock = _catalog!.StepByMHz(
                observedClock,
                -ResumeRampStepForConfidence());
            int startingClock = Math.Max(pendingClock, stagedClock);
            startingClock = _catalog!.SnapNearest(startingClock);
            _pendingInitialClockMHz = null;
            _currentCapMHz = startingClock;
            _lastGoodMHz = startingClock;
            _resumeRampTargetMHz = startingClock > pendingClock ? pendingClock : null;
            BeginWindow(DccGovernorPhase.VerifyLearned, now);
            return Decision(DccGovernorStatus.VerifyLearnedProfile, startingClock, evaluatedFps: fps);
        }

        if (utilization > DesiredUtilizationCeiling)
            return RaiseForRecovery(now, fps);

        if (utilization < HeadroomUtilizationCeiling)
        {
            int startingClock = _currentCapMHz ?? _trustedClockMHz ?? _catalog!.MaximumMHz;
            int step = ResumeProbeStep(startingClock, utilization);
            return BeginProbe(
                now,
                fps,
                p95FrameTime,
                p99FrameTime,
                utilization,
                power,
                startingClock,
                step,
                lowLoadProbe: true);
        }

        BeginWindow(DccGovernorPhase.Locked, now);
        return Decision(DccGovernorStatus.ProfileAdjusted, evaluatedFps: fps);
    }

    private DccGovernorDecision EvaluateLocked(
        DateTimeOffset now,
        double fps,
        double p95FrameTime,
        double p99FrameTime,
        double utilization,
        int observedClock,
        PowerWindow power)
    {
        if (_currentCapMHz.HasValue && utilization > DesiredUtilizationCeiling)
            return RaiseForRecovery(now, fps);

        if (IsBelowDetectedFpsLimit(fps))
        {
            _headroomWindows = 0;
            BeginWindow(DccGovernorPhase.Locked, now);
            return Decision(DccGovernorStatus.Regulated, evaluatedFps: fps);
        }

        if (!_currentCapMHz.HasValue &&
            utilization is >= HeadroomUtilizationCeiling and <= DesiredUtilizationCeiling)
        {
            return ApplyObservedProfile(now, fps, observedClock);
        }

        if (_probeCooldownWindows > 0)
        {
            _probeCooldownWindows--;
            _headroomWindows = 0;
        }
        else if (_lastDriverChangeAt.HasValue &&
                 now - _lastDriverChangeAt.Value < MinimumProbeInterval(now, utilization))
        {
            _headroomWindows = 0;
        }
        else if (utilization < HeadroomUtilizationCeiling)
        {
            _headroomWindows++;
        }
        else
        {
            _headroomWindows = 0;
        }

        if (_headroomWindows >= SustainedHeadroomWindows)
        {
            int startingClock = _currentCapMHz ?? observedClock;
            int step = ResumeProbeStep(startingClock, utilization);
            _headroomWindows = 0;
            return BeginProbe(
                now,
                fps,
                p95FrameTime,
                p99FrameTime,
                utilization,
                power,
                startingClock,
                step,
                lowLoadProbe: true);
        }

        BeginWindow(DccGovernorPhase.Locked, now);
        return Decision(DccGovernorStatus.Regulated, evaluatedFps: fps);
    }

    private DccGovernorDecision EvaluateProbe(
        DateTimeOffset now,
        double fps,
        double p95FrameTime,
        double p99FrameTime,
        double peakP95FrameTime,
        double peakP99FrameTime,
        double utilization,
        PowerWindow power)
    {
        if (_powerReturnValidationPending)
        {
            return EvaluatePowerReturnValidation(
                now,
                fps,
                p95FrameTime,
                p99FrameTime,
                utilization,
                power);
        }

        bool fpsStable = fps >= _probeReferenceFps - Math.Max(1, _probeReferenceFps * FpsRegressionTolerance);
        bool detectedLimitStable = !IsBelowDetectedFpsLimit(fps);
        bool frameTimeStable = _probeReferenceP95FrameTime <= 0 ||
                               p95FrameTime <= 0 ||
                               p95FrameTime <= _probeReferenceP95FrameTime * (1 + P95RegressionTolerance) +
                                   P95AbsoluteToleranceMilliseconds;
        bool onePercentLowStable = _probeReferenceP99FrameTime <= 0 ||
                                   p99FrameTime <= 0 ||
                                   p99FrameTime <= _probeReferenceP99FrameTime * (1 + P99RegressionTolerance) +
                                       P99AbsoluteToleranceMilliseconds;
        bool utilizationSafe = utilization <= DesiredUtilizationCeiling;
        DccClockChangeAssessment assessment = AssessClockChange(
            fps,
            peakP95FrameTime,
            peakP99FrameTime,
            utilization);

        if (!fpsStable || !detectedLimitStable || !frameTimeStable || !onePercentLowStable || !utilizationSafe)
        {
            return RestoreAfterProbe(
                now,
                fps,
                utilization,
                DccGovernorStatus.StableClockRestored,
                assessment);
        }

        if (IsPowerRegression(_probeReferencePower, power, _probeReferenceFps))
        {
            return BeginPowerReturnValidation(now, fps, power) with
            {
                ClockChangeAssessment = assessment
            };
        }

        _lastGoodMHz = _currentCapMHz;
        if (_probingLowLoad)
        {
            if (!_trustedClockMHz.HasValue)
                _trustedClockMHz = _probePreviousClockMHz ?? _currentCapMHz;
            _lowLoadClockMHz = IsBelowTrustedClock() ? _currentCapMHz : null;
        }
        else
        {
            _trustedClockMHz = _currentCapMHz;
            if (_lowLoadClockMHz.HasValue &&
                _trustedClockMHz.HasValue &&
                _lowLoadClockMHz.Value >= _trustedClockMHz.Value)
            {
                _lowLoadClockMHz = null;
            }
        }
        _resumeClockMHz = _currentCapMHz;
        DccResumeValidation resumeValidation = DccResumeValidation.None;
        if (_resumeRampTargetMHz.HasValue &&
            _currentCapMHz.HasValue &&
            _currentCapMHz.Value <= _resumeRampTargetMHz.Value)
        {
            _resumeRampTargetMHz = null;
        }
        if (_resumeValidationPending &&
            _resumeValidationTargetMHz.HasValue &&
            _currentCapMHz.HasValue &&
            _currentCapMHz.Value <= _resumeValidationTargetMHz.Value)
        {
            _resumeConfidence = Math.Min(
                DccLearnedProfile.MaximumResumeConfidence,
                _resumeConfidence + 1);
            _resumeValidationPending = false;
            _resumeValidationTargetMHz = null;
            resumeValidation = DccResumeValidation.Succeeded;
        }

        _probePreviousClockMHz = null;
        _probeStartedUncapped = false;
        _probingLowLoad = false;
        _probeCooldownWindows = 0;
        BeginWindow(DccGovernorPhase.Locked, now);
        return new DccGovernorDecision(
            Phase,
            DccGovernorStatus.LowerClockConfirmed,
            PersistProfile: _trustedClockMHz.HasValue,
            EvaluatedFps: fps,
            ResumeValidation: resumeValidation,
            ClockChangeAssessment: assessment);
    }

    private DccGovernorDecision EvaluateRecovery(
        DateTimeOffset now,
        double fps,
        double utilization)
    {
        if (_currentCapMHz.HasValue && utilization > DesiredUtilizationCeiling)
            return RaiseForRecovery(now, fps);

        _trustedClockMHz = _currentCapMHz;
        if (_lowLoadClockMHz.HasValue &&
            _trustedClockMHz.HasValue &&
            _lowLoadClockMHz.Value >= _trustedClockMHz.Value)
        {
            _lowLoadClockMHz = null;
        }
        _resumeClockMHz = _currentCapMHz;
        _lastGoodMHz = _currentCapMHz;
        _probeCooldownWindows = 0;
        BeginWindow(DccGovernorPhase.Locked, now);
        return new DccGovernorDecision(
            Phase,
            DccGovernorStatus.ProfileAdjusted,
            PersistProfile: _currentCapMHz.HasValue,
            EvaluatedFps: fps);
    }

    private DccGovernorDecision BeginProbe(
        DateTimeOffset now,
        double fps,
        double p95FrameTime,
        double p99FrameTime,
        double utilization,
        PowerWindow power,
        int startingClock,
        int stepMHz,
        bool lowLoadProbe)
    {
        int snappedStartingClock = _catalog!.SnapNearest(startingClock);
        int candidate = _catalog.StepByMHz(snappedStartingClock, -stepMHz);
        if (lowLoadProbe &&
            _lowLoadClockMHz is int savedLowLoadClock &&
            savedLowLoadClock < snappedStartingClock &&
            savedLowLoadClock > candidate)
        {
            candidate = savedLowLoadClock;
        }
        if (lowLoadProbe && _powerValleyFloorMHz is int powerFloor)
        {
            if (snappedStartingClock <= powerFloor && candidate < powerFloor)
            {
                _currentCapMHz = snappedStartingClock;
                _lastGoodMHz = snappedStartingClock;
                _trustedClockMHz ??= snappedStartingClock;
                _lowLoadClockMHz = IsBelowTrustedClock() ? snappedStartingClock : null;
                _resumeClockMHz = snappedStartingClock;
                _probeCooldownWindows = PowerRegressionCooldownWindows;
                BeginWindow(DccGovernorPhase.Locked, now);
                return new DccGovernorDecision(
                    Phase,
                    DccGovernorStatus.Regulated,
                    PersistProfile: _trustedClockMHz.HasValue,
                    EvaluatedFps: fps);
            }

            if (candidate < powerFloor)
                candidate = powerFloor;
        }
        TimeSpan rejectedProbeInterval = IsClockChangeSensitive
            ? SensitiveRejectedProbeInterval
            : RejectedProbeInterval;
        if (_lastRejectedProbeClockMHz == candidate &&
            _lastRejectedProbeAt.HasValue &&
            now - _lastRejectedProbeAt.Value < rejectedProbeInterval)
        {
            _currentCapMHz = snappedStartingClock;
            _lastGoodMHz = snappedStartingClock;
            _headroomWindows = 0;
            BeginWindow(DccGovernorPhase.Locked, now);
            return Decision(DccGovernorStatus.Regulated, evaluatedFps: fps);
        }
        if (candidate >= snappedStartingClock)
        {
            _currentCapMHz = snappedStartingClock;
            _lastGoodMHz = snappedStartingClock;
            if (lowLoadProbe)
            {
                _trustedClockMHz ??= snappedStartingClock;
                _lowLoadClockMHz = IsBelowTrustedClock() ? snappedStartingClock : null;
            }
            else
            {
                _trustedClockMHz = snappedStartingClock;
            }
            BeginWindow(DccGovernorPhase.Locked, now);
            return new DccGovernorDecision(
                Phase,
                DccGovernorStatus.ProfileLearned,
                ApplyClockMHz: snappedStartingClock,
                PersistProfile: _trustedClockMHz.HasValue,
                EvaluatedFps: fps);
        }

        return BeginProbeAtClock(
            now,
            fps,
            p95FrameTime,
            p99FrameTime,
            utilization,
            power,
            snappedStartingClock,
            candidate,
            lowLoadProbe);
    }

    private DccGovernorDecision BeginProbeAtClock(
        DateTimeOffset now,
        double fps,
        double p95FrameTime,
        double p99FrameTime,
        double utilization,
        PowerWindow power,
        int startingClock,
        int candidateClock,
        bool lowLoadProbe)
    {
        int snappedStartingClock = _catalog!.SnapNearest(startingClock);
        int candidate = _catalog.SnapNearest(candidateClock);
        if (candidate >= snappedStartingClock)
        {
            return BeginProbe(
                now,
                fps,
                p95FrameTime,
                p99FrameTime,
                utilization,
                power,
                snappedStartingClock,
                FineProbeStepMHz,
                lowLoadProbe);
        }

        if (lowLoadProbe)
            _trustedClockMHz ??= snappedStartingClock;

        _probePreviousClockMHz = _currentCapMHz;
        _probeStartedUncapped = !_currentCapMHz.HasValue;
        if (lowLoadProbe && !_probePreviousClockMHz.HasValue && _trustedClockMHz.HasValue)
        {
            _probePreviousClockMHz = _trustedClockMHz;
            _probeStartedUncapped = false;
        }
        _probingLowLoad = lowLoadProbe;
        _probeReferenceFps = fps;
        _probeReferenceP95FrameTime = p95FrameTime;
        _probeReferenceP99FrameTime = p99FrameTime;
        _probeReferenceUtilization = utilization;
        _probeReferencePower = power;
        _currentCapMHz = candidate;
        BeginWindow(DccGovernorPhase.EfficiencyProbe, now);
        return Decision(DccGovernorStatus.RecheckingEfficiency, candidate, evaluatedFps: fps);
    }

    private DccGovernorDecision BeginPowerReturnValidation(
        DateTimeOffset now,
        double fps,
        PowerWindow candidatePower)
    {
        if (!_currentCapMHz.HasValue || !_probePreviousClockMHz.HasValue)
            return RestoreAfterProbe(now, fps, _probeReferenceUtilization, DccGovernorStatus.StableClockRestored);

        _powerCandidateClockMHz = _currentCapMHz;
        _powerCandidateWindow = candidatePower;
        _powerReturnValidationPending = true;
        _currentCapMHz = _catalog!.SnapNearest(_probePreviousClockMHz.Value);
        BeginWindow(DccGovernorPhase.EfficiencyProbe, now);
        return Decision(
            DccGovernorStatus.RecheckingEfficiency,
            _currentCapMHz,
            evaluatedFps: fps);
    }

    private DccGovernorDecision EvaluatePowerReturnValidation(
        DateTimeOffset now,
        double fps,
        double p95FrameTime,
        double p99FrameTime,
        double utilization,
        PowerWindow returnedPower)
    {
        bool comparableWorkload = MatchesProbeReference(
            fps,
            p95FrameTime,
            p99FrameTime,
            utilization,
            returnedPower);
        bool candidateWasLower = _powerCandidateClockMHz.HasValue &&
                                 _currentCapMHz.HasValue &&
                                 _powerCandidateClockMHz.Value < _currentCapMHz.Value;
        bool confirmedRegression = candidateWasLower &&
                                   comparableWorkload &&
                                   IsPowerRegression(returnedPower, _powerCandidateWindow, fps);
        bool wasLowLoadProbe = _probingLowLoad;

        if (confirmedRegression && _currentCapMHz.HasValue)
            _powerValleyFloorMHz = _currentCapMHz;

        _lastGoodMHz = _currentCapMHz;
        if (wasLowLoadProbe)
        {
            _trustedClockMHz ??= _currentCapMHz;
            _lowLoadClockMHz = IsBelowTrustedClock() ? _currentCapMHz : null;
            _resumeClockMHz = _currentCapMHz;
        }
        else
        {
            _trustedClockMHz = _currentCapMHz;
        }

        _resumeRampTargetMHz = null;
        if (_resumeValidationPending)
        {
            _resumeValidationPending = false;
            _resumeValidationTargetMHz = null;
        }

        _probePreviousClockMHz = null;
        _probeStartedUncapped = false;
        _probingLowLoad = false;
        _headroomWindows = 0;
        _probeCooldownWindows = confirmedRegression
            ? PowerRegressionCooldownWindows
            : FailedProbeCooldownWindows;
        ClearPowerReturnValidation();
        BeginWindow(DccGovernorPhase.Locked, now);
        return new DccGovernorDecision(
            Phase,
            DccGovernorStatus.StableClockRestored,
            PersistProfile: wasLowLoadProbe && _trustedClockMHz.HasValue,
            EvaluatedFps: fps);
    }

    private bool MatchesProbeReference(
        double fps,
        double p95FrameTime,
        double p99FrameTime,
        double utilization,
        PowerWindow returnedPower)
    {
        if (!_probeReferencePower.IsValid || !returnedPower.IsValid)
            return false;

        bool fpsMatches = ValuesMatch(
            _probeReferenceFps,
            fps,
            FpsRegressionTolerance,
            absoluteTolerance: 1);
        bool p95Matches = OptionalValuesMatch(
            _probeReferenceP95FrameTime,
            p95FrameTime,
            P95RegressionTolerance,
            P95AbsoluteToleranceMilliseconds);
        bool p99Matches = OptionalValuesMatch(
            _probeReferenceP99FrameTime,
            p99FrameTime,
            P99RegressionTolerance,
            P99AbsoluteToleranceMilliseconds);
        bool utilizationMatches = Math.Abs(_probeReferenceUtilization - utilization) <=
                                  Math.Max(
                                      PowerReturnUtilizationAbsoluteTolerance,
                                      Math.Max(_probeReferenceUtilization, utilization) *
                                      PowerReturnUtilizationTolerance);
        bool boardPowerMatches = ValuesMatch(
            _probeReferencePower.MedianWatts,
            returnedPower.MedianWatts,
            PowerReturnMatchTolerance,
            PowerReturnAbsoluteToleranceWatts);
        bool energyMatches = ValuesMatch(
            _probeReferencePower.MedianJoulesPerFrame,
            returnedPower.MedianJoulesPerFrame,
            PowerReturnMatchTolerance,
            PowerReturnAbsoluteToleranceWatts / Math.Max(1, _probeReferenceFps));
        return fpsMatches &&
               p95Matches &&
               p99Matches &&
               utilizationMatches &&
               boardPowerMatches &&
               energyMatches;
    }

    private static bool IsPowerRegression(
        PowerWindow reference,
        PowerWindow candidate,
        double referenceFps)
    {
        if (!reference.IsValid || !candidate.IsValid || referenceFps <= 0)
            return false;

        double boardPowerTolerance = Math.Max(
            PowerRegressionAbsoluteToleranceWatts,
            reference.MedianWatts * PowerRegressionTolerance);
        return candidate.MedianWatts > reference.MedianWatts + boardPowerTolerance &&
               candidate.MedianJoulesPerFrame >
               reference.MedianJoulesPerFrame * (1 + PowerRegressionTolerance);
    }

    private static bool ValuesMatch(
        double first,
        double second,
        double relativeTolerance,
        double absoluteTolerance) =>
        Math.Abs(first - second) <= Math.Max(
            absoluteTolerance,
            Math.Max(Math.Abs(first), Math.Abs(second)) * relativeTolerance);

    private static bool OptionalValuesMatch(
        double first,
        double second,
        double relativeTolerance,
        double absoluteTolerance)
    {
        if (first <= 0 && second <= 0)
            return true;
        if (first <= 0 || second <= 0)
            return false;
        return ValuesMatch(first, second, relativeTolerance, absoluteTolerance);
    }

    private void ClearPowerReturnValidation()
    {
        _powerReturnValidationPending = false;
        _powerCandidateClockMHz = null;
        _powerCandidateWindow = default;
    }

    private DccClockChangeAssessment AssessClockChange(
        double fps,
        double p95FrameTime,
        double p99FrameTime,
        double utilization)
    {
        int fromClock = _probePreviousClockMHz ?? 0;
        int toClock = _currentCapMHz ?? 0;
        int deltaMHz = Math.Abs(fromClock - toClock);
        bool hasFrameTimes =
            (_probeReferenceP95FrameTime > 0 && p95FrameTime > 0) ||
            (_probeReferenceP99FrameTime > 0 && p99FrameTime > 0);
        bool fpsComparable = _probeReferenceFps > 0 &&
                             Math.Abs(fps - _probeReferenceFps) <=
                             Math.Max(5, _probeReferenceFps * 0.20);
        bool utilizationComparable =
            Math.Abs(utilization - _probeReferenceUtilization) <=
            Math.Max(10, Math.Max(utilization, _probeReferenceUtilization) * 0.15);
        bool comparableWorkload = fpsComparable && utilizationComparable;
        bool p95Spike = IsClockChangeFrameTimeSpike(
            _probeReferenceP95FrameTime,
            p95FrameTime,
            ClockChangeP95SpikeTolerance,
            ClockChangeP95SpikeAbsoluteMilliseconds);
        bool p99Spike = IsClockChangeFrameTimeSpike(
            _probeReferenceP99FrameTime,
            p99FrameTime,
            ClockChangeP99SpikeTolerance,
            ClockChangeP99SpikeAbsoluteMilliseconds);
        bool eligible = fromClock > 0 &&
                        toClock > 0 &&
                        deltaMHz is > 0 and <= 90 &&
                        hasFrameTimes &&
                        comparableWorkload;
        bool correlatedSpike = eligible && (p95Spike || p99Spike);

        if (correlatedSpike)
        {
            _clockChangeSensitivityScore = Math.Min(
                MaximumClockChangeSensitivityScore,
                _clockChangeSensitivityScore + 1);
            _cleanClockChangeAssessments = 0;
        }
        else if (eligible && !IsClockChangeSensitive)
        {
            _cleanClockChangeAssessments++;
            if (_cleanClockChangeAssessments >= CleanClockChangesBeforeSensitivityDecay)
            {
                _clockChangeSensitivityScore = Math.Max(0, _clockChangeSensitivityScore - 1);
                _cleanClockChangeAssessments = 0;
            }
        }

        string reason = correlatedSpike
            ? "correlated-frametime-spike"
            : !hasFrameTimes
                ? "missing-frametime"
                : fromClock <= 0 || toClock <= 0
                    ? "uncapped-reference"
                    : deltaMHz > 90
                        ? "large-transition"
                        : !comparableWorkload
                            ? "workload-changed"
                            : "stable-transition";

        return new DccClockChangeAssessment(
            FromClockMHz: fromClock,
            ToClockMHz: toClock,
            ReferenceFps: _probeReferenceFps,
            ObservedFps: fps,
            ReferenceP95FrameTimeMilliseconds: _probeReferenceP95FrameTime,
            ObservedP95FrameTimeMilliseconds: p95FrameTime,
            ReferenceP99FrameTimeMilliseconds: _probeReferenceP99FrameTime,
            ObservedP99FrameTimeMilliseconds: p99FrameTime,
            ReferenceGpuUtilizationPercent: _probeReferenceUtilization,
            ObservedGpuUtilizationPercent: utilization,
            ComparableWorkload: comparableWorkload,
            CorrelatedFrameTimeSpike: correlatedSpike,
            SensitivityScore: _clockChangeSensitivityScore,
            IsSensitive: IsClockChangeSensitive,
            Reason: reason);
    }

    private static bool IsClockChangeFrameTimeSpike(
        double referenceMilliseconds,
        double observedMilliseconds,
        double relativeTolerance,
        double absoluteToleranceMilliseconds) =>
        referenceMilliseconds > 0 &&
        observedMilliseconds > referenceMilliseconds + Math.Max(
            absoluteToleranceMilliseconds,
            referenceMilliseconds * relativeTolerance);

    private DccGovernorDecision RestoreAfterProbe(
        DateTimeOffset now,
        double fps,
        double utilization,
        DccGovernorStatus status,
        DccClockChangeAssessment? assessment = null)
    {
        DccResumeValidation resumeValidation = FailResumeValidation();
        bool wasLowLoadProbe = _probingLowLoad;
        int? rejectedClock = _currentCapMHz;
        int? restoredClock = _probePreviousClockMHz;
        bool resetClock = _probeStartedUncapped || !restoredClock.HasValue;
        _currentCapMHz = resetClock ? null : _catalog!.SnapNearest(restoredClock!.Value);
        _lastGoodMHz = _currentCapMHz;
        if (wasLowLoadProbe)
            _lowLoadClockMHz = IsBelowTrustedClock() ? _currentCapMHz : null;
        if (wasLowLoadProbe &&
            _currentCapMHz.HasValue &&
            utilization <= DesiredUtilizationCeiling)
            _resumeClockMHz = _currentCapMHz;
        _resumeRampTargetMHz = null;
        _probePreviousClockMHz = null;
        _probeStartedUncapped = false;
        _probingLowLoad = false;
        ClearPowerReturnValidation();
        _headroomWindows = 0;
        _probeCooldownWindows = FailedProbeCooldownWindows;
        if (rejectedClock.HasValue &&
            restoredClock.HasValue &&
            rejectedClock.Value < restoredClock.Value)
        {
            _lastRejectedProbeClockMHz = rejectedClock;
            _lastRejectedProbeAt = now;
        }
        BeginWindow(DccGovernorPhase.Locked, now);
        return new DccGovernorDecision(
            Phase,
            status,
            ApplyClockMHz: resetClock ? null : _currentCapMHz,
            ResetClock: resetClock,
            PersistProfile: wasLowLoadProbe && _trustedClockMHz.HasValue,
            EvaluatedFps: fps,
            ResumeValidation: resumeValidation,
            ClockChangeAssessment: assessment);
    }

    private DccGovernorDecision RaiseForRecovery(DateTimeOffset now, double fps)
    {
        DccResumeValidation resumeValidation = FailResumeValidation();
        _headroomWindows = 0;
        _probeCooldownWindows = FailedProbeCooldownWindows;
        _probePreviousClockMHz = null;
        _probeStartedUncapped = false;
        _probingLowLoad = false;
        ClearPowerReturnValidation();
        _resumeRampTargetMHz = null;
        int startingClock = _currentCapMHz ?? _lastGoodMHz ?? _catalog!.MaximumMHz;
        int raised = _catalog!.StepByMHz(
            startingClock,
            Math.Max(RecoveryStepMHz, startingClock / 12));
        if (_trustedClockMHz.HasValue &&
            startingClock < _trustedClockMHz.Value &&
            raised > _trustedClockMHz.Value)
        {
            raised = _trustedClockMHz.Value;
        }

        if (raised <= startingClock && startingClock >= _catalog.MaximumMHz)
        {
            bool hadCap = _currentCapMHz.HasValue;
            _currentCapMHz = null;
            _trustedClockMHz = null;
            _lowLoadClockMHz = null;
            _resumeClockMHz = null;
            _lastGoodMHz = null;
            BeginWindow(DccGovernorPhase.Locked, now);
            return new DccGovernorDecision(
                Phase,
                DccGovernorStatus.IncreasingFpsHeadroom,
                ResetClock: hadCap,
                EvaluatedFps: fps,
                ResumeValidation: resumeValidation);
        }

        _currentCapMHz = raised;
        BeginWindow(DccGovernorPhase.Recovery, now);
        return Decision(DccGovernorStatus.IncreasingFpsHeadroom, raised, evaluatedFps: fps) with
        {
            ResumeValidation = resumeValidation
        };
    }

    private DccResumeValidation FailResumeValidation()
    {
        if (!_resumeValidationPending)
            return DccResumeValidation.None;

        _resumeValidationPending = false;
        _resumeValidationTargetMHz = null;
        _resumeValidationFailedThisSession = true;
        _resumeConfidence = Math.Max(0, _resumeConfidence - 1);
        return DccResumeValidation.Failed;
    }

    private DccGovernorDecision ApplyObservedProfile(
        DateTimeOffset now,
        double fps,
        int observedClock)
    {
        int safeClock = _catalog!.SnapNearest(observedClock);
        _currentCapMHz = safeClock;
        _trustedClockMHz = safeClock;
        _lowLoadClockMHz = null;
        _resumeClockMHz = safeClock;
        _resumeRampTargetMHz = null;
        _lastGoodMHz = safeClock;
        BeginWindow(DccGovernorPhase.Locked, now);
        return new DccGovernorDecision(
            Phase,
            DccGovernorStatus.ProfileLearned,
            ApplyClockMHz: safeClock,
            PersistProfile: true,
            EvaluatedFps: fps);
    }

    private bool IsBelowTrustedClock() =>
        _currentCapMHz.HasValue &&
        _trustedClockMHz.HasValue &&
        _currentCapMHz.Value < _trustedClockMHz.Value;

    private bool IsBelowDetectedFpsLimit(double fps) =>
        _detectedFpsLimit.HasValue &&
        fps < _detectedFpsLimit.Value -
            Math.Max(1, _detectedFpsLimit.Value * FpsRegressionTolerance);

    private static int ProbeStepFor(double utilization) => utilization switch
    {
        >= 85 => FineProbeStepMHz,
        >= 70 => MediumProbeStepMHz,
        _ => CoarseProbeStepMHz
    };

    private int ResumeProbeStep(int startingClock, double utilization)
    {
        if (_resumeRampTargetMHz is not int targetClock || targetClock >= startingClock)
            return ProbeStepFor(utilization);
        return Math.Min(ResumeRampStepForConfidence(), startingClock - targetClock);
    }

    private TimeSpan MinimumProbeInterval(DateTimeOffset now, double utilization)
    {
        if (_resumeRampTargetMHz.HasValue)
            return TimeSpan.Zero;
        if (IsClockChangeSensitive)
            return SensitiveMinimumProbeInterval;
        if (_lastRejectedProbeAt.HasValue &&
            now - _lastRejectedProbeAt.Value < RejectedProbeInterval)
        {
            return RejectedMinimumProbeInterval;
        }
        return utilization >= EfficiencyParkingUtilizationFloor
            ? EstablishedMinimumProbeInterval
            : TimeSpan.Zero;
    }

    private int ResumeRampStepForConfidence() => _resumeConfidence switch
    {
        >= 3 => ProvenResumeRampStepMHz,
        2 => ConfirmedResumeRampStepMHz,
        1 => LearnedResumeRampStepMHz,
        _ => UnconfirmedResumeRampStepMHz
    };

    private void TrackFpsLimit(double fps, bool stableWindow, bool allowNewCandidate)
    {
        if (!stableWindow || fps <= 0)
        {
            if (!_detectedFpsLimit.HasValue)
                ResetFpsLockCandidate();
            return;
        }

        if (_detectedFpsLimit.HasValue && FpsValuesMatch(fps, _detectedFpsLimit.Value))
        {
            _detectedFpsLimit = Math.Max(_detectedFpsLimit.Value, fps);
            ResetFpsLockCandidate();
            return;
        }

        if (_fpsLockCandidate.HasValue && FpsValuesMatch(fps, _fpsLockCandidate.Value))
        {
            _fpsLockCandidate = Math.Max(_fpsLockCandidate.Value, fps);
            _fpsLockCandidateWindows++;
        }
        else if (allowNewCandidate)
        {
            _fpsLockCandidate = fps;
            _fpsLockCandidateWindows = 1;
        }
        else
        {
            return;
        }

        if (_fpsLockCandidateWindows < FpsLockConfirmationWindows)
            return;

        _detectedFpsLimit = _fpsLockCandidate;
        ResetFpsLockCandidate();
    }

    private static bool FpsValuesMatch(double first, double second) =>
        Math.Abs(first - second) <= Math.Max(1, Math.Max(first, second) * FpsLockMatchTolerance);

    private void ResetFpsLockCandidate()
    {
        _fpsLockCandidate = null;
        _fpsLockCandidateWindows = 0;
    }

    private void TrackFreshObservation(DateTimeOffset timestamp)
    {
        if (!_previousFreshObservationAt.HasValue ||
            timestamp <= _previousFreshObservationAt.Value ||
            timestamp - _previousFreshObservationAt.Value > MaximumFreshObservationGap)
        {
            _continuousFreshFramesSince = timestamp;
        }
        _previousFreshObservationAt = timestamp;
    }

    private DccGovernorDecision ApplyProfilePersistenceGate(DccGovernorDecision decision)
    {
        if (!_requireLearningConfidence)
            return decision;
        if (!ProfilePersistenceReady)
            return decision.PersistProfile ? decision with { PersistProfile = false } : decision;
        if (!_profilePersistenceAnnounced && _trustedClockMHz.HasValue)
        {
            _profilePersistenceAnnounced = true;
            if (_resumeConfidence == 0 &&
                !_resumeValidationPending &&
                !_resumeValidationFailedThisSession)
            {
                _resumeConfidence = 1;
            }
            return decision with { PersistProfile = true };
        }
        return decision;
    }

    private void BeginWindow(DccGovernorPhase phase, DateTimeOffset now)
    {
        Phase = phase;
        _windowStarted = now;
        _samples.Clear();
    }

    private TimeSpan CurrentWindowDuration() => Phase switch
    {
        DccGovernorPhase.Baseline => BaselineWindow,
        DccGovernorPhase.VerifyLearned when _pendingInitialClockMHz.HasValue => ResumeWarmupWindow,
        DccGovernorPhase.Locked when _resumeRampTargetMHz.HasValue => EvaluationWindow,
        DccGovernorPhase.Locked => LockedWindow,
        DccGovernorPhase.EfficiencyProbe when _powerReturnValidationPending => PowerReturnValidationWindow,
        _ => EvaluationWindow
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
        DccGovernorPhase.Locked => DccGovernorStatus.Regulated,
        DccGovernorPhase.EfficiencyProbe => DccGovernorStatus.RecheckingEfficiency,
        DccGovernorPhase.Recovery => DccGovernorStatus.IncreasingFpsHeadroom,
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

    private static PowerWindow MeasurePowerWindow(IEnumerable<DccGovernorSample> samples)
    {
        (double Watts, double JoulesPerFrame)[] valid = samples
            .Where(sample =>
                double.IsFinite(sample.BoardPowerWatts) &&
                sample.BoardPowerWatts > 0 &&
                double.IsFinite(sample.FramesPerSecond) &&
                sample.FramesPerSecond > 0)
            .Select(sample => (
                sample.BoardPowerWatts,
                sample.BoardPowerWatts / sample.FramesPerSecond))
            .ToArray();
        if (valid.Length < 3)
            return default;

        return new PowerWindow(
            Percentile(valid.Select(item => item.Watts), 0.5),
            Percentile(valid.Select(item => item.JoulesPerFrame), 0.5));
    }

    private readonly record struct PowerWindow(
        double MedianWatts,
        double MedianJoulesPerFrame)
    {
        public bool IsValid =>
            double.IsFinite(MedianWatts) &&
            MedianWatts > 0 &&
            double.IsFinite(MedianJoulesPerFrame) &&
            MedianJoulesPerFrame > 0;
    }
}
