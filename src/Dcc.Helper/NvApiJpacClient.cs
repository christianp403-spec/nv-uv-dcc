using System.Runtime.InteropServices;
using NvUv.Dcc.Core;

namespace NvUv.Dcc.Helper;

/// <summary>
/// Minimal wrapper for NVIDIA's undocumented driver-internal JPAC
/// (Max Perf/Watt) control. Availability is compatibility-gated at runtime;
/// no NVIDIA binary or SDK component is redistributed by DCC.
/// </summary>
internal sealed class NvApiJpacClient : IDisposable
{
    private const uint NvApiInitializeId = 0x0150E828;
    private const uint NvApiUnloadId = 0xD22BDD7E;
    private const uint NvApiJpacSetControlId = 0xD2561B69;
    private const int ControlSize = 0x4C8;
    private const int ControlVersion = 0x000104C8;

    private readonly NvApiUnloadDelegate _unload;
    private readonly NvApiJpacSetControlDelegate _setControl;
    private bool _disposed;

    public NvApiJpacClient()
    {
        IntPtr initializePointer = NvApiQueryInterface(NvApiInitializeId);
        IntPtr unloadPointer = NvApiQueryInterface(NvApiUnloadId);
        IntPtr setControlPointer = NvApiQueryInterface(NvApiJpacSetControlId);
        if (initializePointer == IntPtr.Zero || unloadPointer == IntPtr.Zero || setControlPointer == IntPtr.Zero)
            throw new NotSupportedException("The installed NVIDIA driver does not expose Ultra Efficiency control.");

        var initialize = Marshal.GetDelegateForFunctionPointer<NvApiInitializeDelegate>(initializePointer);
        _unload = Marshal.GetDelegateForFunctionPointer<NvApiUnloadDelegate>(unloadPointer);
        _setControl = Marshal.GetDelegateForFunctionPointer<NvApiJpacSetControlDelegate>(setControlPointer);
        EnsureSuccess(initialize(), "initialize NVAPI");
    }

    public void SetEnabled(bool enabled, int targetFps = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (enabled && targetFps is < DccFpsRange.NvidiaEfficiencyFloor or > DccFpsRange.NvidiaEfficiencyMaximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetFps),
                $"The NVIDIA efficiency target must be between {DccFpsRange.NvidiaEfficiencyFloor} and {DccFpsRange.NvidiaEfficiencyMaximum} FPS.");
        }

        IntPtr control = Marshal.AllocHGlobal(ControlSize);
        try
        {
            byte[] zero = new byte[ControlSize];
            Marshal.Copy(zero, 0, control, ControlSize);
            Marshal.WriteInt32(control, 0x00, ControlVersion);
            Marshal.WriteInt32(control, 0x04, enabled ? 2 : 1);
            Marshal.WriteInt32(control, 0x48, 0x0F);
            Marshal.WriteByte(control, 0x4C, enabled ? (byte)1 : (byte)0);
            Marshal.WriteInt32(control, 0x6C, 0x10);
            Marshal.WriteInt32(control, 0x70, enabled ? targetFps : 0);
            EnsureSuccess(_setControl(control), enabled ? "enable NVIDIA driver efficiency" : "disable NVIDIA driver efficiency");
        }
        finally
        {
            Marshal.FreeHGlobal(control);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ = _unload();
    }

    private static void EnsureSuccess(int status, string operation)
    {
        if (status != 0)
            throw new InvalidOperationException($"NVAPI could not {operation} (status {status}).");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiInitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiUnloadDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiJpacSetControlDelegate(IntPtr control);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr NvApiQueryInterface(uint interfaceId);
}
