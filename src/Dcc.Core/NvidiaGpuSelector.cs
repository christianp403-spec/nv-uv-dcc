namespace NvUv.Dcc.Core;

public static class NvidiaGpuSelector
{
    public static NvidiaGpuInfo SelectPrimary(
        IReadOnlyList<NvidiaGpuInfo> gpus,
        string? preferredUuid = null)
    {
        if (gpus.Count == 0)
            throw new ArgumentException("No NVIDIA GPU was supplied.", nameof(gpus));

        if (!string.IsNullOrWhiteSpace(preferredUuid))
        {
            NvidiaGpuInfo? preferred = gpus.FirstOrDefault(gpu =>
                string.Equals(gpu.Uuid, preferredUuid, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
                return preferred;
        }

        // A display-attached adapter is the least surprising desktop default.
        // Activity and power resolve multi-GPU systems without asking the user.
        return gpus
            .OrderByDescending(gpu => gpu.DisplayActive)
            .ThenByDescending(gpu => gpu.UtilizationPercent)
            .ThenByDescending(gpu => gpu.PowerWatts)
            .ThenBy(gpu => gpu.Index)
            .First();
    }
}
