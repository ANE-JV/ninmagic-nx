using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ninmagic.Nx.Updates;

internal static class UpdateFiles
{
    internal static bool IsSha256(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);
    internal static string Hash(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }
    internal static void VerifyHash(Stream input, string expected)
    {
        if (!IsSha256(expected)) throw new InvalidDataException("La huella de la actualización no es válida.");
        input.Position = 0;
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(input), Convert.FromHexString(expected)))
            throw new InvalidDataException("El archivo ha cambiado o su SHA-256 no coincide. La aplicación actual se conserva.");
        input.Position = 0;
    }
    internal static void ValidateExecutable(string path, string expectedVersion)
    {
        if (!UpdateVersions.TryParseStable(expectedVersion, out var expected)) throw new InvalidDataException("Versión de actualización no válida.");
        RejectReparseAncestors(path);
        using (var input = File.OpenRead(path))
        using (var pe = new PEReader(input))
        {
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader is not { } header ||
                header.Magic != PEMagic.PE32Plus || (header.Subsystem != Subsystem.WindowsGui && header.Subsystem != Subsystem.WindowsCui))
                throw new InvalidDataException("La descarga no es una aplicación Windows de 64 bits.");
        }
        var version = FileVersionInfo.GetVersionInfo(path);
        if (version.FileMajorPart != expected.Major || version.FileMinorPart != expected.Minor || version.FileBuildPart != expected.Build ||
            version.FilePrivatePart != 0 || !string.Equals(version.ProductName, "ninmagic-nx", StringComparison.Ordinal))
            throw new InvalidDataException("El producto o la versión del ejecutable no coincide con la actualización publicada.");
    }
    internal static void RejectReparseAncestors(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("La actualización necesita una ruta local absoluta.");
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("La actualización no admite enlaces ni carpetas redirigidas.");
            current = Path.GetDirectoryName(current);
        }
    }
    internal static void ValidateInstallTarget(string path)
    {
        RejectReparseAncestors(path);
        if (Path.GetFileName(path) != "ninmagic-nx.exe" || !File.Exists(path)) throw new IOException("No se encontró la aplicación Windows que se va a actualizar.");
        var root = Path.GetPathRoot(path) ?? throw new IOException("Ruta de aplicación no válida.");
        if (new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new IOException("Mueve ninmagic-nx a una carpeta del disco interno del PC antes de actualizar. No se actualiza desde una microSD ni una unidad de red.");
    }
    internal static bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private static SecurityIdentifier SystemSid => new(WellKnownSidType.LocalSystemSid, null);
    private static SecurityIdentifier AdminSid => new(WellKnownSidType.BuiltinAdministratorsSid, null);
    internal static void CreateSecureDirectory(string path)
    {
        RejectReparseAncestors(path);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("La carpeta temporal ya existe.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var owner = IsElevated ? AdminSid : WindowsIdentity.GetCurrent().User ?? throw new IOException("No se pudo identificar al usuario de Windows.");
        security.SetOwner(owner);
        foreach (var sid in new[] { owner, SystemSid, AdminSid }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }
    internal static void ValidateSecureDirectory(string path)
    {
        RejectReparseAncestors(path);
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var trusted = new HashSet<string>(StringComparer.Ordinal) { SystemSid.Value, AdminSid.Value };
        if (!IsElevated) trusted.Add(WindowsIdentity.GetCurrent().User!.Value);
        if (!security.AreAccessRulesProtected || security.GetOwner(typeof(SecurityIdentifier)) is not { } owner || !trusted.Contains(owner.Value))
            throw new IOException("La carpeta temporal no tiene los permisos de protección esperados.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !trusted.Contains(rule.IdentityReference.Value))
                throw new IOException("La carpeta temporal permite acceso a una cuenta no autorizada.");
    }

    // Prevent replacing the protected stage directory after checking its DACL.
    internal static SafeFileHandle LockDirectory(string path)
    {
        RejectReparseAncestors(path);
        var handle = CreateFileW(path, 0x80, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "No se pudo proteger la carpeta durante la actualización."); }
        var finalPath = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, finalPath, (uint)finalPath.Capacity, 0);
        var resolved = finalPath.ToString();
        if (resolved.StartsWith("\\\\?\\", StringComparison.Ordinal)) resolved = resolved[4..];
        if (length == 0 || length >= finalPath.Capacity || !string.Equals(Path.GetFullPath(path).TrimEnd('\\'), resolved.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        { handle.Dispose(); throw new IOException("La carpeta cambió durante la preparación de la actualización."); }
        try { RejectReparseAncestors(path); }
        catch { handle.Dispose(); throw; }
        return handle;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}

public static class UpdateInstanceGuard
{
    public static IDisposable? TryAcquire(string executablePath)
    {
        var canonical = Path.GetFullPath(executablePath).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
        var mutex = new Mutex(false, "Local\\NinmagicNX-" + key);
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { mutex.Dispose(); return null; }
        return new Lease(mutex);
    }
    private sealed class Lease(Mutex mutex) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (disposed) return; disposed = true; mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
