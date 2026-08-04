using PortfolioApi.Common;
using PortfolioApi.DTOs.Astrology;

namespace PortfolioApi.Interfaces;

/// <summary>
/// Two-tier cache for computed birth charts: an in-process memory tier for hot reads
/// and a database tier that survives deploys and cold starts.
/// </summary>
public interface IChartCache
{
    /// <summary>Returns the cached chart for these birth inputs, or null on a miss.</summary>
    Task<ApiResponse<BirthChartData>?> GetAsync(string cacheKey, CancellationToken ct = default);

    /// <summary>Stores a successful compute in both tiers.</summary>
    Task SetAsync(string cacheKey, ApiResponse<BirthChartData> chart, CancellationToken ct = default);
}
