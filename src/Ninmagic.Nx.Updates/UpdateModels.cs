namespace Ninmagic.Nx.Updates;

public sealed record UpdateInfo(string Version, string Notes, long AssetId, long Size, string Sha256);
public sealed record UpdateProgress(long Completed, long Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Completed / Total, 0, 1) : 0;
}
public sealed record StagedUpdate(string FilePath, string Version, string Sha256, long Size);
public sealed record UpdateInstallResult(bool Success, string Message);
internal sealed record InstallRequest(string TargetExe, string NewExe, string OriginalSha256, string NewSha256,
    string CurrentVersion, string NewVersion, int ParentId, long ParentStartTimeUtcTicks);

internal static class UpdateVersions
{
    internal static bool TryParseStable(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.StartsWith('v') ? text[1..] : text;
        var parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Length > 7 ||
            part.Any(c => c < '0' || c > '9') || part.Length > 1 && part[0] == '0')) return false;
        if (!Version.TryParse(text, out var parsed)) return false;
        version = parsed; return true;
    }
}
