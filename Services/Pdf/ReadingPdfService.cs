using PortfolioApi.Interfaces;
using QuestPDF.Fluent;

namespace PortfolioApi.Services.Pdf;

/// <inheritdoc />
public sealed class ReadingPdfService : IReadingPdfService
{
    private readonly ILogger<ReadingPdfService> _log;

    public ReadingPdfService(ILogger<ReadingPdfService> log) => _log = log;

    public byte[] Render(VedinReportModel model)
    {
        var doc = new VedinReportDocument(model);
        var bytes = doc.GeneratePdf();
        _log.LogInformation("Rendered reading PDF ({Kb} KB) for {Name}.",
            bytes.Length / 1024, string.IsNullOrWhiteSpace(model.QuerentName) ? "(anonymous)" : model.QuerentName);
        return bytes;
    }
}
