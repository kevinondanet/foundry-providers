// The tests share process-wide state (Approvers.DefaultPrompter, SandboxRegistry and ApproverRegistry
// registrations), so tests in this assembly run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
