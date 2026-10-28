using Ninmagic.Nx.Core;
using Ninmagic.Nx.Windows;
using Microsoft.Win32.SafeHandles;

namespace Ninmagic.Nx.Tests;

public static class WindowsAdapterTests
{
    public static void Run()
    {
        var disk = new DiskInfo(2, "USB reader", "serial", "unique", 64L * 1024 * 1024 * 1024,
            512, "USB", false, false, false, "MBR", []);
        Check(WindowsDiskAccess.IsEligible(disk), "USB candidate");
        Check(WindowsDiskAccess.IsEligible(disk with { BusType = "SD" }), "SD candidate");
        Check(WindowsDiskAccess.IsEligible(disk with { BusType = "MMC" }), "MMC candidate");
        Check(WindowsDiskAccess.IsEligible(disk with { IsReadOnly = true }), "read-only source candidate");
        Check(!WindowsDiskAccess.IsEligible(disk with { IsBoot = true }), "boot disk rejected");
        Check(!WindowsDiskAccess.IsEligible(disk with { IsSystem = true }), "system disk rejected");
        foreach (string bus in new[] { "SATA", "NVMe", "RAID", "Spaces", "Virtual", "File Backed Virtual", "Unknown" })
            Check(!WindowsDiskAccess.IsEligible(disk with { BusType = bus }), $"{bus} rejected");
        Check(!WindowsDiskAccess.IsEligible(disk with { LogicalSectorSize = 4096 }), "4Kn rejected");
        Check(!WindowsDiskAccess.IsEligible(disk with { Size = 2L * 1024 * 1024 * 1024 * 1024 }), "MBR overflow rejected");
        Check(!WindowsDiskAccess.IsEligible(disk with { Size = disk.Size + 1 }), "unaligned size rejected");
        Check(!WindowsDiskAccess.IsEligible(disk with { SerialNumber = " ", UniqueId = "" }), "anonymous disk rejected");
        Check(!WindowsDiskAccess.IsEligible(disk with { Number = -1 }), "invalid number rejected");
        RawStreamHasNoReadAheadOrDeferredWrites();
        FailedStreamCloseStillReleasesAllLocks();
    }

    private static void RawStreamHasNoReadAheadOrDeferredWrites()
    {
        string path = Path.Combine(Path.GetTempPath(), "ninmagic-adapter-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            File.WriteAllBytes(path, new byte[1536]);
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            using var disk = new PhysicalDiskStream(handle, 1536, writable: true);
            byte[] sector = new byte[512];
            disk.ReadExactly(sector);

            // Change the next sector after the first read. Read-ahead would return stale zeros.
            byte[] replacement = Enumerable.Repeat((byte)0xA7, 512).ToArray();
            RandomAccess.Write(handle, replacement, 512);
            disk.ReadExactly(sector);
            Check(sector.SequenceEqual(replacement), "raw stream does not cache the next sector");

            disk.Write(replacement);
            Array.Clear(sector);
            int read = RandomAccess.Read(handle, sector, 1024);
            Check(read == 512 && sector.SequenceEqual(replacement), "raw sector writes reach the handle before return");
            disk.Position = 1024;
            Check(disk.Read(new byte[1024]) == 512 && disk.Position == 1536, "final read respects physical length");
            Check(disk.Read(new byte[512]) == 0, "read at physical end returns zero");
        }
        finally { File.Delete(path); }
    }

    private static void FailedStreamCloseStillReleasesAllLocks()
    {
        var expected = new IOException("Simulated stream close failure");
        var first = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        var second = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
        var locks = new List<SafeFileHandle> { first, second };
        Exception? actual = null;
        try { WindowsDiskAccess.CloseRawResources(new FailingDisposeStream(expected), locks); }
        catch (IOException error) { actual = error; }
        Check(ReferenceEquals(expected, actual), "stream close preserves its original failure");
        Check(first.IsClosed && second.IsClosed && locks.Count == 0, "all volume locks close after stream failure");
    }

    private sealed class FailingDisposeStream(IOException failure) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) throw failure;
        }
    }

    private static void Check(bool valid, string name)
    {
        if (!valid) throw new InvalidOperationException("Windows adapter test failed: " + name);
    }
}
