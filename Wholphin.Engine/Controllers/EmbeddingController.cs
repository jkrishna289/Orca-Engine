using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Embedding;
using Wholphin.Engine.Recommendation;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Embedding diagnostics: which provider is actually in use, what the content-vector index currently
/// holds, and an on-demand probe that proves a provider works instead of waiting for it to fail.
/// </summary>
/// <remarks>
/// The gap this closes: every other signal about embedding health is negative. An alert fires when
/// something breaks, so an empty dashboard means "nothing has failed since startup" — which includes
/// "nothing has been attempted". <see cref="Test"/> is the positive check.
/// </remarks>
[ApiController]
[Route("OrcaEngine/Embedding")]
[Produces("application/json")]
[Authorize(Policy = "RequiresElevation")]
public class EmbeddingController : ControllerBase
{
    /// <summary>
    /// The probe corpus. Two texts that mean nearly the same thing and one that does not, so the
    /// result can show whether the model is embedding MEANING rather than merely answering 200.
    /// </summary>
    /// <remarks>
    /// A provider returning constant or near-random vectors passes a "did the call succeed" check
    /// and fails this one, because the related pair would not score above the unrelated pair.
    /// </remarks>
    private static readonly string[] Probes =
    {
        "A Hindi-language Bollywood musical romance set in Mumbai, with song and dance numbers.",
        "A romantic Bollywood film in Hindi, filmed in Mumbai, featuring musical numbers.",
        "A documentary about deep-sea geology, hydrothermal vents and volcanic activity.",
    };

    private readonly IEnumerable<IEmbeddingProvider> _providers;
    private readonly IEmbeddingService _embeddings;
    private readonly IContentVectorIndex _index;
    private readonly VectorStore _store;
    private readonly IEngineMetrics _metrics;
    private readonly IEngineAlerts _alerts;

    /// <summary>Initializes a new instance of the <see cref="EmbeddingController"/> class.</summary>
    /// <param name="providers">All registered embedding providers.</param>
    /// <param name="embeddings">The embedding service (resolution + batching).</param>
    /// <param name="index">The content-vector index.</param>
    /// <param name="store">Durable vector storage (how much survives a restart).</param>
    /// <param name="metrics">Operational counters.</param>
    /// <param name="alerts">Sticky health alerts.</param>
    public EmbeddingController(
        IEnumerable<IEmbeddingProvider> providers,
        IEmbeddingService embeddings,
        IContentVectorIndex index,
        VectorStore store,
        IEngineMetrics metrics,
        IEngineAlerts alerts)
    {
        _providers = providers;
        _embeddings = embeddings;
        _index = index;
        _store = store;
        _metrics = metrics;
        _alerts = alerts;
    }

    /// <summary>Returns the state of the embedding layer and the current vector index.</summary>
    /// <returns>The diagnostics.</returns>
    /// <remarks>
    /// Reads <see cref="IContentVectorIndex.Current"/> rather than <c>GetAsync</c>, so opening the
    /// page cannot kick off a full-catalog embed. "No index yet" is a real answer here.
    /// </remarks>
    [HttpGet("Diagnostics")]
    public async Task<ActionResult> Diagnostics(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var configured = string.IsNullOrWhiteSpace(config?.EmbeddingProvider)
            ? EmbeddingService.DefaultProviderName
            : config!.EmbeddingProvider.Trim();
        var active = _embeddings.ActiveProviderName;
        var snapshot = _index.Current;

        var providers = _providers
            .Select(p => new
            {
                p.Name,
                p.IsConfigured,
                p.ModelId,
                p.MaxBatchSize,
                EffectiveBatchSize = EmbeddingService.ResolveBatchSize(p, config?.EmbeddingBatchSize),
                IsConfiguredChoice = string.Equals(p.Name, configured, StringComparison.OrdinalIgnoreCase),
                IsActive = string.Equals(p.Name, active, StringComparison.OrdinalIgnoreCase),
            })
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        return Ok(new
        {
            ConfiguredProvider = configured,
            ActiveProvider = active,
            ActiveModel = _embeddings.ActiveModelId,

            // What survives a restart. Zero here with a healthy provider means every restart is
            // re-embedding the catalog from scratch.
            StoredVectors = await _store.CountAsync(cancellationToken).ConfigureAwait(false),

            // The headline: an operator picked a provider and is silently getting another one.
            UsingFallback = !string.Equals(configured, active, StringComparison.OrdinalIgnoreCase),
            BatchSize = new
            {
                Configured = config?.EmbeddingBatchSize ?? EmbeddingService.DefaultBatchSize,
                Min = EmbeddingService.MinBatchSize,
                Max = EmbeddingService.MaxBatchSize,
                Default = EmbeddingService.DefaultBatchSize,
            },
            RetryAttemptsPerBatch = EmbeddingService.MaxAttemptsPerBatch,
            Providers = providers,
            Index = snapshot is null
                ? null
                : new
                {
                    snapshot.Count,
                    snapshot.ProviderName,
                    snapshot.BuiltAtUtc,
                },
            Alerts = _alerts.Active()
                .Where(a => a.Key.StartsWith("embedding.", StringComparison.Ordinal))
                .ToList(),
            Metrics = _metrics.Snapshot()
                .Where(kv => kv.Key.StartsWith("embedding.", StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        });
    }

    /// <summary>
    /// Embeds three short probe texts through a provider and reports what came back.
    /// </summary>
    /// <param name="provider">Provider to test; defaults to the configured one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The probe result.</returns>
    /// <remarks>
    /// Calls the provider DIRECTLY rather than through <see cref="IEmbeddingService"/>, so the result
    /// describes this provider and nothing else — including which provider was asked, which the
    /// service's resolution would otherwise quietly change.
    /// </remarks>
    [HttpPost("Test")]
    public async Task<ActionResult> Test([FromQuery] string? provider, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var wanted = string.IsNullOrWhiteSpace(provider)
            ? (string.IsNullOrWhiteSpace(config?.EmbeddingProvider) ? EmbeddingService.DefaultProviderName : config!.EmbeddingProvider.Trim())
            : provider.Trim();

        var target = _providers.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return Ok(Failure(wanted, "not-registered", $"'{wanted}' is not a registered embedding provider."));
        }

        if (!target.IsConfigured)
        {
            return Ok(Failure(
                target.Name,
                "not-configured",
                $"'{target.Name}' has no server URL or API key configured, so the engine never calls it. "
                + "Content similarity is inert until this is set."));
        }

        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<ContentVector>? vectors;
        try
        {
            vectors = await target.EmbedAsync(Probes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Ok(Failure(target.Name, "threw", $"{ex.GetType().Name}: {ex.Message}", stopwatch.ElapsedMilliseconds));
        }

        stopwatch.Stop();

        if (vectors is null)
        {
            return Ok(Failure(
                target.Name,
                "no-vectors",
                $"'{target.Name}' was called and returned nothing. Check that the server is running and "
                + "reachable, that the model name is right and pulled, and that any quota is not exhausted. "
                + "The engine log carries the HTTP status.",
                stopwatch.ElapsedMilliseconds));
        }

        if (vectors.Count != Probes.Length)
        {
            return Ok(Failure(
                target.Name,
                "wrong-count",
                $"'{target.Name}' returned {vectors.Count} vectors for {Probes.Length} inputs, which breaks "
                + "the positional pairing the index relies on.",
                stopwatch.ElapsedMilliseconds));
        }

        var related = ContentVector.Cosine(vectors[0], vectors[1]);
        var unrelated = ContentVector.Cosine(vectors[0], vectors[2]);
        var dense = vectors[0].DenseValues;

        return Ok(new
        {
            Provider = target.Name,
            Ok = true,
            Kind = "dense",
            Outcome = related > unrelated ? "healthy" : "suspect",
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            Dimensions = dense?.Count ?? 0,
            RelatedScore = Math.Round(related, 4),
            UnrelatedScore = Math.Round(unrelated, 4),

            // The semantic check, in words. A provider can answer 200 with constant vectors; that
            // passes a connectivity test and fails this one.
            Message = related > unrelated
                ? $"'{target.Name}' answered in {stopwatch.ElapsedMilliseconds} ms and scored the two "
                  + $"related texts ({related:F3}) above the unrelated one ({unrelated:F3}) — it is embedding meaning, not noise."
                : $"'{target.Name}' answered, but scored two near-identical texts ({related:F3}) no higher "
                  + $"than an unrelated one ({unrelated:F3}). The vectors carry little or no meaning — check the model name.",
        });
    }

    private static object Failure(string provider, string outcome, string message, long? elapsedMs = null) => new
    {
        Provider = provider,
        Ok = false,
        Outcome = outcome,
        ElapsedMs = elapsedMs,
        Kind = (string?)null,
        Dimensions = 0,
        RelatedScore = 0.0,
        UnrelatedScore = 0.0,
        Message = message,
    };
}
