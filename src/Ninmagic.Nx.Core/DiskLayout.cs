using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ninmagic.Nx.Core;

public static class DiskLayout
{
    private const uint Alignment = 2048;
    private const uint DataStart = 32768;

    public static (PartitionInfo Data, IReadOnlyList<(PartitionInfo Partition, uint EmuSector)> Raw, string FileSystem) Inspect(Stream disk, DiskInfo info)
    {
        ValidateDevice(info);
        byte[] mbr = ReadAt(disk, 0, 512);
        if (mbr[510] != 0x55 || mbr[511] != 0xAA) throw new InvalidDataException("La tarjeta no tiene una tabla MBR válida.");
        if (Encoding.ASCII.GetString(ReadAt(disk, 512, 8)) == "EFI PART") throw new NotSupportedException("No se admiten tarjetas GPT o híbridas.");
        var partitions = new List<PartitionInfo>();
        for (int i = 0; i < 4; i++)
        {
            var entry = mbr.AsSpan(446 + i * 16, 16);
            byte type = entry[4];
            uint start = U32(entry, 8), count = U32(entry, 12);
            if (start == 0 && count == 0 && type == 0) continue;
            if (entry[0] != 0 && entry[0] != 0x80) throw new InvalidDataException("La tabla MBR tiene un indicador de arranque inválido.");
            if (start == 0 || count == 0 || (ulong)start + count > (ulong)info.Size / 512)
                throw new InvalidDataException("Una partición está fuera de los límites de la tarjeta.");
            if (type is 0xEE or 0x05 or 0x0F or 0x85 or 0x83 or 0xFF)
                throw new NotSupportedException("La tarjeta contiene GPT, Linux, Android o particiones extendidas no compatibles.");
            partitions.Add(new(i + 1, type, start, count));
        }
        var ordered = partitions.OrderBy(p => p.StartSector).ToArray();
        for (int i = 1; i < ordered.Length; i++)
            if ((ulong)ordered[i - 1].StartSector + ordered[i - 1].SectorCount > ordered[i].StartSector)
                throw new InvalidDataException("Hay particiones superpuestas; no se puede migrar esta tarjeta.");
        PartitionInfo data = partitions.SingleOrDefault(p => p.Index == 1)
            ?? throw new InvalidDataException("Falta la primera partición de archivos.");
        if (data.Type is not (0x0B or 0x0C or 0x07)) throw new NotSupportedException("La primera partición debe ser FAT32 o exFAT.");
        string fs = InspectFileSystem(ReadAt(disk, data.Offset, 512), data);
        var raw = new List<(PartitionInfo Partition, uint EmuSector)>();
        foreach (PartitionInfo part in partitions.Where(p => p.Index != 1))
        {
            if (part.Type is not (0xE0 or 0x00 or 0x07 or 0x0B or 0x0C or 0xDA))
                throw new NotSupportedException($"La partición {part.Index} tiene un tipo desconocido ({part.Type:X2}).");
            uint? emu = null;
            foreach (uint delta in new uint[] { 0x8000, 0 })
            {
                ulong headerSector = (ulong)part.StartSector + delta + 0x4001;
                if (headerSector >= (ulong)part.StartSector + part.SectorCount) continue;
                byte[] header = ReadAt(disk, checked((long)headerSector * 512), 512);
                if (Encoding.ASCII.GetString(header, 0, 8) != "EFI PART") continue;
                if (emu.HasValue) throw new InvalidDataException("La ubicación de emuMMC es ambigua.");
                uint candidate = checked(part.StartSector + delta);
                ValidateNandGpt(disk, part, candidate, header);
                emu = candidate;
            }
            if (!emu.HasValue) throw new InvalidDataException($"La partición {part.Index} no contiene una emuMMC reconocible y válida.");
            raw.Add((part, emu.Value));
        }
        return (data, raw, fs);
    }

    private static string InspectFileSystem(byte[] boot, PartitionInfo part)
    {
        if (boot[510] != 0x55 || boot[511] != 0xAA) throw new InvalidDataException("El sector de arranque del sistema de archivos está dañado.");
        if (Encoding.ASCII.GetString(boot, 3, 8) == "EXFAT   ")
        {
            ulong volumeLength = U64(boot, 72), partitionOffset = U64(boot, 64);
            uint fatOffset = U32(boot, 80), fatLength = U32(boot, 84), heap = U32(boot, 88), clusters = U32(boot, 92), root = U32(boot, 96);
            if (part.Type != 0x07 || boot[108] != 9 || boot[109] > 16 || boot[110] is < 1 or > 2 ||
                volumeLength == 0 || volumeLength > part.SectorCount || partitionOffset != part.StartSector ||
                fatOffset < 24 || fatLength == 0 || (ulong)fatOffset + (ulong)fatLength * boot[110] > heap ||
                clusters == 0 || clusters > 0xFFFFFFF5 || root < 2 || root > (ulong)clusters + 1 ||
                (ulong)heap + ((ulong)clusters << boot[109]) > volumeLength || (ulong)fatLength * 128 < (ulong)clusters + 2)
                throw new InvalidDataException("La geometría exFAT no es válida o no usa sectores de 512 bytes.");
            return "exFAT";
        }
        uint sectors = U16(boot, 19) != 0 ? U16(boot, 19) : U32(boot, 32);
        uint reserved = U16(boot, 14), fat = U32(boot, 36), spc = boot[13], fats = boot[16];
        ulong overhead = reserved + (ulong)fats * fat;
        ulong count = spc > 0 && sectors > overhead ? ((ulong)sectors - overhead) / spc : 0;
        if (part.Type is not (0x0B or 0x0C) || U16(boot, 11) != 512 || spc == 0 || spc > 128 || (spc & (spc - 1)) != 0 ||
            reserved < 8 || fats != 2 || U16(boot, 17) != 0 || U16(boot, 22) != 0 || U16(boot, 42) != 0 ||
            fat == 0 || sectors > part.SectorCount || count < 65525 || count >= 0x0FFFFFF5 ||
            (ulong)fat * 128 < count + 2 || U32(boot, 44) < 2 || U32(boot, 44) >= count + 2)
            throw new InvalidDataException("La geometría FAT32 no es válida o no es compatible.");
        return "FAT32";
    }

    private static void ValidateNandGpt(Stream disk, PartitionInfo part, uint emu, byte[] header)
    {
        uint headerSize = U32(header, 12), expectedCrc = U32(header, 16);
        if (U32(header, 8) != 0x10000 || headerSize < 92 || headerSize > 512 || U32(header, 20) != 0)
            throw new InvalidDataException("La cabecera GPT interna de emuMMC no es válida.");
        byte[] crcHeader = header.AsSpan(0, (int)headerSize).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(crcHeader.AsSpan(16), 0);
        if (Crc32(crcHeader) != expectedCrc) throw new InvalidDataException("La cabecera GPT de emuMMC tiene un CRC incorrecto.");
        ulong gppStart = (ulong)emu + 0x4000, available = (ulong)part.StartSector + part.SectorCount - gppStart;
        ulong current = U64(header, 24), alternate = U64(header, 32), first = U64(header, 40), last = U64(header, 48), entriesAt = U64(header, 72);
        uint count = U32(header, 80), entrySize = U32(header, 84);
        if (current != 1 || alternate < 34 || alternate >= available || first < 34 || first > last || last >= alternate ||
            entriesAt < 2 || count == 0 || count > 4096 || entrySize < 128 || entrySize > 1024 || entrySize % 8 != 0)
            throw new InvalidDataException("La imagen NAND supera su partición o tiene una geometría GPT inválida.");
        ulong entriesBytes = (ulong)count * entrySize;
        ulong entrySectors = (entriesBytes + 511) / 512;
        if (entriesAt + entrySectors > first || alternate < entrySectors || last >= alternate - entrySectors) throw new InvalidDataException("La tabla GPT interna invade los datos de emuMMC.");
        byte[] entries = ReadAt(disk, checked((long)(gppStart + entriesAt) * 512), checked((int)entriesBytes));
        if (Crc32(entries) != U32(header, 88)) throw new InvalidDataException("La tabla de particiones de emuMMC tiene un CRC incorrecto.");
        var ranges = new List<(ulong Start, ulong End)>();
        for (int i = 0; i < count; i++)
        {
            var entry = entries.AsSpan(checked((int)(i * entrySize)), (int)entrySize);
            if (entry[..16].IndexOfAnyExcept((byte)0) < 0) continue;
            ulong start = U64(entry, 32), end = U64(entry, 40);
            if (start < first || end > last || end < start) throw new InvalidDataException("Una partición interna de NAND está fuera de sus límites.");
            ranges.Add((start, end));
        }
        if (ranges.Count == 0) throw new InvalidDataException("emuMMC no tiene particiones NAND.");
        var sorted = ranges.OrderBy(r => r.Start).ToArray();
        for (int i = 1; i < sorted.Length; i++) if (sorted[i - 1].End >= sorted[i].Start) throw new InvalidDataException("Las particiones NAND se superponen.");
        byte[] backup = ReadAt(disk, checked((long)(gppStart + alternate) * 512), 512);
        if (!backup.AsSpan(0, 8).SequenceEqual("EFI PART"u8) || U64(backup, 24) != alternate || U64(backup, 32) != 1)
            throw new InvalidDataException("Falta la cabecera GPT de respaldo de emuMMC.");
        uint backupSize = U32(backup, 12), backupCrc = U32(backup, 16);
        ulong backupEntriesAt = U64(backup, 72);
        if (backupSize != headerSize || U32(backup, 8) != 0x10000 || U64(backup, 40) != first || U64(backup, 48) != last ||
            !backup.AsSpan(56, 16).SequenceEqual(header.AsSpan(56, 16)) || U32(backup, 80) != count || U32(backup, 84) != entrySize ||
            U32(backup, 88) != U32(header, 88) || backupEntriesAt <= last || backupEntriesAt + entrySectors > alternate)
            throw new InvalidDataException("La cabecera GPT de respaldo no coincide con la principal.");
        BinaryPrimitives.WriteUInt32LittleEndian(backup.AsSpan(16), 0);
        if (Crc32(backup.AsSpan(0, (int)backupSize)) != backupCrc) throw new InvalidDataException("La cabecera GPT de respaldo tiene un CRC incorrecto.");
        byte[] backupEntries = ReadAt(disk, checked((long)(gppStart + backupEntriesAt) * 512), checked((int)entriesBytes));
        if (!backupEntries.AsSpan().SequenceEqual(entries)) throw new InvalidDataException("La tabla GPT de respaldo no coincide con la principal.");
    }

    public static MigrationPlan Plan(BackupManifest manifest, DiskInfo target)
    {
        ValidateDevice(target);
        if (manifest.FileSystem is not ("FAT32" or "exFAT")) throw new NotSupportedException("El sistema de archivos de la copia no es compatible.");
        string fileSystem = ResolveTargetFileSystem(manifest, target);
        if (manifest.RawPartitions.Count > 3) throw new InvalidDataException("Hay demasiadas particiones emuMMC.");
        ulong cursor = ((ulong)target.Size / 512 - Alignment) / Alignment * Alignment;
        var raw = new PartitionInfo[manifest.RawPartitions.Count];
        var map = new Dictionary<uint, uint>();
        for (int i = raw.Length - 1; i >= 0; i--)
        {
            RawPartition source = manifest.RawPartitions[i];
            if (source.Partition.SectorCount == 0 || source.EmuSector < source.Partition.StartSector ||
                (ulong)source.EmuSector + 0x4002 >= (ulong)source.Partition.StartSector + source.Partition.SectorCount)
                throw new InvalidDataException("La ubicación de una emuMMC de la copia no es válida.");
            if (cursor <= source.Partition.SectorCount + (ulong)DataStart) throw new InvalidDataException("La tarjeta de destino es demasiado pequeña para emuMMC.");
            cursor = (cursor - source.Partition.SectorCount) / Alignment * Alignment;
            raw[i] = new(i + 2, 0xE0, checked((uint)cursor), source.Partition.SectorCount);
            uint newSector = checked(raw[i].StartSector + (source.EmuSector - source.Partition.StartSector));
            if (!map.TryAdd(source.EmuSector, newSector)) throw new InvalidDataException("La copia contiene emuMMC duplicadas o ambiguas.");
        }
        if (cursor <= DataStart) throw new InvalidDataException("No queda espacio para los archivos.");
        var data = new PartitionInfo(1, fileSystem == "FAT32" ? (byte)0x0C : (byte)0x07, DataStart, checked((uint)(cursor - DataStart)));
        long required = 0;
        foreach (var entry in manifest.Files)
        {
            if (entry.Length < 0)
                throw new InvalidDataException("Un archivo de la copia no cabe en el sistema de archivos de destino.");
            if (fileSystem == "FAT32" && !entry.IsDirectory && entry.Length > uint.MaxValue)
                throw new InvalidDataException($"No se puede conservar el archivo «{entry.Path}» al convertir el destino a FAT32: supera el límite de 4 GiB menos 1 byte. No se ha escrito en la tarjeta de destino.");
            // exFAT formatter uses 128 KiB units; FAT32 uses 32 KiB units.
            long allocation = fileSystem == "FAT32" ? 32768 : 131072;
            required = checked(required + (entry.IsDirectory ? allocation : checked((entry.Length + allocation - 1) / allocation * allocation)));
            required = checked(required + 1024); // long names, directory growth and metadata.
        }
        required = checked(required + 64L * 1024 * 1024);
        long capacity = fileSystem == "FAT32" ? Fat32Formatter.GetGeometry(data).DataBytes : checked(data.Length - Math.Max(64L * 1024 * 1024, data.Length / 100));
        if (required > capacity) throw new InvalidDataException($"La tarjeta de destino no tiene espacio suficiente. Se necesitan aproximadamente {required / 1_000_000_000.0:0.00} GB para los archivos, además de emuMMC.");
        return new(data, raw, map, required, fileSystem, target.Size);
    }

    public static string ResolveTargetFileSystem(BackupManifest manifest, DiskInfo target) =>
        target.Volumes.Any(volume => string.Equals(volume.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            ? "FAT32" : manifest.FileSystem;

    public static void WriteMbr(Stream disk, MigrationPlan plan)
    {
        if (plan.TargetSize < 32L * 1024 * 1024 || plan.TargetSize % 512 != 0) throw new InvalidDataException("Tamaño de destino inválido.");
        var parts = new[] { plan.DataPartition }.Concat(plan.RawPartitions).OrderBy(p => p.Index).ToArray();
        if (parts.Length > 4 || parts.Select(p => p.Index).Distinct().Count() != parts.Length || parts.Any(p => p.Index < 1 || p.Index > 4 || p.StartSector < Alignment || p.SectorCount == 0 || (ulong)p.StartSector + p.SectorCount > (ulong)plan.TargetSize / 512 - Alignment))
            throw new InvalidDataException("El plan contiene particiones fuera de los límites seguros.");
        var ordered = parts.OrderBy(p => p.StartSector).ToArray();
        for (int i = 1; i < ordered.Length; i++) if ((ulong)ordered[i - 1].StartSector + ordered[i - 1].SectorCount > ordered[i].StartSector) throw new InvalidDataException("El plan contiene particiones superpuestas.");
        byte[] zero = new byte[1024 * 1024];
        WriteAt(disk, 0, zero);
        WriteAt(disk, plan.TargetSize - zero.Length, zero);
        byte[] mbr = new byte[512];
        RandomNumberGenerator.Fill(mbr.AsSpan(440, 4));
        if (U32(mbr, 440) == 0) mbr[440] = 1;
        foreach (var part in parts)
        {
            var entry = mbr.AsSpan(446 + (part.Index - 1) * 16, 16);
            entry[1] = 0xFE; entry[2] = 0xFF; entry[3] = 0xFF;
            entry[4] = part.Type;
            entry[5] = 0xFE; entry[6] = 0xFF; entry[7] = 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], part.StartSector);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], part.SectorCount);
        }
        mbr[510] = 0x55; mbr[511] = 0xAA;
        WriteAt(disk, 0, mbr);
        disk.Flush();
    }

    internal static byte[] ReadAt(Stream disk, long offset, int count)
    {
        disk.Position = offset;
        byte[] buffer = new byte[count];
        disk.ReadExactly(buffer);
        return buffer;
    }
    internal static void WriteAt(Stream disk, long offset, ReadOnlySpan<byte> bytes) { disk.Position = offset; disk.Write(bytes); }
    private static void ValidateDevice(DiskInfo info)
    {
        if (info.LogicalSectorSize != 512) throw new NotSupportedException("Solo se admiten tarjetas con sectores lógicos de 512 bytes.");
        if (info.Size < 64L * 1024 * 1024 || info.Size % 512 != 0 || (ulong)info.Size / 512 > uint.MaxValue)
            throw new NotSupportedException("El tamaño de esta tarjeta está fuera del rango MBR compatible (hasta 2 TiB).");
    }
    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in bytes) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xEDB88320u & unchecked((uint)-(int)(crc & 1))); }
        return ~crc;
    }
    private static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
}
