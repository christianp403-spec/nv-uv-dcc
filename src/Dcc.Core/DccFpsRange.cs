namespace NvUv.Dcc.Core;

public static class DccFpsRange
{
    public const int Minimum = 20;
    public const int Maximum = 1000;
    public const int NvidiaEfficiencyFloor = 60;
    public const int NvidiaEfficiencyMaximum = 1000;

    public static int ResolveNvidiaEfficiencyTarget(
        int requestedFps,
        int displayRefreshHz,
        bool manualTarget)
    {
        int requested = Math.Clamp(
            requestedFps,
            NvidiaEfficiencyFloor,
            NvidiaEfficiencyMaximum);
        if (manualTarget)
            return requested;
        if (displayRefreshHz <= 1)
            return NvidiaEfficiencyFloor;

        return Math.Clamp(
            displayRefreshHz,
            NvidiaEfficiencyFloor,
            NvidiaEfficiencyMaximum);
    }
}
