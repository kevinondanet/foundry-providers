// SampleContext and the model-event sink are ambient (AsyncLocal), and the Claude Code tests bind
// bridge ports and touch a shared binary cache, so tests in this assembly run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
