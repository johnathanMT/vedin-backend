using PortfolioApi.Common;
using PortfolioApi.DTOs.Astrology;

namespace PortfolioApi.Interfaces;

/// <summary>Generates a personalised Vedic reading from a summarised chart, via
/// an OpenAI-compatible chat-completions provider.</summary>
public interface IAiReadingService
{
    Task<ApiResponse<AiReadingResponseDto>> GenerateAsync(AiReadingRequestDto req, CancellationToken ct = default);

    /// <summary>Verifies the configured API key + model against the provider WITHOUT
    /// generating a reading (calls the provider's lightweight "list models" endpoint).
    /// Used by GET /api/astrology/ai-health so admins can confirm setup at a glance.</summary>
    Task<ApiResponse<object>> CheckHealthAsync(CancellationToken ct = default);
}
