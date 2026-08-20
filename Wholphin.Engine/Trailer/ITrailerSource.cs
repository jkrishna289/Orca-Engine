using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Metadata;

namespace Wholphin.Engine.Trailer;

/// <summary>
/// One place a trailer URL might be found. Sources are tried in the admin-configured order until one
/// answers.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="IMetadataProvider"/> deliberately: half the implementations are not
/// HTTP at all (Jellyfin reads the library, "stored" reads SQLite, "search" shells out to yt-dlp), so
/// folding them into the metadata port would create providers with no upstream health to track.
/// </remarks>
public interface ITrailerSource
{
    /// <summary>Gets the stable lowercase token this source is named by in <c>TrailerSourceOrder</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Resolves a playable trailer URL for a title.
    /// </summary>
    /// <param name="identity">The title's resolved ids, title and year.</param>
    /// <param name="preferredLanguage">Preferred audio language (ISO 639-1), or null for the configured default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A URL yt-dlp can download, or null when this source has nothing.</returns>
    Task<string?> ResolveAsync(MediaIdentity identity, string? preferredLanguage, CancellationToken cancellationToken);
}
