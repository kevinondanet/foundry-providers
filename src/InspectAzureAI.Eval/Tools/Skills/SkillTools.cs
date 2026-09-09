using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools.Skills;

/// <summary>
/// Port of <c>tool/_tools/_skill/tool.py</c>: the <c>skill</c> tool that makes agent skills available to a model.
/// The skills are read and validated when the tool is created; they are installed into the sandbox lazily on
/// the first call (once per sample and instance, recorded in <see cref="InstalledSkills"/> in the store, the
/// install serialised by a per-instance lock as in Python) and each call returns the named skill's instructions.
/// </summary>
public static class SkillTools
{
    /// <summary>Port of the <c>&lt;skills_instructions&gt;</c> block of the tool description (derived from the Claude Code and Codex CLI skills prompts, typo included).</summary>
    public const string SkillsInstructions =
        "Invoke a skill to get specialized instructions for a task.\n"
        + "\n"
        + "<skills_instructions>\n"
        + "Skills provide specialized capabilities and domain knowledge. Each skill contains instructions, and may include scripts, references, and assets.\n"
        + "\n"
        + "When to use:\n"
        + "- Before starting a task, check if any skill in <available_skills> matches the request\n"
        + "- Use the description field to determine relevance\n"
        + "\n"
        + "How to invoke:\n"
        + "- Call this tool with the skill name only\n"
        + "- Example: command: \"pdf\"\n"
        + "- Example: command: \"research\"\n"
        + "\n"
        + "After invoking:\n"
        + "- Follow the instructions returned by the skill\n"
        + "- If the skill references folders like `references/`, `scripts/`, or `assets/` load only the specific files needed — don't bulk-load\n"
        + "- If specific files are referended, their paths are relative to the indicated Base Path.\n"
        + "- If scripts exist, prefer running them over rewriting equivalent code\n"
        + "- If assets or templates exist, reuse them instead of recreating from scratch\n"
        + "\n"
        + "Multiple skills:\n"
        + "- If multiple skills apply, choose the minimal set and invoke them in sequence\n"
        + "- State which skills you're using and why\n"
        + "\n"
        + "Important:\n"
        + "- When a skill is relevant, invoke this tool IMMEDIATELY as your first action — NEVER just mention a skill without actually calling this tool\n"
        + "- Invoke the skill tool BEFORE generating any other response about the task\n"
        + "- Only invoke skills listed in <available_skills>\n"
        + "- Do not invoke a skill that is already running\n"
        + "- If a skill can't be applied (missing files, unclear instructions), state the issue and proceed with the best alternative\n"
        + "</skills_instructions>";

    /// <summary>The description of the <c>command</c> parameter (the Python <c>execute</c> docstring).</summary>
    public const string CommandDescription = "The skill name (no arguments). E.g., \"pdf\" or \"xlsx\"";

    // Per-instance locks serialise the lazy install on the first concurrent call so parallel skill tool calls
    // don't race on file writes/chown/chmod in the sandbox (Python's module-level `_install_locks`).
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallLocks = new(StringComparer.Ordinal);

    /// <summary>
    /// Port of <c>skill()</c>: makes <paramref name="skills"/> available to an agent. See <see cref="Skill"/> for
    /// details on defining skills; each source is a directory containing a SKILL.md or a full specification.
    /// </summary>
    /// <param name="skills">Agent skill specifications: directories containing a skill or full <see cref="Skill"/> specifications.</param>
    /// <param name="instance">Optional instance name for the skill store; enables multiple independent skill tool instances within a single sample (e.g. subagents with different skill sets).</param>
    /// <param name="sandbox">Sandbox environment name to copy skills to.</param>
    /// <param name="user">User to write skills files with.</param>
    /// <param name="dir">Directory to install into (defaults to "./skills").</param>
    /// <exception cref="SkillParsingError">A SKILL.md is missing, malformed or invalid.</exception>
    /// <exception cref="ArgumentException">Two skills share a name.</exception>
    public static ToolDef Skill(
        IReadOnlyList<SkillSource> skills,
        string? instance = null,
        string? sandbox = null,
        string? user = null,
        string? dir = null)
    {
        ArgumentNullException.ThrowIfNull(skills);

        // resolve skills and validate uniqueness
        var resolvedSkills = SkillReader.ReadSkills(skills);
        SkillReader.CheckUniqueSkillNames(resolvedSkills);

        var parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam> { ["command"] = ToolParam.Of("string", CommandDescription) },
            Required = ["command"],
        };

        var description = Description(resolvedSkills);

        return new ToolDef("skill", description, parameters, async (arguments, cancellationToken) =>
        {
            ToolInputValidator.Validate(arguments, parameters);
            var command = (string)arguments["command"]!;

            // see if we need to install the skills (double-checked under a per-instance lock so concurrent
            // first-callers don't both run install_skills and race on sandbox file writes)
            var installed = Store.StoreAs<InstalledSkills>(instance);
            if (installed.Skills is null)
            {
                var gate = InstallLocks.GetOrAdd(instance ?? "", _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (installed.Skills is null)
                    {
                        installed.Skills = (await SkillInstaller
                            .InstallSkillsAsync(resolvedSkills.Select(SkillSource.FromSkill), sandbox, user, dir, cancellationToken)
                            .ConfigureAwait(false)).ToList();
                    }
                }
                finally
                {
                    gate.Release();
                }
            }

            // lookup skill
            var skillInfo = installed.Skills!.FirstOrDefault(si => si.Name == command)
                ?? throw new ToolError($"Unknown skill: {command}");

            // return indicating the skill is running along with skill dir/instructions
            return string.Join("\n",
            [
                $"<command-message>The \"{skillInfo.Name}\" skill is running</command-message>",
                $"<command-name>{skillInfo.Name}</command-name>",
                "",
                $"Base Path: {skillInfo.Location}",
                "",
                skillInfo.Instructions,
            ]);
        })
        { Parallel = true };
    }

    /// <summary>
    /// The full tool description as Python builds it: <c>dedent(rf"...")</c> over a template whose interpolated
    /// <c>&lt;available_skills&gt;</c> lines start at column 0, so <c>dedent</c> finds no common margin and the
    /// template lines keep their four-space indentation (reproduced here so the <c>ToolInfo</c> matches exactly).
    /// </summary>
    public static string Description(IEnumerable<Skill> skills)
    {
        var body = string.Join("\n", SkillsInstructions.Split('\n').Select(line => line.Length == 0 ? "" : "    " + line));
        return $"\n{body}\n\n    {AvailableSkills(skills)}\n";
    }

    /// <summary>Port of <c>_available_skills</c>: the <c>&lt;available_skills&gt;</c> block of the tool description listing each skill's name and description.</summary>
    public static string AvailableSkills(IEnumerable<Skill> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        var prompt = new List<string> { "<available_skills>" };
        foreach (var skill in skills)
        {
            prompt.Add("<skill>");
            prompt.Add($"<name>{skill.Name}</name>");
            prompt.Add($"<description>{skill.Description}</description></skill>");
        }

        prompt.Add("</available_skills>");
        return string.Join("\n", prompt);
    }
}

/// <summary>Port of <c>InstalledSkills</c>: the per-sample (and per-instance) record of the skills installed in the sandbox, stored under <c>InstalledSkills[:{instance}]:skills</c>.</summary>
public sealed class InstalledSkills : StoreModel
{
    /// <summary>The installed skills, or null until the first <c>skill</c> tool call installs them.</summary>
    public List<SkillInfo>? Skills
    {
        get => Get<List<SkillInfo>?>(null);
        set => Set(value);
    }
}
