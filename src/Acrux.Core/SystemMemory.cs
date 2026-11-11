using System.Globalization;
using System.Runtime.InteropServices;

namespace Acrux;

/// <summary>
/// The physical memory the system can hand out right now: what Task Manager
/// calls "Available" (free memory plus the file cache it drops on demand), and
/// MemAvailable on Linux. Not the commit limit (RAM plus page file): long
/// before reaching it, the machine pages and stalls.
/// </summary>
public static class SystemMemory
{
    public static long Available()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
                return (long)status.AvailPhys;
        }
        else if (OperatingSystem.IsLinux())
        {
            // ignores a cgroup's memory limit: fine on a desktop, not in a container
            foreach (var line in File.ReadLines("/proc/meminfo"))
                if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;
        }

        // elsewhere, the GC's view: the machine's total less its load at the last GC
        var gc = GC.GetGCMemoryInfo();
        return Math.Max(0, gc.TotalAvailableMemoryBytes - gc.MemoryLoadBytes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
