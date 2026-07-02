using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Integrations.Tmdb;

/// <summary>
/// Downloads and caches TMDB watch-provider logos on the server (one file per provider id, shared by
/// every title on that service) so the app can render a crisp studio/provider tag from the LAN without
/// depending on the TMDB CDN per device. Served back via the Images controller.
/// </summary>
public interface IProviderLogoCache
{
    /// <summary>
    /// Ensures the given provider's logo is cached locally, downloading it once from TMDB if absent.
    /// No-op when the id/path is empty or the file already exists. Fail-soft.
    /// </summary>
    /// <param name="providerId">The TMDB provider id (cache key).</param>
    /// <param name="logoPath">The TMDB logo path (relative; prefixed with the image base to download).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task EnsureCachedAsync(int providerId, string? logoPath, CancellationToken ct = default);

    /// <summary>Resolves a cached provider logo to a physical file path + content type (null path when not cached).</summary>
    /// <param name="providerId">The TMDB provider id.</param>
    /// <returns>The file path (or null) and its content type.</returns>
    (string? Path, string ContentType) Resolve(int providerId);
}
