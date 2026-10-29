using Ninmagic.Nx.Tests;
using Ninmagic.Nx.Windows;

int total = 0, failures = 0;
void Test(string name, Action action)
{
    total++;
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + name + "\n" + e); }
}
LayoutTests.Run(Test);
ProgressEstimatorTests.Run(Test);
DiskDisplayTests.Run(Test);
Test("Windows adapter excludes unsafe disk identities and buses", WindowsAdapterTests.Run);
try { total += await BackupTests.RunAsync(); }
catch (Exception e) { failures++; Console.Error.WriteLine("FAIL backup suite\n" + e); }
try { total += await TargetPolicyTests.RunAsync(); }
catch (Exception e) { failures++; Console.Error.WriteLine("FAIL target policy suite\n" + e); }
Console.WriteLine($"RESULT: {total} checks; {failures} failures. All disk writes used simulated images only.");
if (args.Contains("--inventory"))
{
    var disks = await new WindowsDiskAccess().ListDisksAsync(CancellationToken.None);
    Console.WriteLine($"READ-ONLY INVENTORY: {disks.Count} external candidate(s). Administrator: {WindowsDiskAccess.IsAdministrator}");
}
return failures == 0 ? 0 : 1;
