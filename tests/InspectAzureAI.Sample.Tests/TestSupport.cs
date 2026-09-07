// The demos write to Console.Out and read process-wide state (the cache directory variable, the model cost
// registry), so tests in this assembly run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
