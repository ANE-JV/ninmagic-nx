using Ninmagic.Nx.Core;
using Ninmagic.Nx.Windows;
using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;

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
        RawReadsRespectStrictSectorDevices();
        LayoutInspectionWorksThroughStrictSectorDevice();
        RawWritesNeverModifyNeighbouringSectors();
        FailedStreamCloseStillReleasesAllLocks();
    }

    private static void RawReadsRespectStrictSectorDevices()
    {
        byte[] content = Enumerable.Range(0, 2048).Select(i => (byte)(i * 17 + i / 512)).ToArray();
        using var device = new StrictSectorDevice(new MemoryStream(content, writable: true), content.Length);
        using var disk = new PhysicalDiskStream(device, content.Length, writable: false);
        foreach (var (offset, size) in new[] { (512, 8), (511, 4), (513, 136), (1024, 128), (1, 1536), (2047, 1), (1536, 1024) })
        {
            disk.Position = offset;
            byte[] actual = new byte[size];
            int count = disk.Read(actual);
            int expected = Math.Min(size, content.Length - offset);
            Check(count == expected && actual.AsSpan(0, count).SequenceEqual(content.AsSpan(offset, expected)), "sector bounce read contents");
            Check(disk.Position == offset + expected, "logical position follows bytes requested, not sector read");
        }
        disk.Position = 512;
        byte[] before = new byte[8]; disk.ReadExactly(before);
        content[512] ^= 0xFF;
        disk.Position = 512;
        byte[] after = new byte[8]; disk.ReadExactly(after);
        Check(after[0] == content[512] && after[0] != before[0], "partial reads do not cache an earlier sector");
        foreach (var (offset, size) in new[] { (511, 4), (513, 136), (1, 1536), (2047, 4) })
        {
            disk.Position = offset;
            byte[] actual = new byte[size];
            int count = disk.ReadAsync(actual.AsMemory()).AsTask().GetAwaiter().GetResult();
            int expected = Math.Min(size, content.Length - offset);
            Check(count == expected && actual.AsSpan(0, count).SequenceEqual(content.AsSpan(offset, expected)), "async sector bounce read contents");
        }
        device.MaxRead = 512;
        disk.Position = 0;
        byte[] all = new byte[content.Length]; disk.ReadExactly(all);
        Check(all.SequenceEqual(content), "short aligned device reads are completed without unaligned retries");
        int reads = device.Reads;
        Check(disk.Read(new byte[8]) == 0, "EOF returns zero");
        Check(disk.ReadAsync(new byte[8].AsMemory()).AsTask().GetAwaiter().GetResult() == 0, "async EOF returns zero");
        Check(device.Reads == reads, "EOF never reaches native device or asks for file length");
        disk.Position = 0;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Expect<OperationCanceledException>(() => disk.ReadAsync(new byte[8].AsMemory(), cancel.Token).AsTask().GetAwaiter().GetResult());
        Check(device.Reads == reads && disk.Position == 0, "cancelled read leaves device and position untouched");
        device.ReturnTruncatedSector = true;
        Expect<IOException>(() => disk.Read(new byte[8]));
        Check(disk.Position == 0, "incomplete native sector never advances a successful read");
    }

    private static void LayoutInspectionWorksThroughStrictSectorDevice()
    {
        const long size = 64_000_000_000;
        // Only sectors inspected by this fixture are stored. The physical size is independently known.
        byte[] metadata = new byte[1536];
        metadata[510] = metadata[1534] = 0x55; metadata[511] = metadata[1535] = 0xAA;
        metadata[450] = 0x0C;
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(454), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(458), 100_000_000);
        var boot = metadata.AsSpan(1024);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[11..], 512);
        boot[13] = 64; boot[16] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[14..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[32..], 100_000_000);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[36..], 13000);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[44..], 2);
        using var image = new MemoryStream(metadata);
        var partition = new PartitionInfo(1, 0x0C, 2, 100_000_000);
        using var strict = new StrictSectorDevice(image, size);
        using var disk = new PhysicalDiskStream(strict, size, writable: false);
        var info = new DiskInfo(42, "SIMULATED", "test", "test", size, 512, "USB", false, false, false, "MBR", []);
        var result = DiskLayout.Inspect(disk, info);
        Check(result.FileSystem == "FAT32" && result.Data == partition && result.Raw.Count == 0, "Inspect handles its 8-byte GPT probe through a sector-only backend");
        Check(strict.Reads == 3 && strict.Writes == 0, "inspection reads only the three required sectors");
    }

    private static void RawWritesNeverModifyNeighbouringSectors()
    {
        byte[] bytes = Enumerable.Repeat((byte)0x5A, 1536).ToArray();
        using var device = new StrictSectorDevice(new MemoryStream(bytes, writable: true), bytes.Length);
        using var disk = new PhysicalDiskStream(device, bytes.Length, writable: true);
        disk.Position = 1;
        Expect<IOException>(() => disk.Write(new byte[512]));
        disk.Position = 512;
        Expect<IOException>(() => disk.Write(new byte[8]));
        Expect<IOException>(() => disk.WriteAsync(new byte[8].AsMemory()).AsTask().GetAwaiter().GetResult());
        Check(device.Writes == 0 && device.Reads == 0 && bytes.All(b => b == 0x5A), "unaligned write rejection never reads or changes neighbours");
        byte[] sector = Enumerable.Repeat((byte)0xA3, 512).ToArray();
        disk.Write(sector);
        Check(bytes.AsSpan(512, 512).SequenceEqual(sector) && bytes.Take(512).All(b => b == 0x5A) && bytes.Skip(1024).All(b => b == 0x5A), "aligned write only changes its sector");
        Check(disk.Position == 1024 && device.Writes == 1, "aligned write completes directly");
        disk.Position = bytes.Length;
        Expect<IOException>(() => disk.Write(sector));
        using var readOnly = new PhysicalDiskStream(new StrictSectorDevice(new MemoryStream(new byte[512]), 512), 512, writable: false);
        Expect<NotSupportedException>(() => readOnly.Write(sector));
    }

    private sealed class StrictSectorDevice(Stream storage, long size) : Stream
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public int MaxRead { get; set; } = int.MaxValue;
        public bool ReturnTruncatedSector { get; set; }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => true;
        public override long Length => throw new InvalidOperationException("Physical device length must not be queried as a file.");
        public override long Position { get => storage.Position; set => storage.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            RequireSectors(buffer.Length);
            Reads++;
            int wanted = ReturnTruncatedSector ? 256 : Math.Min(buffer.Length, MaxRead);
            return storage.Read(buffer[..wanted]);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        { RequireSectors(buffer.Length); Writes++; storage.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
        public override void Flush() => storage.Flush();
        public override long Seek(long offset, SeekOrigin origin) => storage.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        private void RequireSectors(int count)
        {
            if (count <= 0 || Position % 512 != 0 || count % 512 != 0 || Position > size - count)
                throw new IOException("Simulated native device rejects unaligned or out-of-bounds I/O.");
        }
        protected override void Dispose(bool disposing) { if (disposing) storage.Dispose(); base.Dispose(disposing); }
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
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
