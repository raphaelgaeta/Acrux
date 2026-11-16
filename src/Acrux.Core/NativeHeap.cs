using System.Runtime.InteropServices;

namespace Acrux;

/// <summary>
/// The C heap Polars allocates from, on Linux. glibc keeps freed memory in one
/// arena per thread instead of giving it back: with 32 threads and a 1 GB
/// cache, the peak RssAnon was 2.3 GB (CLAUDE.md, "O heap do Windows devolve a
/// memória; o glibc do Linux, não"). Two remedies: fewer arenas, and
/// malloc_trim, which returns the free memory to the system. Windows' heap
/// gives memory back by itself, and musl has no arenas to limit: on both, these
/// do nothing.
/// </summary>
public static class NativeHeap
{
    private const int ArenaMax = -8;   // M_ARENA_MAX in glibc's malloc.h

    /// <summary>
    /// Caps glibc's arenas, as MALLOC_ARENA_MAX would. Call it first thing in
    /// Main: an arena is made when a thread first allocates, and Polars'
    /// threads start with the first query.
    /// </summary>
    public static void LimitArenas(int arenas = 4)
    {
        if (!OperatingSystem.IsLinux()) return;
        try { mallopt(ArenaMax, arenas); }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
    }

    /// <summary>Gives the C heap's free memory back to the system (malloc_trim(0)).</summary>
    public static void Trim()
    {
        if (!OperatingSystem.IsLinux()) return;
        try { malloc_trim(0); }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
    }

    // DllImport, not LibraryImport: the signatures are plain ints, and the
    // core needs no unsafe code for them
    [DllImport("libc", EntryPoint = "mallopt")]
    private static extern int mallopt(int parameter, int value);

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int malloc_trim(nuint pad);
}
