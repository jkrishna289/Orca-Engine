using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Configuration;
using Wholphin.Engine.Diagnostics;

namespace Wholphin.Engine.Http;

/// <summary>
/// Default <see cref="IProviderGate"/>. One singleton shared by every metadata provider, keyed by
/// provider name.
/// </summary>
/// <remarks>
/// <para>
/// A wrapper service rather than a <c>DelegatingHandler</c>, for three concrete reasons: a handler
/// can only refuse a call by throwing (so every provider would need a catch anyway, on top of the
/// handler and a named client each); a handler cannot see OMDb's real failure mode, which is HTTP 200
/// carrying <c>{"Response":"False"}</c>; and a concurrency semaphore held in a handler stays held
/// across JSON deserialization.
/// </para>
/// <para>
/// Health is in-memory on purpose. A tripped breaker SHOULD clear on restart — an operator who
/// restarts the server after fixing a key expects the provider to be tried again immediately.
/// </para>
/// </remarks>
public class ProviderGate : IProviderGate
{
    /// <summary>Smoothing factor for the latency mean — recent calls dominate without storing a window.</summary>
    private const double LatencyAlpha = 0.2;

    /// <summary>The longest a breaker stays open regardless of how many times it has tripped.</summary>
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<ProviderGate> _logger;
    private readonly Func<PluginConfiguration?> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderGate"/> class.
    /// </summary>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">Configuration accessor; defaults to the live plugin configuration. Tests inject their own.</param>
    public ProviderGate(IEngineMetrics metrics, ILogger<ProviderGate> logger, Func<PluginConfiguration?>? config = null)
    {
        _metrics = metrics;
        _logger = logger;
        _config = config ?? (() => Plugin.Instance?.Configuration);
    }

    /// <inheritdoc />
    public async Task<T?> ExecuteAsync<T>(string provider, Func<CancellationToken, Task<T?>> operation, CancellationToken cancellationToken)
        where T : class
    {
        var config = _config();
        var state = _states.GetOrAdd(provider, _ => new State(Math.Clamp(config?.MetadataProviderMaxConcurrency ?? 2, 1, 16)));

        if (!TryPass(state))
        {
            Interlocked.Increment(ref state.ShortCircuited);
            _metrics.Increment($"provider.{provider}.short");
            return null;
        }

        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await ThrottleAsync(state, config, cancellationToken).ConfigureAwait(false);

            var timeoutSeconds = Math.Clamp(config?.MetadataProviderTimeoutSeconds ?? 10, 1, 120);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var result = await operation(timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();

            if (result is null)
            {
                // "This provider has nothing for this title" is a valid answer, not an outage — a
                // breaker that trips on misses would disable OMDb over a handful of obscure films.
                Interlocked.Increment(ref state.Empty);
                _metrics.Increment($"provider.{provider}.empty");
                RecordSuccess(state, stopwatch.ElapsedMilliseconds);
                return null;
            }

            RecordSuccess(state, stopwatch.ElapsedMilliseconds);
            _metrics.Record($"provider.{provider}", stopwatch.ElapsedMilliseconds, ok: true);
            Interlocked.Increment(ref state.Success);
            return result;
        }
        catch (ProviderRateLimitedException)
        {
            stopwatch.Stop();
            Interlocked.Increment(ref state.RateLimited);
            _metrics.Increment($"provider.{provider}.ratelimited");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller went away. Not the provider's fault, and the caller needs to see it.
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            Interlocked.Increment(ref state.Timeout);
            _metrics.Increment($"provider.{provider}.timeout");
            RecordFailure(state, provider, config, nameof(TimeoutException), stopwatch.ElapsedMilliseconds);
            return null;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Interlocked.Increment(ref state.Failure);
            _metrics.Increment($"provider.{provider}.error");

            // Type name ONLY. HttpRequestException.Message can carry the request URL, and provider
            // URLs carry the API key in their query string.
            _metrics.Record($"provider.{provider}", stopwatch.ElapsedMilliseconds, ok: false, data: ex.GetType().Name);
            RecordFailure(state, provider, config, ex.GetType().Name, stopwatch.ElapsedMilliseconds);
            return null;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ProviderHealth> Snapshot(IReadOnlyCollection<string> configured)
    {
        var now = DateTime.UtcNow;
        return _states
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
            {
                var s = pair.Value;
                lock (s.Sync)
                {
                    return new ProviderHealth
                    {
                        Name = pair.Key,
                        Configured = configured.Contains(pair.Key, StringComparer.OrdinalIgnoreCase),
                        Success = Interlocked.Read(ref s.Success),
                        Empty = Interlocked.Read(ref s.Empty),
                        Failure = Interlocked.Read(ref s.Failure),
                        Timeout = Interlocked.Read(ref s.Timeout),
                        RateLimited = Interlocked.Read(ref s.RateLimited),
                        ShortCircuited = Interlocked.Read(ref s.ShortCircuited),
                        ConsecutiveFailures = s.ConsecutiveFailures,
                        AvgLatencyMs = Math.Round(s.AvgLatencyMs, 1),
                        LastSuccessUtc = s.LastSuccessUtc,
                        LastFailureUtc = s.LastFailureUtc,
                        LastFailureKind = s.LastFailureKind,
                        BreakerOpen = s.OpenUntilUtc is { } until && now < until,
                        BreakerOpenUntilUtc = s.OpenUntilUtc,
                    };
                }
            })
            .ToList();
    }

    /// <summary>
    /// The breaker decision: closed passes, open refuses, and an elapsed cooldown admits exactly one
    /// probe so a recovered provider is noticed without a thundering herd against a still-broken one.
    /// </summary>
    private static bool TryPass(State state)
    {
        lock (state.Sync)
        {
            if (state.OpenUntilUtc is not { } until)
            {
                return true;
            }

            if (DateTime.UtcNow < until)
            {
                return false;
            }

            // Cooldown elapsed: half-open. First caller through claims the probe.
            if (state.Probing)
            {
                return false;
            }

            state.Probing = true;
            return true;
        }
    }

    private static async Task ThrottleAsync(State state, PluginConfiguration? config, CancellationToken ct)
    {
        var minIntervalMs = Math.Clamp(config?.MetadataProviderMinIntervalMs ?? 0, 0, 60_000);
        if (minIntervalMs == 0)
        {
            return;
        }

        TimeSpan wait;
        lock (state.Sync)
        {
            var earliest = state.LastStartUtc.AddMilliseconds(minIntervalMs);
            wait = earliest - DateTime.UtcNow;
            state.LastStartUtc = wait > TimeSpan.Zero ? earliest : DateTime.UtcNow;
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    private static void RecordSuccess(State state, long elapsedMs)
    {
        lock (state.Sync)
        {
            state.ConsecutiveFailures = 0;
            state.Trips = 0;
            state.OpenUntilUtc = null;
            state.Probing = false;
            state.LastSuccessUtc = DateTime.UtcNow;
            state.AvgLatencyMs = state.AvgLatencyMs <= 0
                ? elapsedMs
                : (state.AvgLatencyMs * (1 - LatencyAlpha)) + (elapsedMs * LatencyAlpha);
        }
    }

    private void RecordFailure(State state, string provider, PluginConfiguration? config, string kind, long elapsedMs)
    {
        var threshold = Math.Clamp(config?.MetadataProviderBreakerThreshold ?? 5, 1, 100);
        var cooldownSeconds = Math.Clamp(config?.MetadataProviderBreakerCooldownSeconds ?? 60, 1, 3600);
        bool tripped;
        TimeSpan cooldown;

        lock (state.Sync)
        {
            state.Probing = false;
            state.ConsecutiveFailures++;
            state.LastFailureUtc = DateTime.UtcNow;
            state.LastFailureKind = kind;
            state.AvgLatencyMs = state.AvgLatencyMs <= 0
                ? elapsedMs
                : (state.AvgLatencyMs * (1 - LatencyAlpha)) + (elapsedMs * LatencyAlpha);

            tripped = state.ConsecutiveFailures >= threshold;
            if (!tripped)
            {
                return;
            }

            state.Trips++;
            var backoffTicks = TimeSpan.FromSeconds(cooldownSeconds).Ticks * Math.Pow(2, Math.Min(state.Trips - 1, 10));
            cooldown = backoffTicks >= MaxCooldown.Ticks ? MaxCooldown : TimeSpan.FromTicks((long)backoffTicks);
            state.OpenUntilUtc = DateTime.UtcNow + cooldown;
        }

        _metrics.Increment($"provider.{provider}.breaker_open");
        _logger.LogWarning(
            "Orca Engine: metadata provider {Provider} circuit opened for {Seconds}s after {Count} consecutive failures ({Kind}).",
            provider,
            (int)cooldown.TotalSeconds,
            state.ConsecutiveFailures,
            kind);
    }

    /// <summary>Mutable per-provider counters and breaker state.</summary>
    private sealed class State
    {
        public State(int maxConcurrency) => Gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        /// <summary>Guards the composite breaker/latency fields, which must move together.</summary>
        public object Sync { get; } = new();

        public SemaphoreSlim Gate { get; }

        public long Success;
        public long Empty;
        public long Failure;
        public long Timeout;
        public long RateLimited;
        public long ShortCircuited;

        public int ConsecutiveFailures;
        public int Trips;
        public bool Probing;
        public double AvgLatencyMs;
        public DateTime LastStartUtc;
        public DateTime? LastSuccessUtc;
        public DateTime? LastFailureUtc;
        public string? LastFailureKind;
        public DateTime? OpenUntilUtc;
    }
}
