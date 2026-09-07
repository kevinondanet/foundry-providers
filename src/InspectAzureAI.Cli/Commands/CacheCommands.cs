using System.CommandLine;
using InspectAzureAI.Eval.Model.Cache;

namespace InspectAzureAI.Cli.Commands;

/// <summary>Port of <c>_cli/cache.py</c>: <c>cache list</c>, <c>cache clear</c>, <c>cache prune</c> and <c>cache path</c> over <see cref="CacheOps"/>, with its tables rendered as plain text.</summary>
internal static class CacheCommands
{
    public static Command Build(CliIo io)
    {
        var command = new Command("cache", "Manage the inspect model output cache.\n\nLearn more about model output caching at https://inspect.aisi.org.uk/caching.html.");
        command.Subcommands.Add(BuildList(io));
        command.Subcommands.Add(BuildClear(io));
        command.Subcommands.Add(BuildPrune(io));
        command.Subcommands.Add(BuildPath(io));
        return command;
    }

    private static Command BuildList(CliIo io)
    {
        var pruneable = Opt.Flag("--pruneable", "Only list cache entries that can be pruned due to expiry (see inspect cache prune --help).");
        var command = new Command("list", "Lists all current model caches with their sizes.");
        command.Options.Add(pruneable);
        command.SetAction(result =>
        {
            if (result.GetValue(pruneable))
            {
                var expired = CacheOps.CacheListExpired();
                if (expired.Count > 0)
                {
                    PrintTable(io, "The following models can be pruned due to cache expiry", CacheOps.CacheSize(files: expired));
                }
                else
                {
                    io.Out.WriteLine("No expired cache entries.");
                }
            }
            else
            {
                PrintTable(io, "Cache Sizes", CacheOps.CacheSize());
            }

            return 0;
        });
        return command;
    }

    private static Command BuildClear(CliIo io)
    {
        var logLevel = Opt.Choice("--log-level", $"Set the log level (defaults to '{CommonOptions.DefaultLogLevel}')", CommonOptions.LogLevels, "INSPECT_LOG_LEVEL", caseInsensitive: true);
        var all = Opt.Flag("--all", "Clear all cache files in the cache directory.");
        var model = Opt.Multi("--model", "Clear the cache for a specific model (e.g. --model=openai/gpt-4). Can be passed multiple times.");
        var command = new Command("clear", "Clear all cache files. Requires either --all or --model flags.");
        command.Options.Add(logLevel);
        command.Options.Add(all);
        command.Options.Add(model);
        command.SetAction(result =>
        {
            var models = result.GetValue(model) ?? [];
            if (models.Length > 0)
            {
                PrintTable(io, "Clearing the following caches", CacheOps.CacheSize(subdirs: models));
                foreach (var single in models)
                {
                    CacheOps.CacheClear(single);
                }
            }
            else if (result.GetValue(all))
            {
                PrintTable(io, "Clearing the following caches", CacheOps.CacheSize());
                CacheOps.CacheClear();
            }
            else
            {
                throw new UsageError("Need to specify either --all or --model.");
            }

            return 0;
        });
        return command;
    }

    private static Command BuildPrune(CliIo io)
    {
        var logLevel = Opt.Choice("--log-level", $"Set the log level (defaults to '{CommonOptions.DefaultLogLevel}')", CommonOptions.LogLevels, "INSPECT_LOG_LEVEL", caseInsensitive: true);
        var model = Opt.Multi("--model", "Only prune a specific model (e.g. --model=openai/gpt-4). Can be passed multiple times.");
        var command = new Command("prune", "Prune all expired cache entries.\n\nOver time the cache directory can grow, but many cache entries will be expired. This command will remove all expired cache entries for ease of maintenance.");
        command.Options.Add(logLevel);
        command.Options.Add(model);
        command.SetAction(result =>
        {
            var expired = CacheOps.CacheListExpired(result.GetValue(model) ?? []);
            if (expired.Count > 0)
            {
                PrintTable(io, "Pruning the following caches", CacheOps.CacheSize(files: expired));
                CacheOps.CachePrune(expired);
            }
            else
            {
                io.Out.WriteLine("No expired cache entries to prune.");
            }

            return 0;
        });
        return command;
    }

    private static Command BuildPath(CliIo io)
    {
        var command = new Command("path", "Prints the location of the cache directory.");
        command.SetAction(_ =>
        {
            io.Out.WriteLine(CacheOps.CachePath());
            return 0;
        });
        return command;
    }

    /// <summary>Port of <c>_print_table</c>: a title, a Model / Size header and one right-aligned readable size per model.</summary>
    internal static void PrintTable(CliIo io, string title, IReadOnlyList<ModelCacheSize> sizes)
    {
        var rows = sizes.Select(size => (size.Model, Size: CacheOps.ReadableSize(size.Bytes))).ToList();
        var modelWidth = Math.Max("Model".Length, rows.Count == 0 ? 0 : rows.Max(row => row.Model.Length));
        var sizeWidth = Math.Max("Size".Length, rows.Count == 0 ? 0 : rows.Max(row => row.Size.Length));
        io.Out.WriteLine(title);
        io.Out.WriteLine($"{"Model".PadRight(modelWidth)}  {"Size".PadLeft(sizeWidth)}");
        foreach (var (model, size) in rows)
        {
            io.Out.WriteLine($"{model.PadRight(modelWidth)}  {size.PadLeft(sizeWidth)}");
        }
    }
}
