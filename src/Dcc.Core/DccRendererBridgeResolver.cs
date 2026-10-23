using System.Runtime.InteropServices;

namespace NvUv.Dcc.Core;

public sealed record DccProcessTreeEntry(
    int ProcessId,
    int ParentProcessId,
    string Executable);

public sealed record DccRendererBridgeProcess(
    int ProcessId,
    string Executable);

public static class DccProcessTree
{
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static bool IsDescendantOf(
        DccProcessTreeEntry candidate,
        int gameProcessId,
        IReadOnlyDictionary<int, DccProcessTreeEntry> processById)
    {
        int parentProcessId = candidate.ParentProcessId;
        var visited = new HashSet<int> { candidate.ProcessId };
        for (int depth = 0; depth < 32 && parentProcessId > 0; depth++)
        {
            if (parentProcessId == gameProcessId)
                return true;
            if (!visited.Add(parentProcessId) ||
                !processById.TryGetValue(parentProcessId, out DccProcessTreeEntry? parent))
            {
                return false;
            }
            parentProcessId = parent.ParentProcessId;
        }
        return false;
    }

    public static DccProcessTreeEntry[] Capture()
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
            return [];

        try
        {
            var entries = new List<DccProcessTreeEntry>();
            var native = new ProcessEntry32
            {
                Size = checked((uint)Marshal.SizeOf<ProcessEntry32>())
            };
            if (!Process32First(snapshot, ref native))
                return [];

            do
            {
                if (native.ProcessId is > 0 and <= int.MaxValue &&
                    native.ParentProcessId <= int.MaxValue)
                {
                    entries.Add(new DccProcessTreeEntry(
                        checked((int)native.ProcessId),
                        checked((int)native.ParentProcessId),
                        native.ExecutableFile ?? string.Empty));
                }
                native.Size = checked((uint)Marshal.SizeOf<ProcessEntry32>());
            }
            while (Process32Next(snapshot, ref native));

            return entries.ToArray();
        }
        catch
        {
            return [];
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

public static class DccRendererBridgeResolver
{
    public const string NvidiaRtxRemixBridgeExecutable = "nvremixbridge.exe";

    public static IReadOnlyList<DccRendererBridgeProcess> FindForGameProcess(int gameProcessId)
    {
        if (gameProcessId <= 0)
            return [];

        return FindForGameProcess(gameProcessId, DccProcessTree.Capture())
            .Select(entry => new DccRendererBridgeProcess(entry.ProcessId, entry.Executable))
            .ToArray();
    }

    public static IReadOnlyList<DccProcessTreeEntry> FindForGameProcess(
        int gameProcessId,
        IEnumerable<DccProcessTreeEntry> processes)
    {
        if (gameProcessId <= 0)
            return [];

        Dictionary<int, DccProcessTreeEntry> processById = processes
            .Where(entry => entry.ProcessId > 0)
            .GroupBy(entry => entry.ProcessId)
            .Select(group => group.First())
            .ToDictionary(entry => entry.ProcessId);

        if (!processById.ContainsKey(gameProcessId))
            return [];

        return processById.Values
            .Where(entry =>
                string.Equals(
                    DccGameCatalog.NormalizeExecutable(entry.Executable),
                    NvidiaRtxRemixBridgeExecutable,
                    StringComparison.OrdinalIgnoreCase) &&
                DccProcessTree.IsDescendantOf(entry, gameProcessId, processById))
            .OrderBy(entry => entry.ProcessId)
            .ToArray();
    }
}
