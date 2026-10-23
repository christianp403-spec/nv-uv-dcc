using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using NvUv.Dcc.Core;

namespace NvUv.Dcc.Helper;

internal sealed class DccEtwFpsCollector : IDisposable
{
    // Public Windows ETW providers used by D3D9 and DXGI runtimes.
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3d9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int DxgiPresentStartId = 0x2A;
    private const int D3d9PresentStartId = 0x01;
    private const ulong PresentKeyword = 0x8000000000000002;
    private const double WindowMilliseconds = 1250;
    private const double MinimumSustainedStreamCoverageMilliseconds = 250;
    private const double SustainedStreamCoverageRatio = 0.75;
    private const double SelectedStreamFrameCountRatio = 0.80;
    private const int StreamPruneThreshold = 16;
    private const double StreamRetentionMilliseconds = 30_000;
    private const string SessionPrefix = "NVUV-DCC-Present-";
    private static readonly TimeSpan ProcessRetention = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<int, ProcessFrames> _processes = new();
    private readonly string _sessionName = $"{SessionPrefix}{Environment.ProcessId}";
    private TraceEventSession? _session;
    private Task? _processingTask;
    private Timer? _pruneTimer;
    private long _dxgiProviderEventCount;
    private long _d3d9ProviderEventCount;
    private Exception? _processingFailure;
    private bool _disposed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null)
            return;

        CleanupOrphanedSessions();
        DccLog.Helper("fps.etw.starting", $"session={_sessionName}");
        var session = new TraceEventSession(_sessionName)
        {
            StopOnDispose = true
        };
        try
        {
            // Subscribe before enabling the providers so the first Presents cannot race
            // the consumer registration. The public provider manifests expose the
            // swap-chain pointer used to keep parallel Present streams separate.
            session.Source.AllEvents += OnEvent;
            session.EnableProvider(
                DxgiProvider,
                TraceEventLevel.Verbose,
                PresentKeyword,
                new TraceEventProviderOptions { EventIDsToEnable = [DxgiPresentStartId] });
            session.EnableProvider(
                D3d9Provider,
                TraceEventLevel.Verbose,
                PresentKeyword,
                new TraceEventProviderOptions { EventIDsToEnable = [D3d9PresentStartId] });
        }
        catch
        {
            session.Dispose();
            throw;
        }
        _session = session;
        _processingTask = Task.Factory.StartNew(
            ProcessEvents,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        _pruneTimer = new Timer(
            static state => ((DccEtwFpsCollector)state!).PruneStaleProcesses(),
            this,
            PruneInterval,
            PruneInterval);
        DccLog.Helper("fps.etw.started", $"session={_sessionName}; providers=DXGI,D3D9");
    }

    public DccFpsSample Read(int processId)
    {
        if (_processingFailure is not null)
            return DccFpsSample.Unavailable(processId, "NV-UV ETW", _processingFailure.Message);
        if (_session is null)
            return DccFpsSample.Unavailable(processId, "NV-UV ETW", "The ETW collector is not running.");
        if (!_processes.TryGetValue(processId, out ProcessFrames? frames))
            return new DccFpsSample(
                true,
                processId,
                0,
                TimeSpan.MaxValue,
                0,
                "NV-UV ETW",
                BuildProviderSummary());

        return frames.Read(processId);
    }

    private void ProcessEvents()
    {
        try
        {
            _session!.Source.Process();
        }
        catch (Exception ex) when (_disposed || ex is OperationCanceledException)
        {
            // Expected when the helper closes its real-time trace session.
        }
        catch (Exception ex)
        {
            _processingFailure = ex;
            DccLog.Helper("fps.etw.processing_failed", exception: ex);
        }
    }

    private void OnEvent(TraceEvent data)
    {
        if (data.ProviderGuid == DxgiProvider || data.ProviderGuid == D3d9Provider)
        {
            if (data.ProviderGuid == DxgiProvider)
                Interlocked.Increment(ref _dxgiProviderEventCount);
            else
                Interlocked.Increment(ref _d3d9ProviderEventCount);
        }

        bool isDxgiPresent = data.ProviderGuid == DxgiProvider && (int)data.ID == DxgiPresentStartId;
        bool isD3d9Present = data.ProviderGuid == D3d9Provider && (int)data.ID == D3d9PresentStartId;
        if (!isDxgiPresent && !isD3d9Present)
            return;

        if (data.ProcessID <= 0)
            return;

        string runtime = isDxgiPresent ? "DXGI" : "D3D9";
        string swapChainPayload = isDxgiPresent ? "pIDXGISwapChain" : "pSwapchain";
        ulong swapChainId = ReadPointerPayload(data, swapChainPayload);
        _processes.GetOrAdd(data.ProcessID, static _ => new ProcessFrames())
            .Add(data.TimeStampRelativeMSec, Stopwatch.GetTimestamp(), runtime, swapChainId);
    }

    private static ulong ReadPointerPayload(TraceEvent data, string payloadName)
    {
        try
        {
            object? value = data.PayloadByName(payloadName);
            return value switch
            {
                ulong unsigned64 => unsigned64,
                long signed64 => unchecked((ulong)signed64),
                uint unsigned32 => unsigned32,
                int signed32 => unchecked((uint)signed32),
                UIntPtr unsignedPointer => unsignedPointer.ToUInt64(),
                IntPtr signedPointer => unchecked((ulong)signedPointer.ToInt64()),
                IConvertible convertible => convertible.ToUInt64(CultureInfo.InvariantCulture),
                _ => 0
            };
        }
        catch
        {
            // The public DXGI/D3D9 manifests normally expose the pointer fields
            // above. Keep a per-runtime fallback stream if metadata decoding is
            // unavailable on a particular Windows build.
            return 0;
        }
    }

    internal static DccFpsSample ReadSyntheticForSelfTest(
        int processId,
        IEnumerable<(string Runtime, ulong SwapChainId, double TimestampMilliseconds)> events)
    {
        var frames = new ProcessFrames();
        long receivedTimestamp = Stopwatch.GetTimestamp();
        foreach ((string runtime, ulong swapChainId, double timestampMilliseconds) in
                 events.OrderBy(static item => item.TimestampMilliseconds))
        {
            frames.Add(timestampMilliseconds, receivedTimestamp, runtime, swapChainId);
        }

        return frames.Read(processId);
    }

    private string BuildProviderSummary()
    {
        long dxgiEvents = Interlocked.Read(ref _dxgiProviderEventCount);
        long d3d9Events = Interlocked.Read(ref _d3d9ProviderEventCount);
        if (dxgiEvents == 0 && d3d9Events == 0)
            return "No DXGI or D3D9 events received.";

        string providers = $"DXGI:{DxgiPresentStartId}={dxgiEvents}, D3D9:{D3d9PresentStartId}={d3d9Events}";
        string processes = string.Join(
            ", ",
            _processes
                .SelectMany(static pair => pair.Value.SnapshotCounts(pair.Key))
                .OrderByDescending(static item => item.Count)
                .Take(8)
                .Select(static item => $"{item.Runtime}:pid={item.ProcessId}={item.Count}"));
        return string.IsNullOrEmpty(processes) ? providers : $"{providers}; {processes}";
    }

    private void PruneStaleProcesses()
    {
        try
        {
            long now = Stopwatch.GetTimestamp();
            int removed = 0;
            foreach ((int processId, ProcessFrames frames) in _processes)
            {
                if (!frames.IsStale(now, ProcessRetention) || IsProcessRunning(processId))
                    continue;
                if (_processes.TryRemove(processId, out _))
                    removed++;
            }

            if (removed > 0)
                DccLog.Helper("fps.etw.process_state_pruned", $"removed={removed}");
        }
        catch (Exception ex)
        {
            DccLog.Helper("fps.etw.process_state_prune_failed", exception: ex);
        }
    }

    private static void CleanupOrphanedSessions()
    {
        try
        {
            foreach (string sessionName in TraceEventSession.GetActiveSessionNames())
            {
                if (!TryGetOwnerProcessId(sessionName, out int processId) ||
                    processId == Environment.ProcessId ||
                    IsDccHelperRunning(processId))
                {
                    continue;
                }

                using TraceEventSession? staleSession = TraceEventSession.GetActiveSession(sessionName);
                if (staleSession?.Stop(noThrow: true) == true)
                    DccLog.Helper("fps.etw.orphan_stopped", $"session={sessionName}; pid={processId}");
            }
        }
        catch (Exception ex)
        {
            // Failure to enumerate an old session must not disable current FPS collection.
            DccLog.Helper("fps.etw.orphan_cleanup_failed", exception: ex);
        }
    }

    private static bool TryGetOwnerProcessId(string sessionName, out int processId)
    {
        processId = 0;
        return sessionName.StartsWith(SessionPrefix, StringComparison.Ordinal) &&
               int.TryParse(sessionName.AsSpan(SessionPrefix.Length), out processId) &&
               processId > 0;
    }

    private static bool IsDccHelperRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited &&
                   string.Equals(process.ProcessName, "Dcc.Helper", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            // If ownership cannot be proven either way, leave the session untouched.
            return true;
        }
    }

    private static bool IsProcessRunning(int processId)
    {
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
        catch
        {
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pruneTimer?.Dispose();
        _pruneTimer = null;

        TraceEventSession? session = _session;
        _session = null;
        if (session is not null)
        {
            try
            {
                session.Source.StopProcessing();
            }
            catch
            {
                // The session may already have stopped after a trace failure.
            }
            session.Dispose();
        }

        try
        {
            _processingTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Best effort during elevated helper shutdown.
        }
        DccLog.Helper("fps.etw.stopped", BuildProviderSummary());
    }

    private sealed class ProcessFrames
    {
        private readonly object _gate = new();
        private readonly Dictionary<PresentStreamKey, PresentStreamFrames> _streams = new();
        private PresentStreamKey? _selectedStream;
        private double _lastTimestampMilliseconds;
        private long _lastReceivedTimestamp;
        private long _dxgiCount;
        private long _d3d9Count;

        public void Add(
            double timestampMilliseconds,
            long receivedTimestamp,
            string runtime,
            ulong swapChainId)
        {
            lock (_gate)
            {
                if (runtime == "DXGI")
                    _dxgiCount++;
                else
                    _d3d9Count++;

                var key = new PresentStreamKey(runtime, swapChainId);
                if (!_streams.TryGetValue(key, out PresentStreamFrames? stream))
                {
                    stream = new PresentStreamFrames();
                    _streams.Add(key, stream);
                }

                if (!stream.Add(timestampMilliseconds, receivedTimestamp))
                    return;

                _lastTimestampMilliseconds = Math.Max(
                    _lastTimestampMilliseconds,
                    timestampMilliseconds);
                _lastReceivedTimestamp = Math.Max(_lastReceivedTimestamp, receivedTimestamp);
                PruneInactiveStreams(_lastTimestampMilliseconds);
            }
        }

        public DccFpsSample Read(int processId)
        {
            lock (_gate)
            {
                PresentStreamSnapshot[] candidates = _streams
                    .Select(pair => pair.Value.Snapshot(pair.Key, _lastTimestampMilliseconds))
                    .Where(static snapshot => snapshot is not null)
                    .Cast<PresentStreamSnapshot>()
                    .ToArray();
                if (candidates.Length == 0)
                    return DccFpsSample.NoFrames(processId, "NV-UV ETW");

                PresentStreamSnapshot selected = SelectStream(candidates);
                _selectedStream = selected.Key;
                long now = Stopwatch.GetTimestamp();
                TimeSpan age = TimeSpan.FromSeconds(
                    Math.Max(0, now - selected.LastReceivedTimestamp) /
                    (double)Stopwatch.Frequency);
                return new DccFpsSample(
                    true,
                    processId,
                    Math.Clamp(selected.FramesPerSecond, 0, 10_000),
                    age,
                    selected.FrameCount,
                    $"NV-UV ETW/{selected.Key.Runtime}",
                    P95FrameTimeMilliseconds: selected.P95FrameTimeMilliseconds,
                    P99FrameTimeMilliseconds: selected.P99FrameTimeMilliseconds);
            }
        }

        private PresentStreamSnapshot SelectStream(PresentStreamSnapshot[] candidates)
        {
            double maximumCoverage = candidates.Max(static item => item.CoverageMilliseconds);
            PresentStreamSnapshot[] sustained = maximumCoverage >=
                                                MinimumSustainedStreamCoverageMilliseconds
                ? candidates
                    .Where(item => item.CoverageMilliseconds >=
                                   maximumCoverage * SustainedStreamCoverageRatio)
                    .ToArray()
                : candidates;

            PresentStreamSnapshot best = sustained
                .OrderByDescending(static item => item.FrameCount)
                .ThenByDescending(static item => item.CoverageMilliseconds)
                .ThenByDescending(static item => item.LastTimestampMilliseconds)
                .ThenBy(static item => item.Key.Runtime, StringComparer.Ordinal)
                .ThenBy(static item => item.Key.SwapChainId)
                .First();

            if (_selectedStream is not PresentStreamKey selectedKey)
                return best;

            PresentStreamSnapshot? previous = sustained.FirstOrDefault(
                item => item.Key == selectedKey);
            if (previous is null ||
                previous.FrameCount < best.FrameCount * SelectedStreamFrameCountRatio ||
                previous.CoverageMilliseconds <
                best.CoverageMilliseconds * SustainedStreamCoverageRatio)
            {
                return best;
            }

            return previous;
        }

        public bool IsStale(long now, TimeSpan retention)
        {
            lock (_gate)
            {
                if (_lastReceivedTimestamp == 0)
                    return false;
                double ageSeconds = Math.Max(0, now - _lastReceivedTimestamp) /
                                    (double)Stopwatch.Frequency;
                return ageSeconds >= retention.TotalSeconds;
            }
        }

        public PresentProcessCount[] SnapshotCounts(int processId)
        {
            lock (_gate)
            {
                var counts = new List<PresentProcessCount>(2);
                if (_dxgiCount > 0)
                    counts.Add(new PresentProcessCount("DXGI", processId, _dxgiCount));
                if (_d3d9Count > 0)
                    counts.Add(new PresentProcessCount("D3D9", processId, _d3d9Count));
                return counts.ToArray();
            }
        }

        private void PruneInactiveStreams(double newestTimestampMilliseconds)
        {
            if (_streams.Count <= StreamPruneThreshold)
                return;

            PresentStreamKey[] stale = _streams
                .Where(pair => newestTimestampMilliseconds - pair.Value.LastTimestampMilliseconds >=
                               StreamRetentionMilliseconds)
                .Select(static pair => pair.Key)
                .ToArray();
            foreach (PresentStreamKey key in stale)
                _streams.Remove(key);

            if (_selectedStream is PresentStreamKey selected && !_streams.ContainsKey(selected))
                _selectedStream = null;
        }
    }

    private sealed class PresentStreamFrames
    {
        private readonly Queue<double> _timestamps = new();
        private double _lastTimestampMilliseconds;
        private long _lastReceivedTimestamp;

        public double LastTimestampMilliseconds => _lastTimestampMilliseconds;

        public bool Add(double timestampMilliseconds, long receivedTimestamp)
        {
            if (_timestamps.Count > 0 && timestampMilliseconds <= _lastTimestampMilliseconds)
                return false;

            _timestamps.Enqueue(timestampMilliseconds);
            _lastTimestampMilliseconds = timestampMilliseconds;
            _lastReceivedTimestamp = receivedTimestamp;
            Trim(timestampMilliseconds);
            return true;
        }

        public PresentStreamSnapshot? Snapshot(
            PresentStreamKey key,
            double newestProcessTimestampMilliseconds)
        {
            Trim(newestProcessTimestampMilliseconds);
            if (_timestamps.Count == 0)
                return null;

            double first = _timestamps.Peek();
            double last = _lastTimestampMilliseconds;
            double coverage = Math.Max(0, last - first);
            double fps = _timestamps.Count >= 2 && coverage > 0
                ? (_timestamps.Count - 1) * 1000d / coverage
                : 0;
            double[] timestamps = _timestamps.ToArray();
            double[] frameTimes = timestamps
                .Zip(timestamps.Skip(1), static (previous, current) => current - previous)
                .Where(static milliseconds => milliseconds is > 0.05 and < 1000)
                .OrderBy(static milliseconds => milliseconds)
                .ToArray();
            return new PresentStreamSnapshot(
                key,
                _timestamps.Count,
                fps,
                coverage,
                last,
                _lastReceivedTimestamp,
                Percentile(frameTimes, 0.95),
                Percentile(frameTimes, 0.99));
        }

        private void Trim(double newestTimestampMilliseconds)
        {
            double cutoff = newestTimestampMilliseconds - WindowMilliseconds;
            while (_timestamps.Count > 0 && _timestamps.Peek() < cutoff)
                _timestamps.Dequeue();
        }

        private static double Percentile(double[] sorted, double percentile)
        {
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

    private readonly record struct PresentStreamKey(string Runtime, ulong SwapChainId);

    private sealed record PresentStreamSnapshot(
        PresentStreamKey Key,
        int FrameCount,
        double FramesPerSecond,
        double CoverageMilliseconds,
        double LastTimestampMilliseconds,
        long LastReceivedTimestamp,
        double P95FrameTimeMilliseconds,
        double P99FrameTimeMilliseconds);

    private sealed record PresentProcessCount(string Runtime, int ProcessId, long Count);
}
