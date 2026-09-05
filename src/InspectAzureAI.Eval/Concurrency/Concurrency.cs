using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Concurrency;

/// <summary>One status-display entry: holders inside a limit and the limit itself.</summary>
public readonly record struct ConcurrencyStatus(int InUse, int Concurrency);

/// <summary>
/// Port of the ContextVars <c>_active_controller</c> / <c>_request_had_retry</c> / <c>_request_was_cache_hit</c>:
/// the adaptive-controller signal state of one logical generate. <see cref="Concurrency.ReportHttpRetry"/> flips
/// <see cref="HadRetry"/> (and cuts the controller on a rate limit); the model notifies a clean success only when
/// neither flag is set.
/// </summary>
public sealed class ConnectionRequest(AdaptiveConcurrencyController? controller)
{
    private bool _hadRetry;

    private bool _wasCacheHit;

    /// <summary>The controller gating this request, or null on the static path.</summary>
    public AdaptiveConcurrencyController? Controller { get; } = controller;

    /// <summary>True once any retry (rate-limit or transient) was reported during the request.</summary>
    public bool HadRetry
    {
        get => Volatile.Read(ref _hadRetry);
        set => Volatile.Write(ref _hadRetry, value);
    }

    /// <summary>True when the request was served from a cache without a provider call (neutral for the controller).</summary>
    public bool WasCacheHit
    {
        get => Volatile.Read(ref _wasCacheHit);
        set => Volatile.Write(ref _wasCacheHit, value);
    }
}

/// <summary>
/// Port of <c>util/_concurrency.py</c>: the process-global registry of named, keyed concurrency limits (the
/// <c>concurrency()</c> context manager), adaptive-controller creation and observers, the retry signal funnel of
/// <c>_util/retry.py</c> <c>report_http_retry</c>, and the status display. Entries are keyed by <c>key</c> (the
/// name when omitted) with a <c>#adaptive</c> / <c>#static</c> suffix so an adaptive and a static context for the
/// same key coexist. Python resets the registry per eval run; a .NET process can host concurrent runs, so the
/// reset is the explicit <see cref="Init"/>.
/// </summary>
public static class Concurrency
{
    private static readonly object Sync = new();

    private static readonly Dictionary<string, IConcurrencySemaphore> Registry = new(StringComparer.Ordinal);

    private static readonly List<Action<AdaptiveConcurrencyController>> ControllerCreatedObservers = [];

    private static readonly Dictionary<string, ISampleLimiter> TaskSampleSemaphores = new(StringComparer.Ordinal);

    private static readonly AsyncLocal<ConnectionRequest?> ActiveRequestLocal = new();

    private static int _httpRetries;

    /// <summary>Port of <c>http_retries_count()</c>: retries reported this process (never reset, as in Python).</summary>
    public static int HttpRetriesCount => Volatile.Read(ref _httpRetries);

    /// <summary>The adaptive controller of the generate in progress on this async flow, if any.</summary>
    public static AdaptiveConcurrencyController? ActiveController => ActiveRequestLocal.Value?.Controller;

    /// <summary>The signal state of the generate in progress on this async flow, if any.</summary>
    public static ConnectionRequest? ActiveRequest => ActiveRequestLocal.Value;

    /// <summary>
    /// Port of <c>get_or_create_semaphore</c>. <paramref name="concurrency"/> is the static limit (ignored when
    /// <paramref name="adaptive"/> is set, which creates an <see cref="AdaptiveConcurrencyController"/> starting at
    /// <c>adaptive.Start</c>); <paramref name="key"/> defaults to the name. The first creation's bounds win for later
    /// callers of the same key.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A static <paramref name="concurrency"/> below 1 (Python would create a semaphore nothing can enter).</exception>
    public static IConcurrencySemaphore GetOrCreateSemaphore(string name, int concurrency, string? key = null, bool visible = true, AdaptiveConcurrency? adaptive = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var baseKey = string.IsNullOrEmpty(key) ? name : key;
        var storageKey = adaptive is not null ? $"{baseKey}#adaptive" : $"{baseKey}#static";
        AdaptiveConcurrencyController? created = null;
        Action<AdaptiveConcurrencyController>[] observers = [];
        IConcurrencySemaphore semaphore;
        lock (Sync)
        {
            if (Registry.TryGetValue(storageKey, out var existing))
            {
                return existing;
            }

            if (adaptive is not null)
            {
                created = new AdaptiveConcurrencyController(name, adaptive, visible, baseKey);
                semaphore = created;
                observers = ControllerCreatedObservers.ToArray();
            }
            else
            {
                if (concurrency < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(concurrency), concurrency, $"Concurrency limit for '{name}' must be >= 1 (got {concurrency})");
                }

                semaphore = new ResizableSemaphore(name, concurrency, visible, baseKey);
            }

            Registry[storageKey] = semaphore;
        }

        if (created is not null)
        {
            foreach (var observer in observers)
            {
                observer(created);
            }
        }

        return semaphore;
    }

    /// <summary>
    /// Port of the <c>concurrency()</c> context manager: limits the callers inside a block, e.g.
    /// <c>await using var lease = await Concurrency.AcquireAsync("api-name", 10, cancellationToken: ct);</c>.
    /// <paramref name="key"/> is the registry identity when the name isn't (it may carry tokens or account ids);
    /// <paramref name="visible"/> controls the status display; <paramref name="adaptive"/> makes the limit an
    /// adaptive controller instead of a static one.
    /// </summary>
    public static async ValueTask<ConcurrencyLease> AcquireAsync(string name, int concurrency, string? key = null, bool visible = true, AdaptiveConcurrency? adaptive = null, CancellationToken cancellationToken = default) =>
        await GetOrCreateSemaphore(name, concurrency, key, visible, adaptive).AcquireAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Port of <c>concurrency_status_display</c>: visible entries as <c>(in use, limit)</c>. A <c>prefix/rest</c>
    /// name is shortened to its prefix when no other entry shares it (e.g. <c>openai</c> for <c>openai/gpt-4o</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, ConcurrencyStatus> StatusDisplay()
    {
        var semaphores = Semaphores();
        var names = semaphores.Select(s => s.Name).ToArray();
        var status = new Dictionary<string, ConcurrencyStatus>(StringComparer.Ordinal);
        foreach (var semaphore in semaphores)
        {
            if (!semaphore.Visible)
            {
                continue;
            }

            var prefix = semaphore.Name.Split('/')[0];
            var prefixCount = names.Count(n => n.StartsWith(prefix + "/", StringComparison.Ordinal));
            status[prefixCount == 1 ? prefix : semaphore.Name] = new ConcurrencyStatus(semaphore.InUse, semaphore.Concurrency);
        }

        return status;
    }

    /// <summary>Port of <c>concurrency_semaphores()</c>: the raw registry view (unshortened names, hidden entries included).</summary>
    public static IReadOnlyList<IConcurrencySemaphore> Semaphores()
    {
        lock (Sync)
        {
            return Registry.Values.ToArray();
        }
    }

    /// <summary>Port of <c>adaptive_controllers()</c>: every registered adaptive controller.</summary>
    public static IReadOnlyList<AdaptiveConcurrencyController> AdaptiveControllers()
    {
        lock (Sync)
        {
            return Registry.Values.OfType<AdaptiveConcurrencyController>().ToArray();
        }
    }

    /// <summary>Registers a callback fired when the registry creates a new adaptive controller (cleared by <see cref="Init"/>).</summary>
    public static void AddControllerCreatedObserver(Action<AdaptiveConcurrencyController> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (Sync)
        {
            ControllerCreatedObservers.Add(callback);
        }
    }

    /// <summary>Port of <c>task_sample_semaphore</c>: a task's sample limiter from an earlier attempt, if registered.</summary>
    public static ISampleLimiter? TaskSampleSemaphore(string taskId)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        lock (Sync)
        {
            return TaskSampleSemaphores.GetValueOrDefault(taskId);
        }
    }

    /// <summary>Port of <c>register_task_sample_semaphore</c>: keeps a task's sample limiter for reuse by a later retry attempt.</summary>
    public static void RegisterTaskSampleSemaphore(string taskId, ISampleLimiter semaphore)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        ArgumentNullException.ThrowIfNull(semaphore);
        lock (Sync)
        {
            TaskSampleSemaphores[taskId] = semaphore;
        }
    }

    /// <summary>
    /// Port of <c>init_concurrency()</c>: drops every registered limit and controller (with their observers), the
    /// controller-created observers and the task sample limiters. Not called by the runner — a process may host
    /// concurrent runs — so hosts and tests call it between runs.
    /// </summary>
    public static void Init()
    {
        lock (Sync)
        {
            Registry.Clear();
            ControllerCreatedObservers.Clear();
            TaskSampleSemaphores.Clear();
        }
    }

    /// <summary>
    /// Port of <c>adaptive_active</c>: the single predicate deciding the strategy. A null or enabled setting
    /// activates adaptive unless an explicit <paramref name="maxConnections"/> or batch mode silently wins.
    /// </summary>
    public static bool AdaptiveActive(AdaptiveConnections? adaptive, int? maxConnections, bool batch = false) =>
        adaptive?.Enabled != false && maxConnections is null && !batch;

    /// <summary>
    /// Port of <c>report_http_retry</c>: counts the retry, attributes it to <paramref name="model"/>'s throughput
    /// when known, marks the request in progress as retried (its eventual success is then neutral) and, for a
    /// <see cref="RetryKind.RateLimit"/>, cuts the active adaptive controller. <paramref name="retryAfter"/> is
    /// carried to the controller, which deliberately ignores it.
    /// </summary>
    public static void ReportHttpRetry(RetryKind kind = RetryKind.Transient, double? retryAfter = null, string? model = null)
    {
        Interlocked.Increment(ref _httpRetries);
        if (model is not null)
        {
            Throughput.RecordRetry(model, kind);
        }

        var request = ActiveRequestLocal.Value;
        if (request is null)
        {
            return;
        }

        request.HadRetry = true;
        if (kind == RetryKind.RateLimit)
        {
            request.Controller?.NotifyRetry(retryAfter);
        }
    }

    /// <summary>
    /// Makes <paramref name="controller"/> the active one for the current async flow until the returned scope is
    /// disposed (the port of setting the ContextVars for the duration of a generate). The request object records
    /// the retry / cache-hit signals raised meanwhile.
    /// </summary>
    public static ConnectionRequestScope BeginRequest(AdaptiveConcurrencyController? controller)
    {
        var previous = ActiveRequestLocal.Value;
        var request = new ConnectionRequest(controller);
        ActiveRequestLocal.Value = request;
        return new ConnectionRequestScope(request, previous);
    }

    /// <summary>Restores the previous active request on dispose.</summary>
    public sealed class ConnectionRequestScope : IDisposable
    {
        private readonly ConnectionRequest? _previous;

        internal ConnectionRequestScope(ConnectionRequest request, ConnectionRequest? previous)
        {
            Request = request;
            _previous = previous;
        }

        public ConnectionRequest Request { get; }

        public void Dispose() => ActiveRequestLocal.Value = _previous;
    }
}
