using System.Text.Json;
using Ninmagic.Nx.Core;

namespace Ninmagic.Nx.Tests;

public static class DiskDisplayTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Drive letters are visible while disk access keeps the volume GUID", () =>
        {
            var volume = Volume() with { DriveLetter = "e" };
            var disk = Disk(volume);
            Check(volume.DriveLetterDisplay == "E:" && disk.DriveLettersDisplay == "E:", "Assigned letter missing");
            Check(disk.ToString().StartsWith("E: · Disco 3 · USB reader"), "Letter is not visible at the start of the selector");
            Check(volume.Root == Volume().Root, "Display metadata replaced the stable volume root");
        });
        test("Disk selector shows all assigned letters without duplicating or inventing them", () =>
        {
            var disk = Disk(Volume() with { DriveLetter = "g" }, Volume() with { DriveLetter = "E:" },
                Volume() with { DriveLetter = "g" }, Volume());
            Check(disk.DriveLettersDisplay == "E:, G:", "Multiple letters are missing, duplicated or unordered");
            Check(!disk.ToString().Contains("Sin letra asignada"), "Hidden partition masks the assigned data volume");
        });
        test("A card without an assigned letter is explicitly identified", () =>
        {
            foreach (var disk in new[] { Disk(), Disk(Volume()), Disk(Volume() with { DriveLetter = "\0" }) })
                Check(disk.DriveLettersDisplay == "Sin letra asignada", "No-letter card was given a drive letter");
            foreach (var invalid in new[] { "EF", "7", "E:\\folder", @"\\server\share", "É" })
                Check((Volume() with { DriveLetter = invalid }).DriveLetterDisplay == "Sin letra asignada", "Invalid letter accepted");
        });
        test("Old backups without drive metadata still deserialize and support legacy letter roots", () =>
        {
            const string oldVolume = """
                {"PartitionNumber":1,"Offset":1048576,"Size":32000000000,"Root":"E:\\","FileSystem":"FAT32","Label":"SWITCH"}
                """;
            var volume = JsonSerializer.Deserialize<VolumeInfo>(oldVolume)!;
            Check(volume.DriveLetter == "" && volume.DriveLetterDisplay == "E:", "Existing manifest became incompatible");
            var oldGuidNode = JsonSerializer.SerializeToNode(Volume())!.AsObject();
            oldGuidNode.Remove("DriveLetter");
            var oldGuid = JsonSerializer.Deserialize<VolumeInfo>(oldGuidNode.ToJsonString())!;
            Check(oldGuid.DriveLetter == "" && oldGuid.Root == Volume().Root && oldGuid.DriveLetterDisplay == "Sin letra asignada", "Old GUID root not retained");
        });
        test("Drive-letter changes never change the physical disk identity", () =>
        {
            var original = Disk(Volume() with { DriveLetter = "E" });
            var changed = original with { Volumes = [Volume() with { DriveLetter = "F" }] };
            var json = JsonSerializer.Serialize(changed);
            var restored = JsonSerializer.Deserialize<DiskInfo>(json)!;
            Check(original.Identity == changed.Identity && changed.Identity == restored.Identity, "Presentation changed disk identity");
            Check(restored.DriveLettersDisplay == "F:" && restored.Volumes[0].Root == Volume().Root, "Drive metadata did not round-trip");
            Check(!json.Contains("Display"), "Computed UI strings leaked into the backup manifest");
        });
    }

    private static VolumeInfo Volume() => new(1, 1_048_576, 32_000_000_000,
        @"\\?\Volume{00000000-0000-0000-0000-000000000001}\", "FAT32", "SWITCH");
    private static DiskInfo Disk(params VolumeInfo[] volumes) => new(3, "USB reader", "serial", "unique", 64_000_000_000,
        512, "USB", false, false, false, "MBR", volumes);
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
