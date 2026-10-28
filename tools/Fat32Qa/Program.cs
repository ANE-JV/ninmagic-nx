using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Ninmagic.Nx.Core;

foreach (long bytes in new[] { 4L << 30, 128_000_000_000L })
{
    string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../..", $"fat32-{bytes}.img"));
    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    if (!Native.DeviceIoControl(output.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        throw new IOException("Cannot mark temporary QA file sparse: " + Marshal.GetLastWin32Error());
    output.SetLength(bytes);
    var data = new PartitionInfo(1, 0x0C, 32768, checked((uint)(bytes / 512 - 32768 - 2048)));
    var plan = new MigrationPlan(data, Array.Empty<PartitionInfo>(), new Dictionary<uint, uint>(), 0, "FAT32", bytes);
    DiskLayout.WriteMbr(output, plan);
    Fat32Formatter.Format(output, data, "QA NINMAGIC", CancellationToken.None);
    output.Flush(true);
    Console.WriteLine(path);
}
internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(SafeFileHandle handle, uint control, IntPtr input, uint inSize, IntPtr output, uint outSize, out uint result, IntPtr overlapped);
}
