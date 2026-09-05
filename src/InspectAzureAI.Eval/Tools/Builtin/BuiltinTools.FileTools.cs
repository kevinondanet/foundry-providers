using System.Globalization;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools.Builtin;

public static partial class BuiltinTools
{
    /// <summary>The <c>read_file</c> tool description (the Python <c>execute</c> docstring).</summary>
    public const string ReadFileDescription =
        "Read the contents of a file.\n\n"
        + "Returns the file contents with line numbers prepended. Use\noffset and limit for pagination of large files.";

    /// <summary>The <c>list_files</c> tool description.</summary>
    public const string ListFilesDescription =
        "List files and directories at the given path.\n\n"
        + "Returns one path per line. Use depth to limit the recursion\nlevel: 1 lists only immediate contents, None lists everything\nrecursively.";

    /// <summary>The <c>grep</c> tool description.</summary>
    public const string GrepDescription =
        "Search for a pattern in files.\n\n"
        + "Recursively searches for a pattern and returns results based\non the output_mode setting.";

    /// <summary>The <c>output_mode</c> values of <see cref="Grep"/>.</summary>
    public static readonly IReadOnlyList<string> GrepOutputModes = ["content", "files_with_matches", "count"];

    /// <summary>
    /// Port of <c>read_file()</c> (<c>tool/_tools/_read_file.py</c>): a read-only tool that returns a file's
    /// contents with line numbers prepended, paginated with <c>offset</c> (0-indexed first line) and
    /// <c>limit</c>. Runs <c>awk</c> in the sample sandbox with a pure argv (no shell interpolation of the
    /// path; a leading <c>-</c> is prefixed with <c>./</c> so awk does not parse it as an option).
    /// </summary>
    /// <param name="timeout">Timeout for the read operation.</param>
    /// <param name="user">User to execute as.</param>
    /// <param name="sandbox">Optional sandbox environment name.</param>
    public static ToolDef ReadFile(TimeSpan? timeout = null, string? user = null, string? sandbox = null)
    {
        var parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam>
            {
                ["file_path"] = ToolParam.Of("string", "Path to the file to read."),
                ["offset"] = new ToolParam { Type = ["integer"], Description = "Line number to start reading from (0-indexed).", Default = 0 },
                ["limit"] = new ToolParam
                {
                    Description = "Maximum number of lines to read. Reads to end\nof file if not specified.",
                    AnyOf = [ToolParam.Of("integer"), ToolParam.Of("null")],
                },
            },
            Required = ["file_path"],
        };
        return new ToolDef("read_file", ReadFileDescription, parameters, async (arguments, cancellationToken) =>
        {
            ToolInputValidator.Validate(arguments, parameters);
            var filePath = ToolArguments.String(arguments, "file_path");
            var offset = ToolArguments.Int(arguments, "offset", 0);
            var limit = ToolArguments.OptionalInt(arguments, "limit");

            var start = Math.Max(0, offset) + 1;
            string awkProgram;
            if (limit is { } lines)
            {
                var end = start + lines - 1;
                awkProgram = $"NR >= {Invariant(start)} && NR <= {Invariant(end)} {{ printf \"%d\\t%s\\n\", NR, $0 }} NR > {Invariant(end)} {{ exit }}";
            }
            else
            {
                awkProgram = $"NR >= {Invariant(start)} {{ printf \"%d\\t%s\\n\", NR, $0 }}";
            }

            var safePath = filePath.StartsWith('-') ? "./" + filePath : filePath;
            var result = await SampleContext.Require().Sandbox(sandbox)
                .ExecAsync(["awk", awkProgram, safePath], timeout: timeout, user: user, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                var stderr = result.Stderr.Trim().ToLowerInvariant();
                if (stderr.Contains("no such file", StringComparison.Ordinal) || stderr.Contains("not found", StringComparison.Ordinal))
                {
                    throw new ToolError($"File not found: {filePath}");
                }

                if (stderr.Contains("permission denied", StringComparison.Ordinal))
                {
                    throw new ToolError($"Permission denied: {filePath}");
                }

                if (stderr.Contains("is a directory", StringComparison.Ordinal))
                {
                    throw new ToolError($"Path is a directory, not a file: {filePath}");
                }

                throw new ToolError(result.Stderr.Length > 0 ? result.Stderr.Trim() : $"Error reading: {filePath}");
            }

            return result.Stdout.TrimEnd('\n');
        })
        { Parallel = true };
    }

    /// <summary>
    /// Port of <c>list_files()</c> (<c>tool/_tools/_list_files.py</c>): a read-only directory listing tool.
    /// Runs <c>find -- path -mindepth 1 [-maxdepth depth] -print</c> in the sample sandbox and returns the
    /// sorted paths, one per line, or "No files found in: path".
    /// </summary>
    /// <param name="timeout">Timeout for the listing.</param>
    /// <param name="user">User to execute as.</param>
    /// <param name="sandbox">Optional sandbox environment name.</param>
    public static ToolDef ListFiles(TimeSpan? timeout = null, string? user = null, string? sandbox = null)
    {
        var parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam>
            {
                ["path"] = new ToolParam { Type = ["string"], Description = "Directory path to list (defaults to working directory).", Default = "." },
                ["depth"] = new ToolParam
                {
                    Description = "Maximum depth for recursive listing. 1 lists only the\nimmediate directory. None lists all files recursively.",
                    AnyOf = [ToolParam.Of("integer"), ToolParam.Of("null")],
                },
            },
            Required = [],
        };
        return new ToolDef("list_files", ListFilesDescription, parameters, async (arguments, cancellationToken) =>
        {
            ToolInputValidator.Validate(arguments, parameters);
            var path = ToolArguments.String(arguments, "path", ".");
            var depth = ToolArguments.OptionalInt(arguments, "depth");

            var cmd = new List<string> { "find", "--", path, "-mindepth", "1" };
            if (depth is { } maxDepth)
            {
                cmd.AddRange(["-maxdepth", Invariant(maxDepth)]);
            }

            cmd.Add("-print");

            var result = await SampleContext.Require().Sandbox(sandbox)
                .ExecAsync(cmd, timeout: timeout, user: user, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                throw new ToolError(result.Stderr.Length > 0 ? result.Stderr.Trim() : $"Error listing: {path}");
            }

            var output = result.Stdout.Trim();
            if (output.Length == 0)
            {
                return $"No files found in: {path}";
            }

            var lines = PythonSplitLines(output).Order(StringComparer.Ordinal);
            return string.Join("\n", lines);
        })
        { Parallel = true };
    }

    /// <summary>
    /// Port of <c>grep()</c> (<c>tool/_tools/_grep.py</c>): a read-only text search tool running
    /// <c>grep -rn [-F] [-E] [--include glob] [-l | -c] -- pattern path</c> in the sample sandbox. Patterns are
    /// basic regular expressions unless <c>extended_regexp</c> or <c>fixed_strings</c> is set (grep itself
    /// rejects both together). Exit status 1 (no matches) answers "No matches found."; in <c>count</c> mode files
    /// with zero matches are dropped.
    /// </summary>
    /// <param name="timeout">Timeout for the search.</param>
    /// <param name="user">User to execute as.</param>
    /// <param name="sandbox">Optional sandbox environment name.</param>
    public static ToolDef Grep(TimeSpan? timeout = null, string? user = null, string? sandbox = null)
    {
        var parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam>
            {
                ["pattern"] = ToolParam.Of("string", "Regular expression pattern to search for\n(or literal string if fixed_strings is True). Uses\ngrep's basic regex (BRE) by default."),
                ["path"] = new ToolParam { Type = ["string"], Description = "File or directory to search (defaults to working\ndirectory).", Default = "." },
                ["glob"] = new ToolParam
                {
                    Description = "Glob pattern to filter which files to search\n(e.g. \"*.py\").",
                    AnyOf = [ToolParam.Of("string"), ToolParam.Of("null")],
                },
                ["fixed_strings"] = new ToolParam { Type = ["boolean"], Description = "If True, treat pattern as a literal string\nrather than a regular expression.", Default = false },
                ["extended_regexp"] = new ToolParam { Type = ["boolean"], Description = "If True, use extended regex (ERE).\nMutually exclusive with fixed_strings.", Default = false },
                ["output_mode"] = new ToolParam
                {
                    Type = ["string"],
                    Description = "Output format. \"content\" returns matching\nlines with file paths and line numbers. \"files_with_matches\"\nreturns only file paths containing matches. \"count\"\nreturns match counts per file.",
                    Default = "content",
                    Enum = GrepOutputModes.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray(),
                },
            },
            Required = ["pattern"],
        };
        return new ToolDef("grep", GrepDescription, parameters, async (arguments, cancellationToken) =>
        {
            ToolInputValidator.Validate(arguments, parameters);
            var pattern = ToolArguments.String(arguments, "pattern");
            var path = ToolArguments.String(arguments, "path", ".");
            var glob = ToolArguments.OptionalString(arguments, "glob");
            var fixedStrings = ToolArguments.Bool(arguments, "fixed_strings", false);
            var extendedRegexp = ToolArguments.Bool(arguments, "extended_regexp", false);
            var outputMode = ToolArguments.String(arguments, "output_mode", "content");

            var cmd = new List<string> { "grep", "-rn" };
            if (fixedStrings)
            {
                cmd.Add("-F");
            }

            if (extendedRegexp)
            {
                cmd.Add("-E");
            }

            if (!string.IsNullOrEmpty(glob))
            {
                cmd.AddRange(["--include", glob]);
            }

            if (outputMode == "files_with_matches")
            {
                cmd.Add("-l");
            }
            else if (outputMode == "count")
            {
                cmd.Add("-c");
            }

            cmd.AddRange(["--", pattern, path]);

            var result = await SampleContext.Require().Sandbox(sandbox)
                .ExecAsync(cmd, timeout: timeout, user: user, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // exit code 1 means no matches (not an error)
            if (result.ReturnCode == 1)
            {
                return "No matches found.";
            }

            if (result.ReturnCode != 0)
            {
                throw new ToolError(result.Stderr.Length > 0 ? result.Stderr.Trim() : "grep failed");
            }

            var output = result.Stdout.Trim();
            if (outputMode == "count")
            {
                var lines = PythonSplitLines(output).Where(line => !line.EndsWith(":0", StringComparison.Ordinal)).ToList();
                return lines.Count > 0 ? string.Join("\n", lines) : "No matches found.";
            }

            return output;
        })
        { Parallel = true };
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Python <c>str.splitlines()</c>: splits on every line boundary Python recognises, without keeping the ends.</summary>
    internal static List<string> PythonSplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var boundary = c switch
            {
                '\n' or '\r' or '\v' or '\f' or '\u001c' or '\u001d' or '\u001e' or '\u0085' or '\u2028' or '\u2029' => true,
                _ => false,
            };
            if (!boundary)
            {
                continue;
            }

            lines.Add(text[start..i]);
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }
}
