namespace InspectAzureAI.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        return await InspectCli.RunAsync(args, cancellationToken: cancellation.Token).ConfigureAwait(false);
    }
}
