using System.Diagnostics;

namespace NvUv.Dcc.Core;

public static class DccMonotonicClock
{
    private static readonly DateTimeOffset OriginUtc = DateTimeOffset.UtcNow;
    private static readonly long OriginTimestamp = Stopwatch.GetTimestamp();

    public static DateTimeOffset Now =>
        OriginUtc + Stopwatch.GetElapsedTime(OriginTimestamp);
}


