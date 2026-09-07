// SampleContext is ambient (AsyncLocal) and the agent tests install one per test, so tests in this assembly run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
