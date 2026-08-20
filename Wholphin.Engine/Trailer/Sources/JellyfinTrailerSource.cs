using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Metadata;

namespace Wholphin.Engine.Trailer.Sources;

/// <summary>
/// The trailer URLs Jellyfin's own metadata providers already found for a library item.
/// </summary>
/// <remarks>
/// Free and already on disk — no API key, no request. Only library items have one, so this silently
/// yields nothing for discovery rows.
/// </remarks>
public class JellyfinTrailerSource : ITrailerSource
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<JellyfinTrailerSource> _logger;

    /// <summary>Initializes a new instance of the <see cref="JellyfinTrailerSource"/> class.</summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="logger">The logger.</param>
    public JellyfinTrailerSource(ILibraryManager libraryManager, ILogger<JellyfinTrailerSource> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "jellyfin";

    /// <inheritdoc />
    public Task<string?> ResolveAsync(MediaIdentity identity, string? preferredLanguage, CancellationToken cancellationToken)
    {
        if (identity.JellyfinItemId is not { } id || id == Guid.Empty)
        {
            return Task.FromResult<string?>(null);
        }

        try
        {
            var trailers = _libraryManager.GetItemById(id)?.RemoteTrailers;
            return Task.FromResult(trailers?.Select(t => t.Url).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orca Engine: could not read Jellyfin trailers for {Id}.", id);
            return Task.FromResult<string?>(null);
        }
    }
}
