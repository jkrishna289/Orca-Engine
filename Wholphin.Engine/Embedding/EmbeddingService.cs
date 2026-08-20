using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Configuration;
using Wholphin.Engine.Diagnostics;

namespace Wholphin.Engine.Embedding;

/// <summary>
/// Default <see cref="IEmbeddingService"/>. Resolves the provider named by
/// <c>PluginConfiguration.EmbeddingProvider</c> and embeds a corpus through it in bounded batches
/// with bounded retries.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no fallback provider.</b> Every provider here is a trained model reached over the
/// network — a local Ollama server or a hosted API — and none can stand in for another: their
/// vectors have different dimensions and different geometry, so mixing two models' output in one
/// index makes cosine similarity meaningless. When the configured provider cannot answer, this
/// reports failure and raises a critical alert. It never substitutes something weaker, because
/// something weaker would be indistinguishable from success at every layer above.
/// </para>
/// <para>
/// The safety net lives one level up: <c>ContentVectorIndex</c> keeps the last index that built
/// cleanly, so a failing provider costs freshness rather than the whole similarity layer.
/// </para>
/// </remarks>
public class EmbeddingService : IEmbeddingService
{
    /// <summary>Alert key for "the embedding layer is not producing vectors".</summary>
    public const string FallbackAlertKey = "embedding.fallback";

    /// <summary>Provider used when configuration names none.</summary>
    public const string DefaultProviderName = OllamaEmbeddingProvider.ProviderName;

    /// <summary>Batch size used when configuration does not say otherwise.</summary>
    public const int DefaultBatchSize = 96;

    /// <summary>Smallest configurable batch. Below this the per-call overhead dominates.</summary>
    public const int MinBatchSize = 8;

    /// <summary>
    /// Largest configurable batch. Deliberately far below any catalog size: the bug this batching
    /// exists to fix was one request carrying the whole catalog, and a ceiling is what stops a
    /// mistyped configuration value from recreating it.
    /// </summary>
    public const int MaxBatchSize = 512;

    /// <summary>Attempts per batch, including the first. Bounded so a dead provider cannot retry-storm.</summary>
    public const int MaxAttemptsPerBatch = 3;

    /// <summary>Backoff before the next attempt, multiplied by the attempt number.</summary>
    private static readonly TimeSpan DefaultRetryBackoff = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyDictionary<string, IEmbeddingProvider> _providers;
    private readonly IEmbeddingProvider _default;
    private readonly IEngineAlerts _alerts;
    private readonly IEngineMetrics _metrics;
    private readonly Func<PluginConfiguration?> _config;
    private readonly TimeSpan _retryBackoff;
    private readonly ILogger<EmbeddingService> _logger;

    /// <summary>Initializes a new instance of the <see cref="EmbeddingService"/> class.</summary>
    /// <param name="providers">All registered embedding providers.</param>
    /// <param name="alerts">Sticky health alerts (surfaces an unusable provider in the dashboard).</param>
    /// <param name="metrics">Operational counters.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">Configuration accessor; defaults to the live plugin configuration.</param>
    /// <param name="retryBackoff">Delay before a batch retry, scaled by attempt. Defaults to 2s; tests pass zero.</param>
    public EmbeddingService(
        IEnumerable<IEmbeddingProvider> providers,
        IEngineAlerts alerts,
        IEngineMetrics metrics,
        ILogger<EmbeddingService> logger,
        Func<PluginConfiguration?>? config = null,
        TimeSpan? retryBackoff = null)
    {
        var list = providers.ToList();
        _providers = list.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
        _default = _providers.TryGetValue(DefaultProviderName, out var ollama) ? ollama : list[0];
        _alerts = alerts;
        _metrics = metrics;
        _logger = logger;
        _config = config ?? (() => Plugin.Instance?.Configuration);
        _retryBackoff = retryBackoff ?? DefaultRetryBackoff;
    }

    /// <inheritdoc />
    public string ActiveProviderName => Resolve().Name;

    /// <inheritdoc />
    public string ActiveModelId => Resolve().ModelId;

    /// <summary>
    /// Resolves the effective batch size for a provider: the configured value, clamped to this
    /// service's bounds and to whatever the provider itself can accept.
    /// </summary>
    /// <param name="provider">The provider about to be called.</param>
    /// <param name="configured">The configured batch size, or null to use the default.</param>
    /// <returns>Documents per call.</returns>
    public static int ResolveBatchSize(IEmbeddingProvider provider, int? configured)
    {
        var wanted = configured is > 0 ? configured.Value : DefaultBatchSize;
        var ceiling = Math.Min(MaxBatchSize, Math.Max(MinBatchSize, provider.MaxBatchSize));
        return Math.Clamp(wanted, MinBatchSize, ceiling);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentVector>?> EmbedAsync(IReadOnlyList<string> documents, CancellationToken ct = default)
    {
        if (documents.Count == 0)
        {
            return Array.Empty<ContentVector>();
        }

        var provider = Resolve();
        if (!provider.IsConfigured)
        {
            return null;
        }

        var result = await provider.EmbedAsync(documents, ct).ConfigureAwait(false);
        if (result is not null && result.Count == documents.Count)
        {
            _alerts.Clear(FallbackAlertKey);
            return result;
        }

        RaiseUnavailableAlert(provider.Name, $"'{provider.Name}' was called and returned no usable vectors");
        return null;
    }

    /// <inheritdoc />
    public async Task<EmbeddingRun> EmbedCorpusAsync(
        IReadOnlyList<string> documents,
        IProgress<EmbeddingProgress>? progress = null,
        CancellationToken ct = default)
    {
        var provider = Resolve();
        if (documents.Count == 0)
        {
            return EmbeddingRun.Empty(provider.Name);
        }

        var batchSize = ResolveBatchSize(provider, _config()?.EmbeddingBatchSize);
        var batchesTotal = (int)Math.Ceiling(documents.Count / (double)batchSize);

        if (!provider.IsConfigured)
        {
            return EmbeddingRun.Failed(
                provider.Name,
                documents.Count,
                batchSize,
                0,
                0,
                $"'{provider.Name}' is not configured");
        }

        _logger.LogInformation(
            "Orca Engine: embedding {Documents} documents via '{Provider}' in {Batches} batch(es) of {BatchSize}.",
            documents.Count,
            provider.Name,
            batchesTotal,
            batchSize);

        var vectors = new ContentVector[documents.Count];
        var completed = 0;

        for (var start = 0; start < documents.Count; start += batchSize)
        {
            // Cancellation is not a provider failure; it propagates so the caller can discard a
            // half-built index rather than publish it or blame the provider for it.
            ct.ThrowIfCancellationRequested();

            var count = Math.Min(batchSize, documents.Count - start);
            var slice = Slice(documents, start, count);

            var batch = await EmbedBatchWithRetryAsync(provider, slice, ct).ConfigureAwait(false);
            if (batch is null)
            {
                _metrics.Increment("embedding.index.batch.error");
                return Fail(
                    provider,
                    documents.Count,
                    batchSize,
                    completed,
                    $"batch {completed + 1} of {batchesTotal} ({count} documents) failed after {MaxAttemptsPerBatch} attempts");
            }

            // Positional pairing is the whole ballgame: a provider that returns a different count
            // has broken the contract in a way that would shift every later document onto the wrong
            // vector, so it is rejected outright rather than trusted partially.
            if (batch.Count != count)
            {
                _metrics.Increment("embedding.index.batch.malformed");
                return Fail(
                    provider,
                    documents.Count,
                    batchSize,
                    completed,
                    $"provider '{provider.Name}' returned {batch.Count} vectors for a batch of {count}");
            }

            for (var i = 0; i < count; i++)
            {
                vectors[start + i] = batch[i];
            }

            completed++;
            _metrics.Increment("embedding.index.batch.ok");
            progress?.Report(new EmbeddingProgress(
                provider.Name,
                documents.Count,
                batchSize,
                completed,
                batchesTotal,
                Math.Min(start + count, documents.Count)));
        }

        _alerts.Clear(FallbackAlertKey);
        return new EmbeddingRun(vectors, provider.Name, documents.Count, batchSize, completed, 0, null);
    }

    private EmbeddingRun Fail(IEmbeddingProvider provider, int documentCount, int batchSize, int completed, string failure)
    {
        _metrics.Increment("embedding.index.failed");
        _logger.LogWarning(
            "Orca Engine: embedding via '{Provider}' failed — {Failure}. The previous index is kept.",
            provider.Name,
            failure);
        RaiseUnavailableAlert(provider.Name, failure);
        return EmbeddingRun.Failed(provider.Name, documentCount, batchSize, completed, 1, failure);
    }

    /// <summary>
    /// Calls one batch, retrying a null result with linear backoff up to
    /// <see cref="MaxAttemptsPerBatch"/> times.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retries cover only the "provider was asked and produced nothing" case. Two things are
    /// deliberately NOT retried: cancellation, which propagates immediately, and an unconfigured
    /// provider, which is filtered out before any call is made — retrying a missing base URL or API
    /// key would just be a slower way to fail.
    /// </para>
    /// <para>
    /// Rate limiting (HTTP 429) is retried one level down, inside the providers that can see the
    /// status code and its <c>Retry-After</c> header. Retrying it again here would multiply the two
    /// budgets together, which is how a retry storm starts.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ContentVector>?> EmbedBatchWithRetryAsync(
        IEmbeddingProvider provider,
        IReadOnlyList<string> slice,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttemptsPerBatch; attempt++)
        {
            var result = await provider.EmbedAsync(slice, ct).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }

            if (attempt == MaxAttemptsPerBatch)
            {
                break;
            }

            _metrics.Increment("embedding.index.retry");
            _logger.LogWarning(
                "Orca Engine: '{Provider}' returned no vectors for a batch of {Count}; retry {Attempt} of {Max}.",
                provider.Name,
                slice.Count,
                attempt,
                MaxAttemptsPerBatch - 1);

            if (_retryBackoff > TimeSpan.Zero)
            {
                await Task.Delay(_retryBackoff * attempt, ct).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static IReadOnlyList<string> Slice(IReadOnlyList<string> documents, int start, int count)
    {
        if (start == 0 && count == documents.Count)
        {
            return documents;
        }

        var slice = new string[count];
        for (var i = 0; i < count; i++)
        {
            slice[i] = documents[start + i];
        }

        return slice;
    }

    private void RaiseUnavailableAlert(string providerName, string detail) => _alerts.Raise(
        FallbackAlertKey,
        "critical",
        $"Embedding provider '{providerName}' is not producing vectors — content similarity is running on a stale index.",
        $"{detail}. There is no fallback provider: a second model's vectors have different dimensions and "
        + "could not be compared with the ones already indexed, so the engine keeps the last index that "
        + "built cleanly rather than mixing them. Recommendations still work but stop reflecting newly "
        + $"added titles until this is fixed. If '{providerName}' is an Ollama server, check that it is "
        + "running, reachable at the configured URL, and has the model pulled. Lower the embedding batch "
        + "size if requests are timing out.");

    // The configured provider when it exists; else the default.
    private IEmbeddingProvider Resolve()
    {
        var configured = _config()?.EmbeddingProvider?.Trim();
        IEmbeddingProvider provider;

        if (string.IsNullOrWhiteSpace(configured)
            || string.Equals(configured, _default.Name, StringComparison.OrdinalIgnoreCase))
        {
            provider = _default;
        }
        else if (!_providers.TryGetValue(configured, out var named))
        {
            // A name nobody implements. More actionable than the readiness message below, so it
            // returns here rather than falling through and being overwritten by it.
            _alerts.Raise(
                FallbackAlertKey,
                "critical",
                $"Unknown embedding provider '{configured}' — no vectors can be produced.",
                $"Settings name '{configured}', which is not a registered provider. Valid names: "
                + string.Join(", ", _providers.Keys.OrderBy(k => k, StringComparer.Ordinal)) + ".");
            return _default;
        }
        else
        {
            provider = named;
        }

        // Checked for the default too, not just an explicit choice. A fresh install runs on the
        // default with no Ollama URL set, which is exactly the silent "never called anything" state
        // this alert exists to surface — skipping the check there would hide the common case.
        if (!provider.IsConfigured)
        {
            _alerts.Raise(
                FallbackAlertKey,
                "critical",
                $"Embedding provider '{provider.Name}' is selected but not configured — no vectors can be produced.",
                $"'{provider.Name}' is missing its server URL or API key, so the engine has never called it. "
                + "Content similarity, 'More Like This' and the taste blend on trending are all inert until "
                + "this is set under Settings → Embeddings.");
        }

        return provider;
    }
}
