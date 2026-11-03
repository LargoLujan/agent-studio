using System.Runtime.InteropServices;

namespace NodifySpike.Harness;

/// <summary>
/// Windows may let a program take the foreground window when it was started by the program that has it (a terminal or an
/// editor that launched it). WPF's keyboard focus is a Win32 <c>SetFocus</c> inside this process, which can make the harness
/// window the foreground window: the user's editor would lose the keyboard. So the harness asks first whether the current
/// foreground window belongs to one of its ancestors, and does not take the focus (while somebody is using the machine) if so.
/// </summary>
internal static class ProcessTree
{
    private const uint SnapshotProcesses = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    /// <summary>The ids of the parent, the grandparent and so on of the current process.</summary>
    public static HashSet<uint> Ancestors()
    {
        var parents = new Dictionary<uint, uint>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == -1)
        {
            return [];
        }

        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                parents[entry.ProcessId] = entry.ParentProcessId;
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var result = new HashSet<uint>();
        var current = (uint)Environment.ProcessId;
        while (parents.TryGetValue(current, out var parent) && parent != 0 && result.Add(parent))
        {
            current = parent;
        }

        return result;
    }

    /// <summary>True when <paramref name="window"/> belongs to an ancestor of this process (so this process may take the foreground from it).</summary>
    public static bool IsAncestorWindow(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        GetWindowThreadProcessId(window, out var processId);
        return Ancestors().Contains(processId);
    }
}
