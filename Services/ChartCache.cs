using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PortfolioApi.Common;
using PortfolioApi.Data;
using PortfolioApi.DTOs.Astrology;
using PortfolioApi.Interfaces;
using PortfolioApi.Models;

namespace PortfolioApi.Services;

/// <summary>
/// Memory tier in front of a database tier. A hit in memory costs nothing; a hit in
/// the database costs one indexed lookup and a deserialise, still far cheaper than
/// re-running Swiss Ephemeris. Cache failures are always non-fatal: a broken cache
/// degrades to a recompute, never to a failed request.
/// </summary>
public sealed class ChartCache : IChartCache
{
    private static readonly TimeSpan MemorySliding = TimeSpan.FromHours(6);
    private static readonly TimeSpan MemoryAbsolute = TimeSpan.FromHours(24);

    private readonly IMemoryCache _memory;
    private readonly AppDbContext _db;
    private readonly ILogger<ChartCache> _log;

    public ChartCache(IMemoryCache memory, AppDbContext db, ILogger<ChartCache> log)
    {
        _memory = memory;
        _db = db;
        _log = log;
    }

    /// <summary>The raw key is long and variable-width; hash it so the column can be
    /// a fixed-width unique index.</summary>
    private static string Hash(string cacheKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))).ToLowerInvariant();

    public async Task<ApiResponse<BirthChartData>?> GetAsync(string cacheKey, CancellationToken ct = default)
    {
        if (_memory.TryGetValue(cacheKey, out ApiResponse<BirthChartData>? hot) && hot is not null)
            return hot;

        try
        {
            var hash = Hash(cacheKey);
            var row = await _db.ChartCacheEntries.FirstOrDefaultAsync(e => e.CacheKey == hash, ct);
            if (row is null) return null;

            var data = JsonSerializer.Deserialize<BirthChartData>(row.ChartJson);
            if (data is null) return null;

            var result = ApiResponse<BirthChartData>.Ok(data);
            SetMemory(cacheKey, result);

            row.LastAccessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Persistent chart cache read failed — recomputing.");
            return null;
        }
    }

    public async Task SetAsync(string cacheKey, ApiResponse<BirthChartData> chart, CancellationToken ct = default)
    {
        if (chart.Data is null) return;
        SetMemory(cacheKey, chart);

        try
        {
            var hash = Hash(cacheKey);
            if (await _db.ChartCacheEntries.AnyAsync(e => e.CacheKey == hash, ct)) return;

            _db.ChartCacheEntries.Add(new ChartCacheEntry
            {
                CacheKey = hash,
                ChartJson = JsonSerializer.Serialize(chart.Data),
                CreatedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // A racing insert on the unique index lands here too — harmless.
            _log.LogWarning(ex, "Persistent chart cache write failed (non-fatal).");
        }
    }

    private void SetMemory(string cacheKey, ApiResponse<BirthChartData> chart)
        => _memory.Set(cacheKey, chart, new MemoryCacheEntryOptions
        {
            SlidingExpiration = MemorySliding,
            AbsoluteExpirationRelativeToNow = MemoryAbsolute,
            Size = 1,
        });
}
