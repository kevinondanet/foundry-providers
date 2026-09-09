using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InspectAzureAI.Eval.Tools.Skills;

/// <summary>
/// Port of <c>tool/_tools/_skill/types.py</c> <c>Skill</c>: an agent skill specification
/// (see https://agentskills.io/specification). The field constraints pydantic enforces (name pattern and length,
/// description and compatibility lengths) are checked when the record is constructed and raise
/// <see cref="ArgumentException"/> with pydantic's wording.
/// </summary>
/// <param name="Name">Skill name. Max 64 characters. Lowercase letters, numbers, and hyphens only. Must not start or end with a hyphen.</param>
/// <param name="Description">Describes what the skill does and when to use it. Max 1024 characters.</param>
/// <param name="Instructions">
/// Skill instructions: the information agents need to perform the task effectively including step-by-step
/// instructions, examples of inputs and outputs, and common edge cases. The agent loads all of it once it
/// activates the skill, so keep it under 500 lines and mention <c>scripts/</c>, <c>references/</c> and
/// <c>assets/</c> files explicitly so models know to read them as required.
/// </param>
public sealed partial record Skill(string Name, string Description, string Instructions)
{
    /// <summary>Port of the pydantic <c>name</c> pattern: lowercase letters, numbers and hyphens, not starting or ending with a hyphen.</summary>
    public const string NamePattern = "^[a-z0-9]([a-z0-9-]*[a-z0-9])?$";

    public const int NameMaxLength = 64;

    public const int DescriptionMaxLength = 1024;

    public const int CompatibilityMaxLength = 500;

    public string Name { get; init; } = ValidateName(Name);

    public string Description { get; init; } = ValidateLength(Description, DescriptionMaxLength, nameof(Description));

    public string Instructions { get; init; } = Instructions ?? throw new ArgumentNullException(nameof(Instructions));

    /// <summary>
    /// Executable code that agents can run, keyed by the path relative to <c>scripts/</c>. Scripts should be
    /// self-contained or clearly document dependencies, include helpful error messages and handle edge cases.
    /// </summary>
    public IReadOnlyDictionary<string, SkillFile> Scripts { get; init; } = new Dictionary<string, SkillFile>(StringComparer.Ordinal);

    /// <summary>Additional documentation agents read on demand (<c>REFERENCE.md</c>, <c>FORMS.md</c>, domain files), keyed by the path relative to <c>references/</c>.</summary>
    public IReadOnlyDictionary<string, SkillFile> References { get; init; } = new Dictionary<string, SkillFile>(StringComparer.Ordinal);

    /// <summary>Static resources (templates, images, data files), keyed by the path relative to <c>assets/</c>.</summary>
    public IReadOnlyDictionary<string, SkillFile> Assets { get; init; } = new Dictionary<string, SkillFile>(StringComparer.Ordinal);

    /// <summary>License name or reference to a bundled license file.</summary>
    public string? License { get; init; }

    private readonly string? _compatibility;

    /// <summary>Environment requirements (intended product, system packages, network access, etc.). Max 500 characters.</summary>
    public string? Compatibility
    {
        get => _compatibility;
        init => _compatibility = value is null ? null : ValidateLength(value, CompatibilityMaxLength, nameof(Compatibility));
    }

    /// <summary>Arbitrary key-value mapping for additional metadata.</summary>
    public JsonObject? Metadata { get; init; }

    /// <summary>Space-delimited list of pre-approved tools the skill may use (experimental); the <c>allowed-tools</c> front matter field.</summary>
    public string? AllowedTools { get; init; }

    /// <summary>
    /// Port of <c>Skill.skill_md()</c>: the skill rendered as SKILL.md content, YAML front matter (only the
    /// non-null fields, in Python's order) followed by the instructions. Deviation: PyYAML folds long plain
    /// scalars at 80 columns; this writer keeps each scalar on one line (double-quoted when it is not a safe
    /// plain scalar), which parses to the same values.
    /// </summary>
    public string SkillMd()
    {
        var frontmatter = new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
        };
        if (License is not null)
        {
            frontmatter["license"] = License;
        }

        if (Compatibility is not null)
        {
            frontmatter["compatibility"] = Compatibility;
        }

        if (Metadata is not null)
        {
            frontmatter["metadata"] = Metadata.DeepClone();
        }

        if (AllowedTools is not null)
        {
            frontmatter["allowed-tools"] = AllowedTools;
        }

        return $"---\n{SkillYaml.Dump(frontmatter)}---\n\n{Instructions}";
    }

    private static string ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ValidateLength(name, NameMaxLength, nameof(Name));
        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException($"String should match pattern '{NamePattern}'", nameof(Name));
        }

        return name;
    }

    private static string ValidateLength(string value, int maxLength, string field)
    {
        ArgumentNullException.ThrowIfNull(value, field);
        if (value.EnumerateRunes().Count() > maxLength)
        {
            throw new ArgumentException($"String should have at most {maxLength} characters", field);
        }

        return value;
    }

    [GeneratedRegex(NamePattern)]
    internal static partial Regex NameRegex();
}

/// <summary>
/// Port of the <c>str | bytes | Path</c> union the skill file dictionaries hold: text content, raw bytes, or a
/// host file read when the skill is installed. A <see cref="string"/> converts implicitly to text content and a
/// <see cref="byte"/> array to bytes, as in Python; a host path needs <see cref="FromPath"/>.
/// </summary>
public sealed record SkillFile
{
    private SkillFile()
    {
    }

    public string? Text { get; private init; }

    public byte[]? Bytes { get; private init; }

    /// <summary>Absolute host path of a file copied into the sandbox at install time.</summary>
    public string? Path { get; private init; }

    public static SkillFile FromText(string text) => new() { Text = text ?? throw new ArgumentNullException(nameof(text)) };

    public static SkillFile FromBytes(byte[] bytes) => new() { Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes)) };

    public static SkillFile FromPath(string path) => new() { Path = System.IO.Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path))) };

    public static implicit operator SkillFile(string text) => FromText(text);

    public static implicit operator SkillFile(byte[] bytes) => FromBytes(bytes);

    /// <summary>The file's content as bytes (a host path is read now, as <c>write_skill_file</c> does).</summary>
    public async Task<byte[]> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (Bytes is not null)
        {
            return Bytes;
        }

        if (Text is not null)
        {
            return System.Text.Encoding.UTF8.GetBytes(Text);
        }

        return await File.ReadAllBytesAsync(Path!, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Port of <c>tool/_tools/_skill/types.py</c> <c>SkillInfo</c>: an installed skill's name, description, instructions and SKILL.md location in the sandbox.</summary>
/// <param name="Name">Skill name.</param>
/// <param name="Description">Describes what the skill does and when to use it.</param>
/// <param name="Instructions">Skill instructions.</param>
/// <param name="Location">Full path to the skill description file (SKILL.md) inside the sandbox.</param>
public sealed record SkillInfo(string Name, string Description, string Instructions, string Location);

/// <summary>
/// Port of the <c>str | Path | Skill</c> union <c>skill()</c>, <c>read_skills()</c> and <c>install_skills()</c>
/// accept: either a directory containing a SKILL.md file (a <see cref="string"/> converts implicitly) or a
/// full <see cref="Skills.Skill"/> specification (also implicit).
/// </summary>
public sealed record SkillSource
{
    private SkillSource()
    {
    }

    /// <summary>The skill directory, when the source is a directory.</summary>
    public string? Directory { get; private init; }

    /// <summary>The specification, when the source is a <see cref="Skills.Skill"/>.</summary>
    public Skill? Skill { get; private init; }

    public static SkillSource FromDirectory(string directory) => new() { Directory = directory ?? throw new ArgumentNullException(nameof(directory)) };

    public static SkillSource FromSkill(Skill skill) => new() { Skill = skill ?? throw new ArgumentNullException(nameof(skill)) };

    public static implicit operator SkillSource(string directory) => FromDirectory(directory);

    public static implicit operator SkillSource(Skill skill) => FromSkill(skill);
}

/// <summary>Port of <c>tool/_tools/_skill/read.py</c> <c>SkillParsingError</c>: raised when a SKILL.md is missing, malformed or invalid.</summary>
public sealed class SkillParsingError : Exception
{
    public SkillParsingError(string message, IReadOnlyList<string>? errors = null)
        : base(message)
    {
        Errors = errors ?? [];
    }

    /// <summary>The individual validation messages when the front matter failed schema validation (empty otherwise).</summary>
    public IReadOnlyList<string> Errors { get; }
}
