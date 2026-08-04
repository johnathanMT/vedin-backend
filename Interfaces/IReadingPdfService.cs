using PortfolioApi.Services.Pdf;

namespace PortfolioApi.Interfaces;

/// <summary>Renders the premium Vedin report to a PDF byte array.</summary>
public interface IReadingPdfService
{
    byte[] Render(VedinReportModel model);
}
