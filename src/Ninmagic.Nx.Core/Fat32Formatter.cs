using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ninmagic.Nx.Core;

public static class Fat32Formatter
{
    public sealed record Geometry(uint ReservedSectors, uint FatSectors, uint ClusterCount, uint SectorsPerCluster)
    {
        public long DataBytes => (long)ClusterCount * SectorsPerCluster * 512;
    }

    public static Geometry GetGeometry(PartitionInfo partition)
    {
        const uint reserved = 32, spc = 64;
        if (partition.Type is not (0x0B or 0x0C) || partition.StartSector == 0 || partition.SectorCount <= reserved)
            throw new InvalidDataException("Partición FAT32 inválida.");
        // An upper bound is intentional: a FAT may be slightly larger than necessary,
        // but must never be too small for its data area.
        ulong maxClusters = ((ulong)partition.SectorCount - reserved) / spc;
        uint fat = checked((uint)((maxClusters + 2 + 127) / 128));
        ulong overhead = reserved + (ulong)fat * 2;
        uint clusters = overhead < partition.SectorCount ? checked((uint)((partition.SectorCount - overhead) / spc)) : 0;
        if (clusters < 65525 || clusters >= 0x0FFFFFF5 || (ulong)fat * 128 < (ulong)clusters + 2)
            throw new NotSupportedException("La partición FAT32 debe tener al menos 2,1 GiB para usar clústeres de 32 KiB.");
        return new(reserved, fat, clusters, spc);
    }

    public static void Format(Stream disk, PartitionInfo partition, string label, CancellationToken cancellationToken)
    {
        var geometry = GetGeometry(partition);
        if (!disk.CanSeek || !disk.CanWrite) throw new ArgumentException("El destino debe permitir escritura y acceso aleatorio.", nameof(disk));
        cancellationToken.ThrowIfCancellationRequested();
        long metadataLength = checked(((long)geometry.ReservedSectors + 2L * geometry.FatSectors + geometry.SectorsPerCluster) * 512);
        byte[] zeros = new byte[1024 * 1024];
        disk.Position = partition.Offset;
        for (long remaining = metadataLength; remaining > 0;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int size = (int)Math.Min(zeros.Length, remaining);
            disk.Write(zeros, 0, size);
            remaining -= size;
        }
        byte[] labelBytes = Encoding.ASCII.GetBytes(NormalizeLabel(label));
        byte[] boot = new byte[512];
        boot[0] = 0xEB; boot[1] = 0x58; boot[2] = 0x90;
        "MSWIN4.1"u8.CopyTo(boot.AsSpan(3));
        U16(boot, 11, 512); boot[13] = (byte)geometry.SectorsPerCluster;
        U16(boot, 14, (ushort)geometry.ReservedSectors); boot[16] = 2;
        boot[21] = 0xF8; U16(boot, 24, 63); U16(boot, 26, 255);
        U32(boot, 28, partition.StartSector); U32(boot, 32, partition.SectorCount);
        U32(boot, 36, geometry.FatSectors); U32(boot, 44, 2);
        U16(boot, 48, 1); U16(boot, 50, 6);
        boot[64] = 0x80; boot[66] = 0x29;
        RandomNumberGenerator.Fill(boot.AsSpan(67, 4));
        labelBytes.CopyTo(boot, 71); "FAT32   "u8.CopyTo(boot.AsSpan(82));
        boot[510] = 0x55; boot[511] = 0xAA;
        byte[] info = new byte[512];
        U32(info, 0, 0x41615252); U32(info, 484, 0x61417272);
        U32(info, 488, geometry.ClusterCount - 1); U32(info, 492, 3); U32(info, 508, 0xAA550000);
        byte[] fat = new byte[512];
        U32(fat, 0, 0x0FFFFFF8); U32(fat, 4, 0xFFFFFFFF); U32(fat, 8, 0x0FFFFFFF);
        byte[] root = new byte[512];
        labelBytes.CopyTo(root, 0); root[11] = 0x08;
        DiskLayout.WriteAt(disk, partition.Offset, boot);
        DiskLayout.WriteAt(disk, partition.Offset + 512, info);
        DiskLayout.WriteAt(disk, partition.Offset + 6 * 512, boot);
        DiskLayout.WriteAt(disk, partition.Offset + 7 * 512, info);
        DiskLayout.WriteAt(disk, partition.Offset + (long)geometry.ReservedSectors * 512, fat);
        DiskLayout.WriteAt(disk, partition.Offset + ((long)geometry.ReservedSectors + geometry.FatSectors) * 512, fat);
        DiskLayout.WriteAt(disk, partition.Offset + ((long)geometry.ReservedSectors + 2L * geometry.FatSectors) * 512, root);
        disk.Flush();
    }

    private static string NormalizeLabel(string label)
    {
        var chars = (label ?? "SWITCH SD").ToUpperInvariant().Select(c => c is >= ' ' and <= '~' && !"*?.,;:/\\|+=<>[]\"".Contains(c) ? c : '_').ToArray();
        string value = new(chars.Take(11).ToArray());
        if (string.IsNullOrWhiteSpace(value)) value = "SWITCH SD";
        return value.PadRight(11);
    }
    private static void U16(Span<byte> b, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b[offset..], value);
    private static void U32(Span<byte> b, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b[offset..], value);
}
