using Ninmagic.Nx.Core;
using Ninmagic.Nx.Windows;

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
    }

    private static void Check(bool valid, string name)
    {
        if (!valid) throw new InvalidOperationException("Windows adapter test failed: " + name);
    }
}
