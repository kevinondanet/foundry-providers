using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Skills;

namespace InspectAzureAI.Eval.Tests;

/// <summary>
/// The <c>skill</c> tool (<c>tool/_tools/_skill/</c>): SKILL.md reading and front matter validation with
/// jsonschema's messages, <c>skill_md()</c> rendering, installation into a <see cref="FakeSandboxEnvironment"/>
/// and the tool's description, lazy install and result text.
/// </summary>
public class SkillToolTests
{
    private const string ExampleSkills = "/Users/kevinburrowes/Documents/code/inspect_ai/examples/skills/skills";

    private sealed class TempSkills : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "skill-tests-" + Guid.NewGuid().ToString("N"));

        public TempSkills() => Directory.CreateDirectory(Root);

        public string Write(string skill, string skillMd, params (string Path, string Content)[] files)
        {
            var dir = Path.Combine(Root, skill);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), skillMd);
            foreach (var (path, content) in files)
            {
                var full = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }

            return dir;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private const string SystemInfoMd = """
        ---
        name: system-info
        description: >-
          Retrieve detailed Linux system information including OS distribution,
          kernel version and CPU model. Use when the user asks about system specs.
        license: MIT
        compatibility: "Linux: any distribution"  # a comment
        metadata:
          author: inspect
          version: 2
          tags: [linux, sysadmin]
        allowed-tools: bash python
        ---

        # System Information Skill

        1. Run `./scripts/sysinfo.sh` for structured output.
        """;

    private static string Fix(string text) => text.Replace("\r\n", "\n");

    [Fact]
    public void ReadSkill_ParsesFrontMatterAndEnumeratesFiles()
    {
        using var temp = new TempSkills();
        var dir = temp.Write("system-info", SystemInfoMd,
            ("scripts/sysinfo.sh", "#!/bin/sh\nuname -a\n"),
            ("scripts/lib/helper.sh", "echo hi\n"),
            ("scripts/_private.sh", "no\n"),
            ("references/.hidden/notes.md", "no\n"),
            ("references/REFERENCE.md", "ref\n"));

        var skill = SkillReader.ReadSkill(dir);

        Assert.Equal("system-info", skill.Name);
        Assert.Equal("Retrieve detailed Linux system information including OS distribution, kernel version and CPU model. Use when the user asks about system specs.", skill.Description);
        Assert.Equal("MIT", skill.License);
        Assert.Equal("Linux: any distribution", skill.Compatibility);
        Assert.Equal("bash python", skill.AllowedTools);
        Assert.Equal("# System Information Skill\n\n1. Run `./scripts/sysinfo.sh` for structured output.", Fix(skill.Instructions));
        Assert.NotNull(skill.Metadata);
        Assert.Equal("inspect", (string)skill.Metadata["author"]!);
        Assert.Equal(2L, (long)skill.Metadata["version"]!);
        Assert.Equal(["linux", "sysadmin"], skill.Metadata["tags"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal(["scripts/lib/helper.sh", "scripts/sysinfo.sh"], skill.Scripts.Keys.Select(k => "scripts/" + k).ToArray());
        Assert.Equal(Path.Combine(dir, "scripts", "sysinfo.sh"), skill.Scripts["sysinfo.sh"].Path);
        Assert.Equal(["REFERENCE.md"], skill.References.Keys.ToArray());
        Assert.Empty(skill.Assets);
    }

    [Fact]
    public void ReadSkill_MissingName_ReportsRequiredProperty()
    {
        using var temp = new TempSkills();
        var dir = temp.Write("no-name", "---\ndescription: something\n---\nBody\n");

        var ex = Assert.Throws<SkillParsingError>(() => SkillReader.ReadSkill(dir));

        Assert.Equal("Found 1 validation error(s) parsing SKILL.md:\n- 'name' is a required property", ex.Message);
        Assert.Equal(["'name' is a required property"], ex.Errors);
    }

    [Fact]
    public void ReadSkill_NoFrontMatter_ReportsBothRequiredProperties()
    {
        using var temp = new TempSkills();
        var dir = temp.Write("plain", "# Just markdown\n");

        var ex = Assert.Throws<SkillParsingError>(() => SkillReader.ReadSkill(dir));

        Assert.Equal(["'name' is a required property", "'description' is a required property"], ex.Errors);
    }

    [Fact]
    public void ReadSkill_BadYaml_IsInvalidFrontMatter()
    {
        using var temp = new TempSkills();
        var dir = temp.Write("bad-yaml", "---\nname: bad-yaml\n\tdescription: tabs are not allowed\n---\nBody\n");

        var ex = Assert.Throws<SkillParsingError>(() => SkillReader.ReadSkill(dir));

        Assert.StartsWith("Invalid YAML frontmatter: ", ex.Message);
    }

    [Fact]
    public void ReadSkill_SchemaErrors_UseJsonSchemaWording()
    {
        using var temp = new TempSkills();
        var dir = temp.Write("schema", "---\nname: Bad_Name\ndescription: 42\ncompatibility: " + new string('x', 501) + "\nmetadata: nope\nextra: 1\nother: 2\n---\nBody\n");

        var ex = Assert.Throws<SkillParsingError>(() => SkillReader.ReadSkill(dir));

        Assert.Equal(
            [
                "'Bad_Name' does not match '^[a-z0-9]([a-z0-9-]*[a-z0-9])?$'",
                "42 is not of type 'string'",
                "'" + new string('x', 501) + "' is too long",
                "'nope' is not of type 'object'",
                "Additional properties are not allowed ('extra', 'other' were unexpected)",
            ],
            ex.Errors);
        Assert.StartsWith("Found 5 validation error(s) parsing SKILL.md:\n- ", ex.Message);
    }

    [Fact]
    public void ReadSkill_NameMustMatchDirectory()
    {
        using var temp = new TempSkills();
        var dir = temp.Write("folder", "---\nname: other\ndescription: d\n---\nBody\n");

        var ex = Assert.Throws<SkillParsingError>(() => SkillReader.ReadSkill(dir));

        Assert.Equal("Skill name 'other' does not match directory name 'folder'", ex.Message);
    }

    [Fact]
    public void ReadSkill_MissingDirectoryOrSkillMd()
    {
        using var temp = new TempSkills();
        var missing = Path.Combine(temp.Root, "missing");
        var empty = Path.Combine(temp.Root, "empty");
        Directory.CreateDirectory(empty);

        Assert.Equal($"Skill directory does not exist: {missing}", Assert.Throws<SkillParsingError>(() => SkillReader.ReadSkill(missing)).Message);
        Assert.Equal($"SKILL.md not found in: {empty}", Assert.Throws<SkillParsingError>(() => SkillReader.ReadSkill(empty)).Message);
    }

    [Fact]
    public void DuplicateSkillNames_AreRejected()
    {
        var a = new Skill("dup", "one", "i");
        var b = new Skill("dup", "two", "i");

        var ex = Assert.Throws<ArgumentException>(() => SkillTools.Skill([a, b]));

        Assert.StartsWith("Duplicate skill name 'dup'. Each skill must have a unique name.", ex.Message);
    }

    [Fact]
    public void Skill_ValidatesFieldsLikePydantic()
    {
        Assert.Throws<ArgumentException>(() => new Skill("-bad", "d", "i"));
        Assert.Throws<ArgumentException>(() => new Skill("Upper", "d", "i"));
        Assert.Throws<ArgumentException>(() => new Skill(new string('a', 65), "d", "i"));
        Assert.Throws<ArgumentException>(() => new Skill("ok", new string('d', 1025), "i"));
        Assert.Throws<ArgumentException>(() => new Skill("ok", "d", "i") { Compatibility = new string('c', 501) });
        Assert.Equal("a-b-1", new Skill("a-b-1", "d", "i").Name);
    }

    [Fact]
    public void SkillMd_RendersFrontMatterThatReadsBack()
    {
        var skill = new Skill("pdf", "Work with PDFs: extract, merge", "# PDF\n\nDo things.")
        {
            License = "MIT",
            Metadata = new JsonObject { ["version"] = "1.0", ["tags"] = new JsonArray("a", "b"), ["nested"] = new JsonObject { ["k"] = true } },
            AllowedTools = "bash",
        };

        var md = skill.SkillMd();

        Assert.StartsWith("---\nname: pdf\ndescription: \"Work with PDFs: extract, merge\"\nlicense: MIT\nmetadata:\n  version: \"1.0\"\n  tags:\n  - a\n  - b\n  nested:\n    k: true\nallowed-tools: bash\n---\n\n", md);
        Assert.EndsWith("---\n\n# PDF\n\nDo things.", md);
        var (front, body) = SkillReader.ParseFrontmatter(md);
        Assert.Equal("pdf", (string)front["name"]!);
        Assert.Equal(skill.Description, (string)front["description"]!);
        Assert.Equal("1.0", (string)front["metadata"]!["version"]!);
        Assert.True((bool)front["metadata"]!["nested"]!["k"]!);
        Assert.Equal(["a", "b"], front["metadata"]!["tags"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal("bash", (string)front["allowed-tools"]!);
        Assert.Null(front["compatibility"]);
        Assert.Equal("# PDF\n\nDo things.", body);
    }

    [Fact]
    public void ParseFrontmatter_SplitsOnFirstTwoMarkers()
    {
        var (front, body) = SkillReader.ParseFrontmatter("---\nname: a\ndescription: 'it''s'\n---\n\n\nBody --- with dashes\n");
        Assert.Equal("a", (string)front["name"]!);
        Assert.Equal("it's", (string)front["description"]!);
        Assert.Equal("Body --- with dashes\n", body);

        var (none, all) = SkillReader.ParseFrontmatter("---\nunterminated\n");
        Assert.Empty(none);
        Assert.Equal("---\nunterminated\n", all);

        var (list, _) = SkillReader.ParseFrontmatter("---\n- a\n- b\n---\nbody");
        Assert.Empty(list);
    }

    [Fact]
    public async Task InstallSkills_WritesFilesAndMarksScriptsExecutable()
    {
        using var temp = new TempSkills();
        var dir = temp.Write("system-info", SystemInfoMd, ("scripts/sysinfo.sh", "#!/bin/sh\nuname -a\n"), ("assets/data.csv", "a,b\n"));
        var sandbox = new FakeSandboxEnvironment(cmd => cmd[0] == "sh" ? FakeSandboxEnvironment.Ok("/home/agent\n") : FakeSandboxEnvironment.Ok());

        var infos = await SkillInstaller.InstallSkillsAsync([dir], sandbox, user: "agent");

        var info = Assert.Single(infos);
        Assert.Equal("system-info", info.Name);
        Assert.Equal("/home/agent/skills/system-info/SKILL.md", info.Location);
        Assert.Equal(["/home/agent/skills/system-info/SKILL.md", "/home/agent/skills/system-info/assets/data.csv", "/home/agent/skills/system-info/scripts/sysinfo.sh"], sandbox.Files.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("#!/bin/sh\nuname -a\n", await sandbox.ReadFileAsync("/home/agent/skills/system-info/scripts/sysinfo.sh"));
        Assert.StartsWith("---\nname: system-info\n", await sandbox.ReadFileAsync(info.Location));

        var commands = sandbox.Calls.Select(c => (string.Join(' ', c.Cmd), c.User, c.Timeout)).ToArray();
        Assert.Equal(("sh -c pwd", "agent", TimeSpan.FromSeconds(60)), commands[0]);
        Assert.Contains(("chown agent /home/agent/skills/system-info/SKILL.md", "root", TimeSpan.FromSeconds(60)), commands);
        Assert.Contains(("chmod +x /home/agent/skills/system-info/scripts/sysinfo.sh", "agent", TimeSpan.FromSeconds(60)), commands);
        Assert.DoesNotContain(commands, c => c.Item1.StartsWith("chmod") && c.Item1.Contains("data.csv"));
        Assert.DoesNotContain(commands, c => c.Item1.StartsWith("chmod") && c.Item1.Contains("SKILL.md"));
    }

    [Fact]
    public async Task InstallSkills_AbsoluteDirSkipsPwdAndFailedCommandThrows()
    {
        var skill = new Skill("pdf", "PDF tools", "Use it.") { Scripts = new Dictionary<string, SkillFile> { ["run.sh"] = "echo\n" } };
        var sandbox = new FakeSandboxEnvironment(cmd => cmd[0] == "chmod" ? FakeSandboxEnvironment.Fail(1, "chmod: denied") : FakeSandboxEnvironment.Ok());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SkillInstaller.InstallSkillsAsync([skill], sandbox, dir: "/opt/skills"));

        Assert.Equal("Error executing command chmod +x /opt/skills/pdf/scripts/run.sh: chmod: denied", ex.Message);
        Assert.DoesNotContain(sandbox.Calls, c => c.Cmd[0] == "sh");
        Assert.True(sandbox.Files.ContainsKey("/opt/skills/pdf/SKILL.md"));
    }

    [Fact]
    public void SkillTool_DescriptionAndParametersMatchPython()
    {
        var tool = SkillTools.Skill([new Skill("pdf", "Work with PDF files", "i"), new Skill("xlsx", "Spreadsheets", "i")]);

        Assert.Equal("skill", tool.Name);
        Assert.True(tool.Parallel);
        Assert.Equal(["command"], tool.Parameters.Required);
        Assert.Equal(["string"], tool.Parameters.Properties["command"].Type);
        Assert.Equal(SkillTools.CommandDescription, tool.Parameters.Properties["command"].Description);
        Assert.StartsWith("\n    Invoke a skill to get specialized instructions for a task.\n\n    <skills_instructions>\n", tool.Description);
        Assert.Contains("\n    </skills_instructions>\n\n    <available_skills>\n<skill>\n<name>pdf</name>\n<description>Work with PDF files</description></skill>\n<skill>\n<name>xlsx</name>\n<description>Spreadsheets</description></skill>\n</available_skills>\n", tool.Description);
        Assert.EndsWith("</available_skills>\n", tool.Description);
    }

    [Fact]
    public async Task SkillTool_InstallsOnceAndReturnsInstructions()
    {
        using var temp = new TempSkills();
        var dir = temp.Write("system-info", SystemInfoMd, ("scripts/sysinfo.sh", "#!/bin/sh\n"));
        var sandbox = new FakeSandboxEnvironment(cmd => cmd[0] == "sh" ? FakeSandboxEnvironment.Ok("/root") : FakeSandboxEnvironment.Ok());
        using var scope = new SampleContextScope(sandbox: sandbox);
        var tool = SkillTools.Skill([dir], instance: "sub");

        var result = await tool.Execute(new JsonObject { ["command"] = "system-info" }, CancellationToken.None);

        var skill = SkillReader.ReadSkill(dir);
        Assert.Equal(
            "<command-message>The \"system-info\" skill is running</command-message>\n<command-name>system-info</command-name>\n\nBase Path: /root/skills/system-info/SKILL.md\n\n" + skill.Instructions,
            result.AsText());
        Assert.True(sandbox.Files.ContainsKey("/root/skills/system-info/scripts/sysinfo.sh"));
        var calls = sandbox.Calls.Count;

        var again = await tool.Execute(new JsonObject { ["command"] = "system-info" }, CancellationToken.None);
        Assert.Equal(result.AsText(), again.AsText());
        Assert.Equal(calls, sandbox.Calls.Count);

        var installed = scope.Context.Store.As<InstalledSkills>("sub");
        var info = Assert.Single(installed.Skills!);
        Assert.Equal("/root/skills/system-info/SKILL.md", info.Location);
        Assert.True(scope.Context.Store.Contains("InstalledSkills:sub:skills"));

        var ex = await Assert.ThrowsAsync<ToolError>(() => tool.Execute(new JsonObject { ["command"] = "nope" }, CancellationToken.None));
        Assert.Equal("Unknown skill: nope", ex.Message);
        await Assert.ThrowsAsync<ToolParsingError>(() => tool.Execute(new JsonObject(), CancellationToken.None));
    }

    [Fact]
    public async Task SkillTool_ConcurrentFirstCallsInstallOnce()
    {
        var sandbox = new FakeSandboxEnvironment(cmd => cmd[0] == "sh" ? FakeSandboxEnvironment.Ok("/w") : FakeSandboxEnvironment.Ok());
        using var scope = new SampleContextScope(sandbox: sandbox);
        var tool = SkillTools.Skill([new Skill("a", "A", "do a"), new Skill("b", "B", "do b")], instance: Guid.NewGuid().ToString("N"));

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            {
                using var inner = SampleContext.Begin(scope.Context);
                return (await tool.Execute(new JsonObject { ["command"] = i % 2 == 0 ? "a" : "b" }, CancellationToken.None)).AsText();
            })));

        Assert.Equal(1, sandbox.Calls.Count(c => c.Cmd[0] == "sh"));
        Assert.All(results.Where((_, i) => i % 2 == 0), r => Assert.EndsWith("Base Path: /w/skills/a/SKILL.md\n\ndo a", r));
        Assert.All(results.Where((_, i) => i % 2 == 1), r => Assert.EndsWith("Base Path: /w/skills/b/SKILL.md\n\ndo b", r));
    }

    [Fact]
    public void ExampleSkills_ReadWhenPresent()
    {
        if (!Directory.Exists(ExampleSkills))
        {
            return;
        }

        var skills = SkillReader.ReadSkills([
            Path.Combine(ExampleSkills, "system-info"),
            Path.Combine(ExampleSkills, "network-info"),
            Path.Combine(ExampleSkills, "disk-usage"),
        ]);

        Assert.Equal(["system-info", "network-info", "disk-usage"], skills.Select(s => s.Name).ToArray());
        Assert.All(skills, s => Assert.Single(s.Scripts));
        Assert.StartsWith("Retrieve detailed Linux system information", skills[0].Description);
        Assert.Contains("# System Information Skill", skills[0].Instructions);
    }
}
