using System.Text.RegularExpressions;

namespace Ninmagic.Nx.Core;

/// <summary>All backup paths are untrusted, including JSON created by this application.</summary>
public static partial class SafePaths
{
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|CLOCK\$|COM[1-9¹²³]|LPT[1-9¹²³]) *(?:\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceName();

    public static string NormalizeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 32000 || Path.IsPathRooted(relative))
            throw new InvalidDataException("La copia contiene una ruta absoluta, vacía o demasiado larga.");
        var parts = relative.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c)) || DeviceName().IsMatch(part))
                throw new InvalidDataException($"Ruta de copia no válida: {relative}");
        }
        return string.Join('/', parts);
    }

    public static string Resolve(string root, string relative)
    {
        var clean = NormalizeRelative(relative);
        var fullRoot = Path.GetFullPath(root);
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(fullRoot, clean.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Una ruta de la copia sale de su carpeta.");
        RejectReparseAncestors(result);
        return result;
    }

    public static void RejectReparseAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"No se admiten enlaces ni carpetas redirigidas: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }
    }
}
