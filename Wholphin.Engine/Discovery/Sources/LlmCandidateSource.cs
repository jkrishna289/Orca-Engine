using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Integrations.Tmdb;
using Wholphin.Engine.Llm;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Discovery.Sources;

/// <summary>
/// Per-user candidate source that asks the configured LLM to PROPOSE titles from the viewer's watch
/// history (ported from SuggestArr), then resolves each proposal to TMDB via search. Proposals the
/// model attributes to a watched title become Because-You-Watched candidates carrying the real
/// taste seed; the rest surface as AI picks in You Might Like. The primary source when LLM
/// discovery is on — the TMDB sources fill only when this under-delivers.
/// </summary>
public class LlmCandidateSource : IDiscoverySource
{
    /// <summary>The source's intrinsic confidence — above seedrelated's 0.8; each pick is individually curated.</summary>
    public const double Confidence = 0.95;

    private readonly ILlmCandidateGenerator _generator;
    private readonly ITmdbClient _tmdb;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<LlmCandidateSource> _logger;

    /// <summary>Initializes a new instance of the <see cref="LlmCandidateSource"/> class.</summary>
    /// <param name="generator">The LLM candidate generator.</param>
    /// <param name="tmdb">The TMDB client (title resolution).</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    public LlmCandidateSource(ILlmCandidateGenerator generator, ITmdbClient tmdb, IEngineMetrics metrics, ILogger<LlmCandidateSource> logger)
    {
        _generator = generator;
        _tmdb = tmdb;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "llm";

    /// <inheritdoc />
    public DiscoveryPickKind Kind => DiscoveryPickKind.LlmPick;

    /// <inheritdoc />
    public DiscoverySourceScope Scope => DiscoverySourceScope.PerUser;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveryCandidate>> GatherAsync(DiscoveryContext context, CancellationToken ct = default)
    {
        var candidates = new List<DiscoveryCandidate>();
        if (!context.Settings.Features.LlmDiscovery || context.Profile is not { } profile)
        {
            return candidates;
        }

        foreach (var mediaType in new[] { MediaType.Movie, MediaType.Series })
        {
            var recommendations = await _generator
                .GenerateAsync(context, mediaType, context.Tuning.LlmMaxPerMediaType, ct)
                .ConfigureAwait(false);
            foreach (var rec in recommendations)
            {
                var result = await _tmdb.SearchAsync(rec.Title, rec.Year, mediaType, ct).ConfigureAwait(false);
                if (result is null)
                {
                    _metrics.Increment("llm.discovery.unresolved");
                    _logger.LogDebug("Orca Engine: could not resolve LLM pick '{Title}' ({Year}) on TMDB.", rec.Title, rec.Year);
                    continue;
                }

                var seed = MatchSeed(rec.SourceTitle, profile.Seeds, mediaType);
                candidates.Add(new DiscoveryCandidate
                {
                    Result = result,
                    Kind = seed is null ? DiscoveryPickKind.LlmPick : DiscoveryPickKind.BecauseYouWatched,
                    Seed = seed,
                    LlmRationale = string.IsNullOrWhiteSpace(rec.Rationale) ? null : rec.Rationale,
                    Attributions = { new SourceAttribution { SourceType = Name, SourceConfidence = Confidence } },
                });
            }
        }

        return candidates;
    }

    /// <summary>
    /// Resolves the model's source-title attribution back to a real taste seed (normalized,
    /// case-insensitive compare; TMDB-bearing seeds only, since seeded picks need the seed's TMDB
    /// id for row grouping). Null when unattributed or unmatched.
    /// </summary>
    internal static TasteSeed? MatchSeed(string? sourceTitle, IReadOnlyList<TasteSeed> seeds, MediaType mediaType)
    {
        if (string.IsNullOrWhiteSpace(sourceTitle))
        {
            return null;
        }

        var normalized = LlmDiscoveryPromptBuilder.NormalizeTitle(sourceTitle);
        if (normalized.Length == 0)
        {
            return null;
        }

        return seeds.FirstOrDefault(s => s.TmdbId is > 0
            && s.MediaType == mediaType
            && string.Equals(LlmDiscoveryPromptBuilder.NormalizeTitle(s.Title), normalized, StringComparison.OrdinalIgnoreCase));
    }
}
