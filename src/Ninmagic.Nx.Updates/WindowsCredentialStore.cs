using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Ninmagic.Nx.Updates;

internal static class WindowsCredentialStore
{
    private const string Target = "NinmagicNX:GitHub:ANE-JV/ninmagic-nx";
    private const uint Generic = 1;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        internal uint Flags, Type;
        internal string TargetName, Comment;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        internal uint CredentialBlobSize;
        internal IntPtr CredentialBlob;
        internal uint Persist, AttributeCount;
        internal IntPtr Attributes;
        internal string TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);

    internal static string? Read()
    {
        if (!CredRead(Target, Generic, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, "Windows no pudo leer el acceso guardado para las actualizaciones.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize > 4096) throw new InvalidDataException("El acceso guardado no es válido.");
            var bytes = new byte[credential.CredentialBlobSize];
            try { Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length); return Encoding.UTF8.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }
    internal static void Save(string token)
    {
        token = token.Trim();
        if (token.Length < 10 || token.Length > 2048 || token.Any(c => c <= 32 || c > 126))
            throw new ArgumentException("Introduce un token válido de GitHub con permiso de lectura del repositorio.");
        var bytes = Encoding.UTF8.GetBytes(token);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential { Type = Generic, TargetName = Target, Comment = "Actualizaciones privadas de ninmagic-nx",
                CredentialBlobSize = (uint)bytes.Length, CredentialBlob = pointer, Persist = 2,
                UserName = "GitHub repository access", TargetAlias = "" };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo guardar el acceso en Windows.");
        }
        finally { Array.Clear(bytes); Marshal.Copy(bytes, 0, pointer, bytes.Length); Marshal.FreeHGlobal(pointer); }
    }
    internal static void Delete()
    {
        if (!CredDelete(Target, Generic, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo olvidar el acceso guardado.");
    }
}
