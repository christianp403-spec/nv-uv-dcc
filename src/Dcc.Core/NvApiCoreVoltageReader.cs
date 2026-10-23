using System.Globalization;
using System.Runtime.InteropServices;

namespace NvUv.Dcc.Core;

/// <summary>
/// Best-effort, read-only access to the current NVIDIA GPU core voltage.
/// The voltage interface is not part of NVIDIA's public NVAPI contract, so
/// every unsupported, ambiguous, or implausible result is treated as absent.
/// </summary>
internal sealed class NvApiCoreVoltageReader
{
    private const int MaximumPhysicalGpus = 64;
    private const int NvApiOk = 0;
    private const int NvApiHandleInvalidated = -10;
    private const uint NvApiInitializeId = 0x0150E828;
    private const uint NvApiUnloadId = 0xD22BDD7E;
    private const uint NvApiEnumPhysicalGpusId = 0xE5AC921F;
    private const uint NvApiGpuGetBusIdId = 0x1BE0B8E5;
    private const uint NvApiGpuGetBusSlotIdId = 0x2A0A350F;
    private const uint NvApiGpuClientVoltRailsGetStatusId = 0x465F9BCF;
    private const uint VoltRailsStatusVersion1 = 0x0001004C;
    private const double MinimumPlausibleCoreVoltageMillivolts = 100;
    private const double MaximumPlausibleCoreVoltageMillivolts = 2_000;

    private readonly object _queryGate = new();
    private readonly Lazy<Capability?> _capability;

    private NvApiCoreVoltageReader()
    {
        _capability = new Lazy<Capability?>(CreateCapability, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static NvApiCoreVoltageReader Shared { get; } = new();

    public double? TryReadCoreVoltageMillivolts(string pciBusId)
    {
        if (!TryParsePciLocation(pciBusId, out PciLocation location) ||
            location.Domain != 0 ||
            location.Function != 0)
        {
            return null;
        }

        try
        {
            Capability? capability = _capability.Value;
            if (capability is null)
                return null;

            lock (_queryGate)
            {
                var matchedLocation = new PciLocation(0, location.Bus, location.Device, 0);
                if (!capability.TryGetHandle(matchedLocation, out nint gpuHandle))
                    return null;

                NvGpuClientVoltRailsStatus status = CreateVoltRailsStatus();
                int readStatus = capability.ReadVoltRailsStatus(gpuHandle, ref status);
                if (readStatus == NvApiHandleInvalidated)
                {
                    if (!capability.TryRefreshHandles() ||
                        !capability.TryGetHandle(matchedLocation, out gpuHandle))
                    {
                        return null;
                    }

                    status = CreateVoltRailsStatus();
                    readStatus = capability.ReadVoltRailsStatus(gpuHandle, ref status);
                }

                if (readStatus != NvApiOk ||
                    status.Version != VoltRailsStatusVersion1 ||
                    status.CoreMicrovoltsHigh != 0)
                {
                    return null;
                }

                double millivolts = status.CoreMicrovolts / 1_000d;
                return double.IsFinite(millivolts) &&
                       millivolts is >= MinimumPlausibleCoreVoltageMillivolts and <= MaximumPlausibleCoreVoltageMillivolts
                    ? millivolts
                    : null;
            }
        }
        catch
        {
            // Optional telemetry must never affect discovery or GPU control.
            return null;
        }
    }

    private Capability? CreateCapability()
    {
        UnloadDelegate? unload = null;
        bool initialized = false;
        try
        {
            InitializeDelegate? initialize = GetDelegate<InitializeDelegate>(NvApiInitializeId);
            unload = GetDelegate<UnloadDelegate>(NvApiUnloadId);
            EnumPhysicalGpusDelegate? enumeratePhysicalGpus = GetDelegate<EnumPhysicalGpusDelegate>(NvApiEnumPhysicalGpusId);
            GetBusIdDelegate? getBusId = GetDelegate<GetBusIdDelegate>(NvApiGpuGetBusIdId);
            GetBusSlotIdDelegate? getBusSlotId = GetDelegate<GetBusSlotIdDelegate>(NvApiGpuGetBusSlotIdId);
            ReadVoltRailsStatusDelegate? readVoltRailsStatus =
                GetDelegate<ReadVoltRailsStatusDelegate>(NvApiGpuClientVoltRailsGetStatusId);
            if (initialize is null ||
                unload is null ||
                enumeratePhysicalGpus is null ||
                getBusId is null ||
                getBusSlotId is null ||
                readVoltRailsStatus is null ||
                initialize() != NvApiOk)
            {
                return null;
            }
            initialized = true;

            Dictionary<PciLocation, nint>? mappedHandles = TryBuildHandleMap(
                enumeratePhysicalGpus,
                getBusId,
                getBusSlotId);
            if (mappedHandles is null)
            {
                TryUnload(unload);
                return null;
            }

            var capability = new Capability(
                mappedHandles,
                enumeratePhysicalGpus,
                getBusId,
                getBusSlotId,
                readVoltRailsStatus,
                unload);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => UnloadAtProcessExit(capability);
            return capability;
        }
        catch
        {
            if (initialized)
                TryUnload(unload);
            return null;
        }
    }

    private static Dictionary<PciLocation, nint>? TryBuildHandleMap(
        EnumPhysicalGpusDelegate enumeratePhysicalGpus,
        GetBusIdDelegate getBusId,
        GetBusSlotIdDelegate getBusSlotId)
    {
        var handles = new nint[MaximumPhysicalGpus];
        if (enumeratePhysicalGpus(handles, out uint count) != NvApiOk || count is 0 or > MaximumPhysicalGpus)
            return null;

        var mappedHandles = new Dictionary<PciLocation, nint>();
        var ambiguousLocations = new HashSet<PciLocation>();
        for (uint index = 0; index < count; index++)
        {
            nint handle = handles[index];
            if (handle == nint.Zero ||
                getBusId(handle, out uint bus) != NvApiOk ||
                getBusSlotId(handle, out uint device) != NvApiOk ||
                bus > byte.MaxValue ||
                device > 31)
            {
                continue;
            }

            var location = new PciLocation(0, bus, device, 0);
            if (!mappedHandles.TryAdd(location, handle))
            {
                mappedHandles.Remove(location);
                ambiguousLocations.Add(location);
            }
        }

        foreach (PciLocation ambiguous in ambiguousLocations)
            mappedHandles.Remove(ambiguous);

        return mappedHandles.Count == 0 ? null : mappedHandles;
    }

    private static NvGpuClientVoltRailsStatus CreateVoltRailsStatus() => new()
    {
        Version = VoltRailsStatusVersion1
    };

    private void UnloadAtProcessExit(Capability capability)
    {
        try
        {
            lock (_queryGate)
                capability.Unload();
        }
        catch
        {
            // Process teardown releases remaining native resources.
        }
    }

    private static void TryUnload(UnloadDelegate? unload)
    {
        try
        {
            _ = unload?.Invoke();
        }
        catch
        {
            // Best effort rollback after optional telemetry initialization.
        }
    }

    private static T? GetDelegate<T>(uint interfaceId) where T : Delegate
    {
        nint pointer = NvApiQueryInterface(interfaceId);
        return pointer == nint.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private static bool TryParsePciLocation(string value, out PciLocation location)
    {
        location = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string[] addressParts = value.Trim().Split(':', StringSplitOptions.TrimEntries);
        if (addressParts.Length is not (2 or 3))
            return false;

        string domainToken = addressParts.Length == 3 ? addressParts[0] : "0";
        string busToken = addressParts[^2];
        string[] deviceParts = addressParts[^1].Split('.', StringSplitOptions.TrimEntries);
        if (deviceParts.Length != 2 ||
            !uint.TryParse(domainToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint domain) ||
            !uint.TryParse(busToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bus) ||
            !uint.TryParse(deviceParts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint device) ||
            !uint.TryParse(deviceParts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint function) ||
            bus > byte.MaxValue ||
            device > 31 ||
            function > 7)
        {
            return false;
        }

        location = new PciLocation(domain, bus, device, function);
        return true;
    }

    // The read-only voltage ABI below is intentionally isolated because NVIDIA
    // does not publish it. Layout reference: LibreHardwareMonitor's NvApi.cs,
    // github.com/LibreHardwareMonitor/LibreHardwareMonitor, NvGpuClientVoltRailsStatus v1.
    // No NVIDIA or third-party binary is bundled.
    [StructLayout(LayoutKind.Explicit, Size = 0x4C)]
    private struct NvGpuClientVoltRailsStatus
    {
        [FieldOffset(0x00)] public uint Version;
        [FieldOffset(0x28)] public uint CoreMicrovolts;
        [FieldOffset(0x2C)] public uint CoreMicrovoltsHigh;
    }

    private sealed class Capability(
        Dictionary<PciLocation, nint> gpuHandles,
        EnumPhysicalGpusDelegate enumeratePhysicalGpus,
        GetBusIdDelegate getBusId,
        GetBusSlotIdDelegate getBusSlotId,
        ReadVoltRailsStatusDelegate readVoltRailsStatus,
        UnloadDelegate unload)
    {
        private Dictionary<PciLocation, nint> _gpuHandles = gpuHandles;
        private int _unloaded;

        public ReadVoltRailsStatusDelegate ReadVoltRailsStatus { get; } = readVoltRailsStatus;

        public bool TryGetHandle(PciLocation location, out nint gpuHandle)
        {
            if (Volatile.Read(ref _unloaded) != 0)
            {
                gpuHandle = nint.Zero;
                return false;
            }
            return _gpuHandles.TryGetValue(location, out gpuHandle);
        }

        public bool TryRefreshHandles()
        {
            if (Volatile.Read(ref _unloaded) != 0)
                return false;
            Dictionary<PciLocation, nint>? refreshed = TryBuildHandleMap(
                enumeratePhysicalGpus,
                getBusId,
                getBusSlotId);
            if (refreshed is null)
                return false;
            _gpuHandles = refreshed;
            return true;
        }

        public void Unload()
        {
            if (Interlocked.Exchange(ref _unloaded, 1) == 0)
                _ = unload();
        }
    }

    private readonly record struct PciLocation(uint Domain, uint Bus, uint Device, uint Function);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint NvApiQueryInterface(uint interfaceId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UnloadDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGpusDelegate([Out] nint[] gpuHandles, out uint gpuCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBusIdDelegate(nint gpuHandle, out uint busId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBusSlotIdDelegate(nint gpuHandle, out uint busSlotId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadVoltRailsStatusDelegate(
        nint gpuHandle,
        ref NvGpuClientVoltRailsStatus status);
}
