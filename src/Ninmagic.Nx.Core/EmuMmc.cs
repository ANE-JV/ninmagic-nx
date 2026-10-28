using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ninmagic.Nx.Core;

public static class EmuMmc
{
    private sealed record Field(string Value, int Start, int Length);
    private sealed record Ini(string Text, Encoding Encoding, byte[] Preamble, Dictionary<string, Field> Fields)
    {
        public byte[] ReplaceSector(uint sector)
        {
            Field field = Fields["sector"];
            string result = Text.Remove(field.Start, field.Length).Insert(field.Start, $"0x{sector:X}");
            return Preamble.Concat(Encoding.GetBytes(result)).ToArray();
        }
    }

    public static void ValidateReferences(BackupManifest manifest, string backupFolder)
    {
        string root = Path.Combine(backupFolder, "files");
        var paths = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            string relative = ValidateRelative(entry.Path);
            if (!paths.TryAdd(relative, entry)) throw new InvalidDataException("La copia contiene rutas duplicadas.");
            if (relative.Split('/')[^1].Equals("raw_based", StringComparison.OrdinalIgnoreCase) && !IsRawMarker(relative))
                throw new NotSupportedException("Los marcadores raw_based deben estar en emuMMC/<nombre>/raw_based. La tarjeta usa una ubicación personalizada no compatible.");
        }
        var known = manifest.RawPartitions.Select(p => p.EmuSector).ToHashSet();
        foreach (var entry in manifest.Files.Where(f => IsRawMarker(f.Path)))
        {
            if (entry.IsDirectory || entry.Length != 4) throw new InvalidDataException($"El marcador {entry.Path} debe contener exactamente cuatro bytes.");
            uint sector = ReadMarker(CombineSafe(root, entry.Path));
            if (!known.Contains(sector)) throw new InvalidDataException($"El marcador {entry.Path} apunta a una emuMMC que no está en la copia.");
        }
        if (!paths.TryGetValue("emuMMC/emummc.ini", out var iniEntry))
        {
            if (manifest.RawPartitions.Count > 0 && !manifest.Files.Any(f => IsRawMarker(f.Path)))
                throw new InvalidDataException("Se detectó emuMMC sin configuración ni marcadores que permitan identificarla.");
            return;
        }
        if (iniEntry.IsDirectory || iniEntry.Length > 1024 * 1024) throw new InvalidDataException("La configuración emummc.ini no es válida.");
        Ini ini = Parse(File.ReadAllBytes(CombineSafe(root, iniEntry.Path)));
        ValidateIni(ini, known, path => paths.TryGetValue(path, out var e) ? e : null,
            path => File.ReadAllBytes(CombineSafe(root, path)));
    }

    public static IReadOnlyDictionary<string, byte[]> PatchReferences(string targetRoot, MigrationPlan plan)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        string emuRoot = CombineSafe(targetRoot, "emuMMC");
        if (!Directory.Exists(emuRoot))
        {
            if (plan.RawPartitions.Count > 0) throw new InvalidDataException("No se encuentra la carpeta emuMMC en el destino.");
            return result;
        }
        RejectLink(emuRoot);
        foreach (string directory in Directory.EnumerateDirectories(emuRoot))
        {
            RejectLink(directory);
            string markerPath = Path.Combine(directory, "raw_based");
            if (!File.Exists(markerPath)) continue;
            RejectLink(markerPath);
            uint oldSector = ReadMarker(markerPath);
            if (!plan.SectorMap.TryGetValue(oldSector, out uint next)) throw new InvalidDataException($"El marcador {markerPath} no corresponde a ninguna emuMMC del plan.");
            byte[] bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, next);
            string relative = Path.GetRelativePath(targetRoot, markerPath).Replace('\\', '/');
            result.Add(relative, bytes);
        }
        string iniPath = Path.Combine(emuRoot, "emummc.ini");
        if (File.Exists(iniPath))
        {
            RejectLink(iniPath);
            if (new FileInfo(iniPath).Length > 1024 * 1024) throw new InvalidDataException("emummc.ini es demasiado grande.");
            Ini ini = Parse(File.ReadAllBytes(iniPath));
            ValidateIni(ini, plan.SectorMap.Keys.ToHashSet(), path =>
            {
                string full = CombineSafe(targetRoot, path);
                if (!File.Exists(full)) return null;
                RejectLink(full);
                var info = new FileInfo(full);
                return new FileEntry(path, info.Length, "", (int)info.Attributes, info.LastWriteTimeUtc, false);
            }, path => File.ReadAllBytes(CombineSafe(targetRoot, path)));
            if (ini.Fields.TryGetValue("sector", out var field))
            {
                uint sector = ParseSector(field.Value);
                if (sector != 0)
                {
                    if (!plan.SectorMap.TryGetValue(sector, out uint next)) throw new InvalidDataException("La configuración activa de emuMMC no coincide con el plan.");
                    result.Add("emuMMC/emummc.ini", ini.ReplaceSector(next));
                }
            }
        }
        // Validate every reference before changing any file.
        foreach (var pair in result)
        {
            string full = CombineSafe(targetRoot, pair.Key);
            FileAttributes attributes = File.GetAttributes(full);
            DateTime time = File.GetLastWriteTimeUtc(full);
            try
            {
                File.SetAttributes(full, attributes & ~FileAttributes.ReadOnly);
                using (var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None))
                { stream.Write(pair.Value); stream.Flush(true); }
                File.SetLastWriteTimeUtc(full, time);
            }
            finally { File.SetAttributes(full, attributes); }
        }
        return result;
    }

    private static void ValidateIni(Ini ini, HashSet<uint> known, Func<string, FileEntry?> lookup, Func<string, byte[]> read)
    {
        bool enabled = ini.Fields.TryGetValue("enabled", out var enabledField) && enabledField.Value != "0";
        if (enabled && (!int.TryParse(enabledField!.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int en) || en < 0))
            throw new InvalidDataException("El valor enabled de emummc.ini no es válido.");
        uint sector = ini.Fields.TryGetValue("sector", out var sectorField) ? ParseSector(sectorField.Value) : 0;
        if (enabled && !ini.Fields.ContainsKey("sector")) throw new InvalidDataException("Falta el sector en emummc.ini.");
        string? path = ini.Fields.TryGetValue("path", out var pathField) && pathField.Value.Length > 0 ? ValidateRelative(pathField.Value) : null;
        if (ini.Fields.TryGetValue("nintendo_path", out var nintendoField) && nintendoField.Value.Length > 0) ValidateRelative(nintendoField.Value);
        if (sector > 0)
        {
            if (!known.Contains(sector)) throw new InvalidDataException("emummc.ini apunta a una partición emuMMC ausente o desconocida.");
            if (path != null && !IsRawMarker(path + "/raw_based"))
                throw new NotSupportedException("La ruta de una emuMMC por partición debe ser emuMMC/<nombre>.");
            if (path != null && lookup(path + "/raw_based") is { } marker)
            {
                if (marker.IsDirectory || marker.Length != 4 || BinaryPrimitives.ReadUInt32LittleEndian(read(path + "/raw_based")) != sector)
                    throw new InvalidDataException("emummc.ini y el marcador raw_based se contradicen.");
            }
        }
        else if (enabled)
        {
            if (path == null) throw new InvalidDataException("La emuMMC por archivos no tiene una ruta válida.");
            foreach (string name in new[] { "BOOT0", "BOOT1", "00" })
            {
                FileEntry? file = lookup(path + "/eMMC/" + name);
                if (file == null || file.IsDirectory || file.Length == 0 || file.Length % 512 != 0 || (name != "00" && file.Length != 4 * 1024 * 1024))
                    throw new InvalidDataException($"Falta o está incompleto {path}/eMMC/{name}.");
            }
            if (lookup(path + "/raw_based") != null) throw new InvalidDataException("La emuMMC por archivos tiene un marcador raw_based contradictorio.");
        }
    }

    private static Ini Parse(byte[] bytes)
    {
        Encoding encoding;
        byte[] preamble;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { encoding = new UTF8Encoding(false, true); preamble = bytes[..3]; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); preamble = bytes[..2]; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); preamble = bytes[..2]; }
        else { encoding = new UTF8Encoding(false, true); preamble = []; }
        string text;
        try { text = encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length); }
        catch (DecoderFallbackException) { encoding = Encoding.Latin1; text = encoding.GetString(bytes); preamble = []; }
        if (text.Contains('\0')) throw new InvalidDataException("emummc.ini contiene caracteres nulos.");
        var fields = new Dictionary<string, Field>(StringComparer.Ordinal);
        bool inEmu = false, found = false;
        foreach (Match line in Regex.Matches(text, @"[^\r\n]+"))
        {
            string trimmed = line.Value.Trim();
            if (trimmed.Length == 0 || trimmed[0] is ';' or '#') continue;
            if (trimmed[0] == '[')
            {
                if (!trimmed.EndsWith(']')) throw new InvalidDataException("Una sección de emummc.ini es inválida.");
                inEmu = trimmed == "[emummc]";
                if (inEmu && found) throw new InvalidDataException("Hay varias secciones emummc y la selección es ambigua.");
                found |= inEmu;
                continue;
            }
            if (!inEmu) continue;
            int equals = line.Value.IndexOf('=');
            if (equals < 1) throw new InvalidDataException("Una opción de emummc.ini no tiene un formato válido.");
            string key = line.Value[..equals].Trim();
            int valueStart = equals + 1;
            while (valueStart < line.Length && char.IsWhiteSpace(line.Value[valueStart])) valueStart++;
            int valueEnd = line.Length;
            int comment = line.Value.IndexOfAny([';', '#'], valueStart);
            if (comment >= 0) valueEnd = comment;
            while (valueEnd > valueStart && char.IsWhiteSpace(line.Value[valueEnd - 1])) valueEnd--;
            if (!fields.TryAdd(key, new(line.Value[valueStart..valueEnd], line.Index + valueStart, valueEnd - valueStart)))
                throw new InvalidDataException($"La opción {key} aparece varias veces en emummc.ini.");
        }
        if (!found) throw new InvalidDataException("Falta la sección [emummc] en emummc.ini.");
        return new(text, encoding, preamble, fields);
    }
    private static uint ParseSector(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        if (value.Length == 0 || !uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint result))
            throw new InvalidDataException("El sector de emummc.ini no es un número hexadecimal válido.");
        return result;
    }
    private static uint ReadMarker(string path)
    {
        RejectLink(path);
        byte[] marker = File.ReadAllBytes(path);
        if (marker.Length != 4) throw new InvalidDataException($"El marcador {path} no contiene cuatro bytes.");
        uint sector = BinaryPrimitives.ReadUInt32LittleEndian(marker);
        if (sector == 0) throw new InvalidDataException("Un marcador raw_based apunta al sector cero.");
        return sector;
    }
    private static bool IsRawMarker(string path)
    {
        string[] parts = ValidateRelative(path).Split('/');
        return parts.Length == 3 && parts[0].Equals("emuMMC", StringComparison.OrdinalIgnoreCase) && parts[2].Equals("raw_based", StringComparison.OrdinalIgnoreCase);
    }
    private static string ValidateRelative(string path)
    {
        string normalized = path.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains(':') || normalized.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException($"Ruta relativa no segura: {path}");
        return normalized;
    }
    private static string CombineSafe(string root, string relative)
    {
        string normalized = ValidateRelative(relative);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("La ruta sale de la carpeta de datos.");
        return full;
    }
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("No se admiten enlaces ni puntos de montaje en emuMMC.");
    }
}
