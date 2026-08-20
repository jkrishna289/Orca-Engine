using System;

namespace Wholphin.Engine.Data.Entities;

/// <summary>
/// One catalog item's stored content vector, so the index survives a restart instead of costing a
/// full re-embed of the catalog every time the server comes up.
/// </summary>
/// <remarks>
/// <para>
/// The three identity columns are what make a stored vector safe to reuse. A vector is only valid
/// for the exact <see cref="Provider"/> and <see cref="ModelId"/> that produced it — a different
/// model means different dimensions and different geometry, and comparing across them is
/// meaningless. <see cref="DocumentHash"/> covers the third way a vector goes stale: the item's text
/// changed. That last one is not hypothetical — backfilling a title's original language rewrites its
/// document, and without the hash the engine would keep serving the vector that never knew about it.
/// </para>
/// <para>
/// Storing the hash also turns rebuilds incremental for free: an item whose document still hashes
/// the same is reused, so adding ten titles embeds ten, not the whole catalog.
/// </para>
/// </remarks>
public class CatalogItemVector
{
    /// <summary>Gets or sets the catalog item this vector describes. One vector per item.</summary>
    public long CatalogItemId { get; set; }

    /// <summary>Gets or sets the embedding provider that produced it (e.g. "ollama").</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Gets or sets the model that produced it (e.g. "nomic-embed-text").</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Gets or sets the vector length, so a corrupt or truncated blob is detectable.</summary>
    public int Dimensions { get; set; }

    /// <summary>Gets or sets the hash of the document that was embedded.</summary>
    public string DocumentHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the L2-normalized vector, little-endian IEEE-754 singles.</summary>
    public byte[] Vector { get; set; } = Array.Empty<byte>();

    /// <summary>Gets or sets when this row was written (UTC).</summary>
    public DateTime UpdatedAt { get; set; }
}
