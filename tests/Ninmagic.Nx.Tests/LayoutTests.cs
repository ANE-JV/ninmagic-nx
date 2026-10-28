using System.Buffers.Binary;
using System.Text;
using Ninmagic.Nx.Core;

namespace Ninmagic.Nx.Tests;

public static class LayoutTests
{
    public static void Run(Action<string, Action> test)
    {
        test("FAT32 metadata has consistent independent geometry, mirror FAT and FSInfo", () =>
        {
            using var disk = new SparseDisk(64L << 30);
            var part = new PartitionInfo(1, 0x0C, 32768, 100_000_000);
            Fat32Formatter.Format(disk, part, "NINMAGIC", CancellationToken.None);
            byte[] boot = Read(disk, part.Offset, 512);
            Check(U16(boot, 11) == 512 && boot[13] == 64 && boot[16] == 2, "Wrong FAT32 cluster geometry");
            uint fat = U32(boot, 36), reserved = U16(boot, 14);
            uint clusters = (part.SectorCount - reserved - fat * 2) / 64;
            Check((ulong)fat * 128 >= (ulong)clusters + 2, "FAT too small");
            Check(Read(disk, part.Offset + 6 * 512, 512).SequenceEqual(boot), "Backup boot mismatch");
            Check(U32(Read(disk, part.Offset + 512, 512), 488) == clusters - 1, "FSInfo count mismatch");
            byte[] fat1 = Read(disk, part.Offset + reserved * 512L, 512);
            Check(fat1.SequenceEqual(Read(disk, part.Offset + (reserved + fat) * 512L, 512)), "FAT mirrors mismatch");
            Check(U32(fat1, 8) >= 0x0FFFFFF8, "Root cluster is not allocated");
        });
        test("Plan expands file partition at 128 GB, 512 GB, 1 TB and 2 TB", () =>
        {
            BackupManifest manifest = Manifest();
            foreach (long bytes in new[] { 128_000_000_000L, 512_000_000_000L, 1_000_000_000_000L, 2_000_000_000_000L })
            {
                MigrationPlan plan = DiskLayout.Plan(manifest, Info(bytes));
                Check(plan.DataPartition.StartSector == 32768 && plan.RawPartitions[0].StartSector % 2048 == 0, "Bad alignment");
                Check(plan.RawPartitions[0].SectorCount == manifest.RawPartitions[0].Partition.SectorCount, "Raw resized");
                Check(plan.SectorMap[manifest.RawPartitions[0].EmuSector] == plan.RawPartitions[0].StartSector + 0x8000, "Wrong sector relocation");
                Check(plan.DataPartition.Length > manifest.DataPartition.Length, "Files did not expand");
                Check((long)plan.RawPartitions[0].StartSector * 512 + plan.RawPartitions[0].Length <= bytes - 1048576, "Tail overlap");
            }
        });
        test("Plan rejects insufficient capacity, >2 TiB and non-512 sectors", () =>
        {
            Throws(() => DiskLayout.Plan(Manifest(), Info(16L << 30)));
            Throws(() => DiskLayout.Plan(Manifest(), Info(3L << 40)));
            Throws(() => DiskLayout.Plan(Manifest(), Info(128L << 30) with { LogicalSectorSize = 4096 }));
        });
        test("Plan preserves multiple raw emuMMC partitions and legacy zero gap", () =>
        {
            var original = Manifest();
            var second = new RawPartition(new(3, 0xE0, 100_000_000, 8_000_000), 100_000_000, "raw/partition-3.img", "");
            var manifest = original with { RawPartitions = original.RawPartitions.Append(second).ToArray() };
            var plan = DiskLayout.Plan(manifest, Info(128_000_000_000));
            Check(plan.RawPartitions.Count == 2 && plan.SectorMap[second.EmuSector] == plan.RawPartitions[1].StartSector, "Legacy mapping changed");
            Check((ulong)plan.RawPartitions[0].StartSector + plan.RawPartitions[0].SectorCount <= plan.RawPartitions[1].StartSector, "Overlap");
        });
        test("MBR writer clears stale GPT metadata and writes unique disk signature", () =>
        {
            var plan = DiskLayout.Plan(Manifest(), Info(128_000_000_000));
            using var disk = new SparseDisk(plan.TargetSize);
            Write(disk, 512, "EFI PART"u8.ToArray());
            Write(disk, plan.TargetSize - 512, "EFI PART"u8.ToArray());
            DiskLayout.WriteMbr(disk, plan);
            byte[] mbr = Read(disk, 0, 512);
            Check(mbr[510] == 0x55 && mbr[511] == 0xAA && U32(mbr, 440) != 0, "Bad MBR");
            Check(U32(mbr, 454) == 32768 && mbr[450] == 0x0C && mbr[466] == 0xE0, "Bad partition table");
            Check(Read(disk, 512, 8).All(b => b == 0) && Read(disk, plan.TargetSize - 512, 8).All(b => b == 0), "GPT remnants retained");
        });
        test("Inspect accepts CRC-validated standard and legacy raw emuMMC", () =>
        {
            foreach (uint gap in new uint[] { 0x8000, 0 })
            {
                using var disk = Fixture(gap);
                var result = DiskLayout.Inspect(disk, Info(disk.Length));
                Check(result.FileSystem == "FAT32" && result.Raw.Count == 1 && result.Raw[0].EmuSector == 10_000_384 + gap, "Wrong inspection");
            }
        });
        test("Inspect rejects corrupted NAND GPT and source partition overlap", () =>
        {
            using var disk = Fixture(0x8000);
            long header = (10_000_384L + 0x8000 + 0x4001) * 512;
            Write(disk, header + 16, new byte[] { 0, 0, 0, 0 });
            Throws(() => DiskLayout.Inspect(disk, Info(disk.Length)));
            using var overlap = Fixture(0x8000);
            Write(overlap, 446 + 16 + 8, BitConverter.GetBytes(50000u));
            Throws(() => DiskLayout.Inspect(overlap, Info(overlap.Length)));
        });
        test("Inspect rejects unknown outer partition and outer GPT", () =>
        {
            using var disk = Fixture(0x8000);
            Write(disk, 466, new byte[] { 0x83 });
            Throws(() => DiskLayout.Inspect(disk, Info(disk.Length)));
            Write(disk, 466, new byte[] { 0xE0 });
            Write(disk, 512, "EFI PART"u8.ToArray());
            Throws(() => DiskLayout.Inspect(disk, Info(disk.Length)));
        });
        test("FAT32 formatter cancellation leaves metadata unwritten", () =>
        {
            using var disk = new SparseDisk(64L << 30);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Throws(() => Fat32Formatter.Format(disk, new(1, 0x0C, 32768, 100_000_000), "SD", cancel.Token));
            Check(disk.Writes == 0, "Cancelled format wrote disk");
        });
        test("Reference patch updates binary marker and only INI sector preserving BOM/comments", () => WithFolder(folder =>
        {
            Directory.CreateDirectory(Path.Combine(folder, "emuMMC", "RAW1"));
            uint old = 50_000_000 + 0x8000;
            string ini = "[emummc]\r\nenabled=1\r\nsector = 0x" + old.ToString("X") + " ; keep\r\npath=emuMMC/RAW1\r\nid=0x1234\r\nnintendo_path=emuMMC/RAW1/Nintendo\r\n";
            File.WriteAllBytes(Path.Combine(folder, "emuMMC", "emummc.ini"), Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ini)).ToArray());
            File.WriteAllBytes(Path.Combine(folder, "emuMMC", "RAW1", "raw_based"), BitConverter.GetBytes(old));
            var plan = DiskLayout.Plan(Manifest(), Info(128_000_000_000));
            var patches = EmuMmc.PatchReferences(folder, plan);
            Check(patches.Count == 2, "Expected two references");
            byte[] changed = File.ReadAllBytes(Path.Combine(folder, "emuMMC", "emummc.ini"));
            Check(changed.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()), "BOM lost");
            Check(Encoding.UTF8.GetString(changed).Contains("sector = 0x" + plan.SectorMap[old].ToString("X") + " ; keep\r\n"), "Formatting changed");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(Path.Combine(folder, "emuMMC", "RAW1", "raw_based"))) == plan.SectorMap[old], "Wrong marker");
        }));
        test("Reference patch rejects contradiction before modifying either file", () => WithFolder(folder =>
        {
            Directory.CreateDirectory(Path.Combine(folder, "emuMMC", "RAW1"));
            string marker = Path.Combine(folder, "emuMMC", "RAW1", "raw_based");
            File.WriteAllBytes(marker, BitConverter.GetBytes(50_000_000u + 0x8000));
            File.WriteAllText(Path.Combine(folder, "emuMMC", "emummc.ini"), "[emummc]\nenabled=1\nsector=0xDEADBEEF\npath=emuMMC/RAW1\n");
            Throws(() => EmuMmc.PatchReferences(folder, DiskLayout.Plan(Manifest(), Info(128_000_000_000))));
            Check(BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(marker)) == 50_000_000u + 0x8000, "Partial patch occurred");
        }));
        test("Reference validator rejects path traversal and malformed raw marker", () => WithFolder(folder =>
        {
            var manifest = Manifest() with { Files = new[] { new FileEntry("../outside", 0, "", 0, DateTime.UtcNow, false) } };
            Throws(() => EmuMmc.ValidateReferences(manifest, folder));
            Directory.CreateDirectory(Path.Combine(folder, "files", "emuMMC", "RAW1"));
            File.WriteAllBytes(Path.Combine(folder, "files", "emuMMC", "RAW1", "raw_based"), new byte[8]);
            manifest = Manifest() with { Files = new[] { new FileEntry("emuMMC/RAW1/raw_based", 8, "", 0, DateTime.UtcNow, false) } };
            Throws(() => EmuMmc.ValidateReferences(manifest, folder));
        }));
    }

    private static BackupManifest Manifest() => new(1, "1.0.0", DateTime.UtcNow, Info(64_000_000_000), new(1, 0x0C, 32768, 49_967_232), "FAT32", "SWITCH", new[] { new RawPartition(new(2, 0xE0, 50_000_000, 60_000_000), 50_000_000 + 0x8000, "raw/partition-2.img", "") }, new[] { new FileEntry("test.bin", 1024, "", 0, DateTime.UtcNow, false) }, Array.Empty<string>());
    private static DiskInfo Info(long bytes) => new(42, "VIRTUAL TEST", "test", "test", bytes, 512, "USB", false, false, false, "MBR", Array.Empty<VolumeInfo>());
    private static SparseDisk Fixture(uint gap)
    {
        var disk = new SparseDisk(8L << 30);
        var data = new PartitionInfo(1, 0x0C, 32768, 9_967_616);
        var raw = new PartitionInfo(2, 0xE0, 10_000_384, 131072);
        var plan = new MigrationPlan(data, new[] { raw }, new Dictionary<uint, uint>(), 0, "FAT32", disk.Length);
        DiskLayout.WriteMbr(disk, plan);
        Fat32Formatter.Format(disk, data, "TEST", CancellationToken.None);
        ulong gpp = raw.StartSector + gap + 0x4000UL;
        byte[] entries = new byte[128 * 128]; entries[0] = 1;
        W64(entries, 32, 34); W64(entries, 40, 4062);
        byte[] header = GptHeader(1, 4095, 2, entries);
        byte[] backup = GptHeader(4095, 1, 4063, entries);
        Write(disk, checked((long)(gpp + 1) * 512), header);
        Write(disk, checked((long)(gpp + 2) * 512), entries);
        Write(disk, checked((long)(gpp + 4063) * 512), entries);
        Write(disk, checked((long)(gpp + 4095) * 512), backup);
        return disk;
    }
    private static byte[] GptHeader(ulong current, ulong alternate, ulong entriesAt, byte[] entries)
    {
        byte[] header = new byte[512]; "EFI PART"u8.CopyTo(header);
        W32(header, 8, 0x10000); W32(header, 12, 92); W64(header, 24, current); W64(header, 32, alternate);
        W64(header, 40, 34); W64(header, 48, 4062); W64(header, 72, entriesAt); W32(header, 80, 128); W32(header, 84, 128); W32(header, 88, Crc(entries)); W32(header, 16, Crc(header.AsSpan(0, 92)));
        return header;
    }
    private static uint Crc(ReadOnlySpan<byte> data) { uint crc = uint.MaxValue; foreach (byte b in data) { crc ^= b; for (int n = 0; n < 8; n++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1; } return ~crc; }
    private static ushort U16(byte[] b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset));
    private static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset));
    private static void W32(byte[] b, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset), value);
    private static void W64(byte[] b, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(offset), value);
    private static byte[] Read(Stream s, long offset, int count) { s.Position = offset; byte[] b = new byte[count]; s.ReadExactly(b); return b; }
    private static void Write(Stream s, long offset, byte[] bytes) { s.Position = offset; s.Write(bytes); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or NotSupportedException or OperationCanceledException) { return; } throw new Exception("Expected fail-closed rejection"); }
    private static void WithFolder(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "ninmagic-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); } finally { Directory.Delete(root, true); }
    }
    private sealed class SparseDisk(long length) : Stream
    {
        private readonly Dictionary<long, byte[]> blocks = new();
        public long Writes { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => true;
        public override long Length => length; public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, length - Position); buffer[..count].Clear();
            for (int done = 0; done < count;)
            {
                long block = Position / 4096; int within = (int)(Position % 4096), take = Math.Min(count - done, 4096 - within);
                if (blocks.TryGetValue(block, out byte[]? bytes)) bytes.AsSpan(within, take).CopyTo(buffer.Slice(done, take));
                Position += take; done += take;
            }
            return count;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position < 0 || Position + buffer.Length > length) throw new IOException("Fixture disk out of bounds");
            Writes++;
            for (int done = 0; done < buffer.Length;)
            {
                long block = Position / 4096; int within = (int)(Position % 4096), take = Math.Min(buffer.Length - done, 4096 - within);
                ReadOnlySpan<byte> slice = buffer.Slice(done, take);
                if (blocks.TryGetValue(block, out byte[]? bytes) || slice.IndexOfAnyExcept((byte)0) >= 0)
                {
                    if (bytes == null) { bytes = new byte[4096]; blocks.Add(block, bytes); }
                    slice.CopyTo(bytes.AsSpan(within, take));
                }
                Position += take; done += take;
            }
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, SeekOrigin.End => length + offset, _ => throw new ArgumentException() };
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
