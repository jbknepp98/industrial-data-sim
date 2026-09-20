using System.Runtime.InteropServices;

namespace IndustrialDataSim.Runtime;

/// <summary>
/// Flush file naming metadata before SQLite may prune the source rows. Flushing
/// file contents alone does not durably publish a new directory entry on Unix.
/// Unsupported/failing filesystem operations refuse pruning rather than quietly
/// weakening the archival contract. Storage hardware must still honor flushes.
/// </summary>
internal static class ArchiveStorage
{
    internal static void Publish(string temporary, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            // No replacement or cross-volume copy: both names share a directory.
            // MOVEFILE_WRITE_THROUGH waits for the move to reach disk.
            if (!MoveFileEx(temporary, destination, 0x8)) throw Failure();
            return;
        }
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw Failure();
        File.Move(temporary, destination, overwrite: false);
        // Sync ancestors too: the archive directory itself may have just been
        // created. Its contents and its name in its parent both need persistence.
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(destination)!); directory is not null; directory = directory.Parent)
        {
            int descriptor = Open(directory.FullName, 0); // O_RDONLY
            if (descriptor < 0) throw Failure();
            try { if (Sync(descriptor) != 0) throw Failure(); }
            finally { Close(descriptor); }
        }
    }

    private static RuntimeFailure Failure() => new("archive.durability_failed",
        "The archive file or directory entry could not be flushed durably. Original audit rows remain. Use supported writable local storage, check permissions and free space, and preserve any incomplete export before retrying.");

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string destination, uint flags);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Sync(int descriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}
