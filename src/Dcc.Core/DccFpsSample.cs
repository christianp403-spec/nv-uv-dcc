namespace NvUv.Dcc.Core;

public sealed record DccFpsSample(
    bool CollectorAvailable,
    int ProcessId,
    double FramesPerSecond,
    TimeSpan SampleAge,
    int FrameCount,
    string Source,
    string Error = "",
    double P95FrameTimeMilliseconds = 0,
    double P99FrameTimeMilliseconds = 0)
{
    public bool IsFresh => CollectorAvailable &&
                           ProcessId > 0 &&
                           double.IsFinite(FramesPerSecond) &&
                           FramesPerSecond > 0 &&
                           double.IsFinite(P95FrameTimeMilliseconds) &&
                           double.IsFinite(P99FrameTimeMilliseconds) &&
                           SampleAge <= TimeSpan.FromSeconds(3);

    public static DccFpsSample NoFrames(int processId, string source) =>
        new(true, processId, 0, TimeSpan.MaxValue, 0, source);

    public static DccFpsSample Unavailable(int processId, string source, string error) =>
        new(false, processId, 0, TimeSpan.MaxValue, 0, source, error);
}
