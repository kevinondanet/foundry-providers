// The matrix tests share process-wide state (the model price registry) and the fake catalog's log directories,
// so tests in this assembly run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
