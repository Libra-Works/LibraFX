namespace LiberaFx.Services.Extensions;

public sealed record OperatorSkillInstallResult(string SkillPath, string? BackupPath, bool Changed);

public static class OperatorSkillPackage
{
    public const string SkillName = "liberafx-operator";
    public static string BundledSkillPath => Path.Combine(AppContext.BaseDirectory, "Skills", SkillName, "SKILL.md");

    public static async Task<OperatorSkillInstallResult> InstallAsync(string skillsDirectory, bool replace = false)
    {
        if (string.IsNullOrWhiteSpace(skillsDirectory) || !Path.IsPathFullyQualified(skillsDirectory))
            throw new ArgumentException("Enter an absolute skills directory.");
        var source = await File.ReadAllBytesAsync(BundledSkillPath).ConfigureAwait(false);
        var directory = Path.Combine(Path.GetFullPath(skillsDirectory), SkillName);
        var target = Path.Combine(directory, "SKILL.md");
        if (File.Exists(target))
        {
            var existing = await File.ReadAllBytesAsync(target).ConfigureAwait(false);
            if (source.AsSpan().SequenceEqual(existing))
                return new(target, null, false);
            if (!replace)
                throw new IOException("SKILL.md already exists. Enable replacement to update it with a backup.");
        }
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".skill-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, source).ConfigureAwait(false);
            string? backup = null;
            if (replace && File.Exists(target))
            {
                backup = Path.Combine(directory, $"SKILL.md.backup-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}");
                File.Replace(temporary, target, backup);
            }
            else
                File.Move(temporary, target);
            return new(target, backup, true);
        }
        finally { File.Delete(temporary); }
    }
}
