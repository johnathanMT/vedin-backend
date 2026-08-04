namespace PortfolioApi.Models;

/// <summary>
/// A durable cache of computed birth charts.
/// <para>
/// The chart is a pure, deterministic function of the birth inputs, so it is safe to
/// memoise indefinitely. <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>
/// alone loses everything on each deploy and on every Render cold start, which is
/// exactly when the ephemeris cost hurts most — this table survives both.
/// </para>
/// <para>No PII is stored beyond the birth coordinates already present in the key,
/// and the payload is the same public chart JSON returned to the caller.</para>
/// </summary>
public class ChartCacheEntry
{
    public int Id { get; set; }

    /// <summary>SHA-256 of the canonical birth-input cache key (fixed width, indexable).</summary>
    public string CacheKey { get; set; } = string.Empty;

    /// <summary>Serialised BirthChartData.</summary>
    public string ChartJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Touched on every hit, so a sweep can evict genuinely cold rows.</summary>
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
}
