namespace Wholphin.Engine.Settings;

/// <summary>
/// The engine's runtime feature flags. Resolved per scope through the layered settings chain
/// (system defaults → admin config → group → user override) and serialized into the bootstrap
/// payload so the client can adapt its UI without a redeploy.
/// </summary>
public class FeatureFlags
{
    /// <summary>Gets or sets a value indicating whether personalized recommendations (the "For You" row + spotlight picks) are produced.</summary>
    public bool Personalization { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the rotating spotlight (Hero billboard) row is emitted.</summary>
    public bool Spotlight { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the "Continue Watching" row is emitted.</summary>
    public bool ContinueWatching { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the "Trending" row is emitted.</summary>
    public bool Trending { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether content-similarity rows ("Because You Watched X") are emitted.</summary>
    public bool SimilarityRows { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the recommender injects controlled exploration (novel/serendipitous picks).</summary>
    public bool Exploration { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the "New Since You Were Away" row is emitted. (Milestone 8.)</summary>
    public bool NewSinceAway { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the "Coming Soon" calendar row is emitted. (Milestone 8.)</summary>
    public bool ComingSoon { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether mood-based collection rows are emitted. (Milestone 8.)</summary>
    public bool MoodCollections { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the server pre-buffers trailers (needs yt-dlp/ffmpeg). (Milestone 8.)</summary>
    public bool TrailerPrebuffer { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether availability-aware discovery (Jellyseerr/TMDB) rows are surfaced. (Milestone 2.)</summary>
    public bool JellyseerrDiscovery { get; set; }

    /// <summary>Gets or sets a value indicating whether TMDB-direct trending/popular discovery is pulled into the catalog. (Milestone 7.)</summary>
    public bool TmdbDiscovery { get; set; }

    /// <summary>Gets or sets a value indicating whether engine-proxied requests are accepted. (Milestone 2.)</summary>
    public bool Requests { get; set; }

    /// <summary>Returns a shallow copy so a resolved layer can be mutated without affecting defaults.</summary>
    /// <returns>A clone of these flags.</returns>
    public FeatureFlags Clone() => (FeatureFlags)MemberwiseClone();
}
