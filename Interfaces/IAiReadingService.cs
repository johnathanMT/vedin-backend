using PortfolioApi.Common;
using PortfolioApi.DTOs.Astrology;

namespace PortfolioApi.Interfaces;

/// <summary>Generates a personalised Vedic reading from a summarised chart.</summary>
public interface IAiReadingService
{
    /// <summary>
    /// Generates the reading. <paramref name="requestId"/> identifies the reading request
    /// row so a staged implementation can persist and resume intermediate work; pass null
    /// for a one-off generation that need not survive a failure.
    /// </summary>
    Task<ApiResponse<AiReadingResponseDto>> GenerateAsync(
        AiReadingRequestDto req, int? requestId = null, CancellationToken ct = default);

    /// <summary>Verifies the configured API key + model against the provider WITHOUT
    /// generating a reading (calls the provider's lightweight "list models" endpoint).
    /// Used by GET /api/astrology/ai-health so admins can confirm setup at a glance.</summary>
    Task<ApiResponse<object>> CheckHealthAsync(CancellationToken ct = default);
}
