using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;

namespace InspectAzureAI.Eval.Tools.Skills;

/// <summary>
/// Port of <c>tool/_tools/_skill/install.py</c> <c>install_skills</c>: writes each skill's SKILL.md and its
/// scripts (marked executable), references and assets under <c>{dir}/{name}/</c> in a sandbox, chowning every
/// file to <c>user</c> when one is given.
/// </summary>
public static class SkillInstaller
{
    /// <summary>Port of the 60 second timeout each install command runs under.</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Port of <c>install_skills(skills, sandbox: str | None, user, dir)</c>: installs into the sample sandbox named
    /// <paramref name="sandbox"/> (null for the default) resolved through the ambient <see cref="SampleContext"/>.
    /// </summary>
    /// <param name="skills">Agent skills to install.</param>
    /// <param name="sandbox">Sandbox environment name to copy skills to.</param>
    /// <param name="user">User to write skills files with.</param>
    /// <param name="dir">Directory to install into (defaults to "./skills", relative to the sandbox working directory).</param>
    /// <param name="cancellationToken">Cancels the install.</param>
    /// <returns>A <see cref="SkillInfo"/> per skill with its name, description, instructions and SKILL.md location.</returns>
    public static Task<IReadOnlyList<SkillInfo>> InstallSkillsAsync(
        IEnumerable<SkillSource> skills,
        string? sandbox = null,
        string? user = null,
        string? dir = null,
        CancellationToken cancellationToken = default) =>
        InstallSkillsAsync(skills, SampleContext.Require().Sandbox(sandbox), user, dir, cancellationToken);

    /// <summary>Port of <c>install_skills(skills, sandbox: SandboxEnvironment, user, dir)</c>: installs into <paramref name="sandbox"/> directly.</summary>
    public static async Task<IReadOnlyList<SkillInfo>> InstallSkillsAsync(
        IEnumerable<SkillSource> skills,
        ISandboxEnvironment sandbox,
        string? user = null,
        string? dir = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(sandbox);

        // resolve and validate skills before sandbox setup
        var resolved = SkillReader.ReadSkills(skills);
        SkillReader.CheckUniqueSkillNames(resolved);

        async Task<string> CheckedExec(IReadOnlyList<string> cmd, string? asUser = null)
        {
            var result = await sandbox.ExecAsync(cmd, user: asUser ?? user, timeout: CommandTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException($"Error executing command {string.Join(' ', cmd)}: {result.Stderr}");
            }

            return result.Stdout.Trim();
        }

        async Task WriteSkillFile(string file, SkillFile contents, bool executable = false)
        {
            // write the file (a host path is read as bytes)
            await sandbox.WriteFileAsync(file, await contents.ReadAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

            // change user if required
            if (!string.IsNullOrEmpty(user))
            {
                await CheckedExec(["chown", user, file], asUser: "root").ConfigureAwait(false);
            }

            // mark executable if required
            if (executable)
            {
                await CheckedExec(["chmod", "+x", file]).ConfigureAwait(false);
            }
        }

        // determine skills dir
        var skillsDir = string.IsNullOrEmpty(dir) ? "skills" : dir;
        if (!skillsDir.StartsWith('/'))
        {
            skillsDir = PosixJoin(await CheckedExec(["sh", "-c", "pwd"]).ConfigureAwait(false), skillsDir);
        }

        // install skills
        var infos = new List<SkillInfo>();
        foreach (var skill in resolved)
        {
            var skillDir = PosixJoin(skillsDir, skill.Name);
            var info = new SkillInfo(skill.Name, skill.Description, skill.Instructions, PosixJoin(skillDir, "SKILL.md"));
            await WriteSkillFile(info.Location, skill.SkillMd()).ConfigureAwait(false);
            await WriteSupportingFiles(skillDir, "scripts", skill.Scripts, executable: true).ConfigureAwait(false);
            await WriteSupportingFiles(skillDir, "references", skill.References).ConfigureAwait(false);
            await WriteSupportingFiles(skillDir, "assets", skill.Assets).ConfigureAwait(false);
            infos.Add(info);
        }

        return infos;

        async Task WriteSupportingFiles(string skillDir, string subdir, IReadOnlyDictionary<string, SkillFile> files, bool executable = false)
        {
            foreach (var (file, contents) in files)
            {
                await WriteSkillFile(PosixJoin(PosixJoin(skillDir, subdir), file), contents, executable).ConfigureAwait(false);
            }
        }
    }

    private static string PosixJoin(string left, string right) =>
        right.StartsWith('/') ? right : left.EndsWith('/') ? left + right : left + "/" + right;
}
