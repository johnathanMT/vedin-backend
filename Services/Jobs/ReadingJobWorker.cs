using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PortfolioApi.Data;
using PortfolioApi.DTOs.Astrology;
using PortfolioApi.Interfaces;
using PortfolioApi.Security;
using PortfolioApi.Services.Pdf;

namespace PortfolioApi.Services.Jobs;

/// <summary>
/// Drains <see cref="IReadingJobQueue"/> and generates the approved readings.
/// <para>
/// Runs one job at a time on purpose: Gemini calls are the expensive resource and
/// serialising them keeps usage predictable. A failed job records the reason on the
/// row and leaves the request recoverable rather than silently dropping it.
/// </para>
/// </summary>
public sealed class ReadingJobWorker : BackgroundService
{
    private const int MaxAttempts = 3;

    private readonly IReadingJobQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ReadingJobWorker> _log;
    private readonly string _encKey;

    public ReadingJobWorker(
        IReadingJobQueue queue,
        IServiceScopeFactory scopes,
        IConfiguration cfg,
        ILogger<ReadingJobWorker> log)
    {
        _queue = queue;
        _scopes = scopes;
        _log = log;
        _encKey = cfg["Astrology:EncryptionKey"] ?? cfg["Jwt:Key"] ?? "astrology-fallback-key-set-in-env";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueOrphansAsync(stoppingToken);

        await foreach (var id in _queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;   // shutting down
            }
            catch (Exception ex)
            {
                // Never let one bad job kill the worker — the queue must keep draining.
                _log.LogError(ex, "Reading job {Id} failed unexpectedly.", id);
            }
        }
    }

    /// <summary>
    /// The queue lives in memory, so a deploy or Render cold start drops whatever was
    /// pending. Anything left mid-flight in the database is picked back up here.
    /// </summary>
    private async Task RequeueOrphansAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var orphans = await db.ReadingRequests
                .Where(r => r.Status == "Queued" || r.Status == "Processing")
                .OrderBy(r => r.Id)
                .Select(r => r.Id)
                .ToListAsync(ct);

            foreach (var id in orphans)
            {
                if (!_queue.TryEnqueue(id))
                {
                    _log.LogWarning("Queue full while recovering reading {Id} at startup.", id);
                    break;
                }
            }

            if (orphans.Count > 0)
                _log.LogInformation("Recovered {Count} interrupted reading job(s) at startup.", orphans.Count);
        }
        catch (Exception ex)
        {
            // A database hiccup at boot must not prevent the worker from serving new jobs.
            _log.LogError(ex, "Could not recover interrupted reading jobs at startup.");
        }
    }

    private async Task ProcessAsync(int id, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ai = scope.ServiceProvider.GetRequiredService<IAiReadingService>();

        var row = await db.ReadingRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
        {
            _log.LogWarning("Reading job {Id} has no matching row — dropping.", id);
            return;
        }

        // Approved rows already carry markdown; a duplicate enqueue must not re-bill the API.
        if (row.Status == "Approved" && !string.IsNullOrEmpty(row.Markdown)) return;

        AiReadingRequestDto? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AiReadingRequestDto>(FieldCrypto.Decrypt(row.PayloadJson, _encKey));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Reading job {Id}: stored payload could not be decrypted.", id);
            await FailAsync(db, row.Id, "Stored chart payload could not be read.", ct);
            return;
        }

        if (payload is null)
        {
            await FailAsync(db, row.Id, "Stored chart payload was empty.", ct);
            return;
        }

        row.Status = "Processing";
        row.Attempts += 1;
        await db.SaveChangesAsync(ct);

        var result = await ai.GenerateAsync(payload, row.Id, ct);

        if (!result.Success || result.Data is null)
        {
            var reason = result.Message ?? "The AI provider returned no reading.";
            if (row.Attempts < MaxAttempts && _queue.TryEnqueue(row.Id))
            {
                row.Status = "Queued";
                row.LastError = reason;
                await db.SaveChangesAsync(ct);
                _log.LogWarning("Reading job {Id} attempt {N} failed ({Reason}) — requeued.", id, row.Attempts, reason);
            }
            else
            {
                await FailAsync(db, row.Id, reason, ct);
                _log.LogError("Reading job {Id} gave up after {N} attempt(s): {Reason}", id, row.Attempts, reason);
            }
            return;
        }

        row.Markdown = FieldCrypto.Encrypt(result.Data.Markdown, _encKey);
        row.Model = result.Data.Model;
        row.Status = "Approved";
        row.LastError = null;
        row.ApprovedAt = DateTime.UtcNow;

        // Render the premium report here rather than on download, so the querent's
        // click streams stored bytes instead of waiting on a layout pass.
        try
        {
            var pdf = scope.ServiceProvider.GetRequiredService<IReadingPdfService>();
            row.PdfDocument = pdf.Render(new VedinReportModel
            {
                QuerentName = payload.Name ?? string.Empty,
                BirthDate = payload.BirthDate,
                BirthTime = payload.BirthTime,
                Location = payload.Location,
                Chart = payload,
                ReadingMarkdown = result.Data.Markdown,
                Model = result.Data.Model,
                Burmese = !string.Equals(payload.Language, "en", StringComparison.OrdinalIgnoreCase),
            });
            row.PdfGeneratedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // The reading itself succeeded — a failed render must not lose it. The
            // download path re-renders on demand when PdfDocument is null.
            _log.LogError(ex, "Reading job {Id}: PDF render failed; reading kept.", id);
        }

        await db.SaveChangesAsync(ct);

        // The intermediate drafts exist only to make a retry cheap. Once the reading is
        // finished they are redundant copies of personal material, so they are dropped.
        try
        {
            await db.ReadingStepOutputs.Where(o => o.ReadingRequestId == row.Id).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reading job {Id}: could not clear intermediate step outputs.", id);
        }

        _log.LogInformation("Reading job {Id} generated with {Model}.", id, row.Model);
    }

    private static async Task FailAsync(AppDbContext db, int id, string reason, CancellationToken ct)
    {
        var row = await db.ReadingRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return;
        row.Status = "Failed";
        row.LastError = reason.Length > 480 ? reason[..480] : reason;
        await db.SaveChangesAsync(ct);
    }
}
