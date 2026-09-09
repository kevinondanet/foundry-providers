using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Tools.Skills;

/// <summary>
/// Port of <c>tool/_tools/_skill/read.py</c> (<c>read_skills</c>, <c>_read_skill</c>, <c>_parse_frontmatter</c>,
/// <c>_validate_frontmatter</c>, <c>_enumerate_directory</c>) and <c>validate.py</c> <c>check_unique_skill_names</c>:
/// SKILL.md discovery, front matter parsing and validation with the messages Python's jsonschema
/// <c>Draft7Validator</c> produces.
/// </summary>
public static class SkillReader
{
    /// <summary>
    /// Port of <c>read_skills</c>: each directory source is read and validated (see the
    /// <see href="https://agentskills.io/specification">agent skills specification</see>); a <see cref="Skill"/>
    /// source passes through. Throws <see cref="SkillParsingError"/> if a SKILL.md is missing, malformed or invalid.
    /// </summary>
    public static IReadOnlyList<Skill> ReadSkills(IEnumerable<SkillSource> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        return skills.Select(source => source.Skill ?? ReadSkill(source.Directory!)).ToList();
    }

    /// <summary>Port of <c>_read_skill</c>: reads and validates the SKILL.md in <paramref name="location"/> and enumerates its <c>scripts/</c>, <c>references/</c> and <c>assets/</c>.</summary>
    public static Skill ReadSkill(string location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var skillDir = Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (skillDir.Length == 0)
        {
            skillDir = Path.GetFullPath(location);
        }

        if (!Directory.Exists(skillDir))
        {
            if (File.Exists(skillDir))
            {
                throw new SkillParsingError($"Skill location is not a directory: {skillDir}");
            }

            throw new SkillParsingError($"Skill directory does not exist: {skillDir}");
        }

        var skillFile = Path.Combine(skillDir, "SKILL.md");
        if (!File.Exists(skillFile))
        {
            throw new SkillParsingError($"SKILL.md not found in: {skillDir}");
        }

        var content = File.ReadAllText(skillFile, System.Text.Encoding.UTF8);
        var (frontmatter, instructions) = ParseFrontmatter(content);
        ValidateFrontmatter(frontmatter);

        var skillName = (string)frontmatter["name"]!;
        var dirName = Path.GetFileName(skillDir);
        if (skillName != dirName)
        {
            throw new SkillParsingError($"Skill name '{skillName}' does not match directory name '{dirName}'");
        }

        return new Skill(skillName, (string)frontmatter["description"]!, instructions)
        {
            Scripts = EnumerateDirectory(Path.Combine(skillDir, "scripts")),
            References = EnumerateDirectory(Path.Combine(skillDir, "references")),
            Assets = EnumerateDirectory(Path.Combine(skillDir, "assets")),
            License = (string?)frontmatter["license"],
            Compatibility = (string?)frontmatter["compatibility"],
            Metadata = frontmatter["metadata"]?.AsObject(),
            AllowedTools = (string?)frontmatter["allowed-tools"],
        };
    }

    /// <summary>Port of <c>check_unique_skill_names</c>: an <see cref="ArgumentException"/> (Python's <c>ValueError</c>) when two skills share a name.</summary>
    public static void CheckUniqueSkillNames(IEnumerable<Skill> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skill in skills)
        {
            if (!seen.Add(skill.Name))
            {
                throw new ArgumentException($"Duplicate skill name '{skill.Name}'. Each skill must have a unique name.", nameof(skills));
            }
        }
    }

    /// <summary>
    /// Port of <c>_parse_frontmatter</c>: content split on the first two <c>---</c> markers (Python's
    /// <c>str.split("---", 2)</c>); no leading marker or fewer than two markers means no front matter, and front
    /// matter that is not a mapping counts as empty. A YAML error is a <see cref="SkillParsingError"/>.
    /// </summary>
    public static (JsonObject Frontmatter, string Body) ParseFrontmatter(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.StartsWith("---", StringComparison.Ordinal))
        {
            return (new JsonObject(), content);
        }

        var second = content.IndexOf("---", 3, StringComparison.Ordinal);
        if (second < 0)
        {
            return (new JsonObject(), content);
        }

        var frontmatterStr = content[3..second].Trim();
        var body = content[(second + 3)..].TrimStart('\n');

        JsonNode? frontmatter;
        try
        {
            frontmatter = SkillYaml.Parse(frontmatterStr);
        }
        catch (SkillYamlException ex)
        {
            throw new SkillParsingError($"Invalid YAML frontmatter: {ex.Message}");
        }

        return (frontmatter as JsonObject ?? new JsonObject(), body);
    }

    /// <summary>
    /// Port of <c>_validate_frontmatter</c> against <c>_skill_schema()</c>: the errors are worded and ordered the way
    /// jsonschema's <c>Draft7Validator.iter_errors</c> reports them (required properties, then each property's type /
    /// maxLength / pattern in schema order, then additional properties).
    /// </summary>
    public static void ValidateFrontmatter(JsonObject frontmatter)
    {
        ArgumentNullException.ThrowIfNull(frontmatter);
        var errors = new List<string>();
        foreach (var required in new[] { "name", "description" })
        {
            if (!frontmatter.ContainsKey(required))
            {
                errors.Add($"'{required}' is a required property");
            }
        }

        CheckString(frontmatter, "name", 64, Skill.NamePattern, errors);
        CheckString(frontmatter, "description", 1024, null, errors);
        CheckString(frontmatter, "license", null, null, errors);
        CheckString(frontmatter, "compatibility", 500, null, errors);
        if (frontmatter.TryGetPropertyValue("metadata", out var metadata) && metadata is not JsonObject)
        {
            errors.Add($"{SkillYaml.PyRepr(metadata)} is not of type 'object'");
        }

        CheckString(frontmatter, "allowed-tools", null, null, errors);

        var known = new HashSet<string>(["name", "description", "license", "compatibility", "metadata", "allowed-tools"], StringComparer.Ordinal);
        var extras = frontmatter.Select(kv => kv.Key).Where(key => !known.Contains(key)).ToList();
        if (extras.Count == 1)
        {
            errors.Add($"Additional properties are not allowed ({SkillYaml.PyReprString(extras[0])} was unexpected)");
        }
        else if (extras.Count > 1)
        {
            errors.Add($"Additional properties are not allowed ({string.Join(", ", extras.Select(SkillYaml.PyReprString))} were unexpected)");
        }

        if (errors.Count > 0)
        {
            var message = string.Join("\n", new[] { $"Found {errors.Count} validation error(s) parsing SKILL.md:" }.Concat(errors.Select(e => $"- {e}")));
            throw new SkillParsingError(message, errors);
        }
    }

    private static void CheckString(JsonObject frontmatter, string property, int? maxLength, string? pattern, List<string> errors)
    {
        if (!frontmatter.TryGetPropertyValue(property, out var node))
        {
            return;
        }

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            errors.Add($"{SkillYaml.PyRepr(node)} is not of type 'string'");
            return;
        }

        if (maxLength is { } max && text.EnumerateRunes().Count() > max)
        {
            errors.Add($"{SkillYaml.PyReprString(text)} is too long");
        }

        if (pattern is not null && !Skill.NameRegex().IsMatch(text))
        {
            errors.Add($"{SkillYaml.PyReprString(text)} does not match {SkillYaml.PyReprString(pattern)}");
        }
    }

    /// <summary>
    /// Port of <c>_enumerate_directory</c>: every file under <paramref name="dirPath"/> (recursively) keyed by its
    /// relative path, skipping any path component starting with '.' or '_'. Deviation: keys always use '/' (the
    /// sandbox is POSIX) and are sorted for a stable install order.
    /// </summary>
    public static IReadOnlyDictionary<string, SkillFile> EnumerateDirectory(string dirPath)
    {
        ArgumentNullException.ThrowIfNull(dirPath);
        var result = new Dictionary<string, SkillFile>(StringComparer.Ordinal);
        if (!Directory.Exists(dirPath))
        {
            return result;
        }

        var root = Path.GetFullPath(dirPath);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(part => part.StartsWith('.') || part.StartsWith('_')))
            {
                continue;
            }

            result[string.Join('/', parts)] = SkillFile.FromPath(file);
        }

        return result;
    }
}
