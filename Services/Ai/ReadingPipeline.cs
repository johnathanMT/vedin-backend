using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PortfolioApi.Common;
using PortfolioApi.Data;
using PortfolioApi.DTOs.Astrology;
using PortfolioApi.Interfaces;
using PortfolioApi.Models;
using PortfolioApi.Security;
using PortfolioApi.Services.Ai.Steps;

namespace PortfolioApi.Services.Ai;

/// <summary>
/// Runs the reading steps in order and returns the finished document.
/// <para>
/// Replaces a single ~8k-token prompt that produced the whole reading in one call. That
/// design had three problems this one addresses: any failure discarded everything, the
/// grounding rule was unenforceable because no code ever compared the prose to the
/// chart, and Burmese quality degraded because one prompt carried the persona, the
/// structure, the astrology and the language standard at once.
/// </para>
/// <para>
/// Completed steps are persisted per reading request, so a retry resumes rather than
/// restarts — the seven life-area drafts are paid for once.
/// </para>
/// </summary>
public sealed class ReadingPipeline : IAiReadingService
{
    private readonly IReadOnlyList<IReadingStep> _steps;
    private readonly IChatModel _model;
    private readonly AppDbContext _db;
    private readonly ILogger<ReadingPipeline> _log;
    private readonly string _encKey;

    public ReadingPipeline(
        ChartAnalysisStep analysis,
        LifeAreaDraftStep areas,
        SynthesisStep synthesis,
        LanguagePolishStep polish,
        GroundingCheckStep grounding,
        IChatModel model,
        AppDbContext db,
        IConfiguration cfg,
        ILogger<ReadingPipeline> log)
    {
        // Grounding runs LAST: it verifies the finished, language-polished prose against
        // the computed placements and is best-effort (never discards a good reading).
        _steps = new IReadingStep[] { analysis, areas, synthesis, polish, grounding };
        _model = model;
        _db = db;
        _log = log;
        _encKey = cfg["Astrology:EncryptionKey"] ?? cfg["Jwt:Key"] ?? "astrology-fallback-key-set-in-env";
    }

    public async Task<ApiResponse<AiReadingResponseDto>> GenerateAsync(
        AiReadingRequestDto req, int? requestId = null, CancellationToken ct = default)
    {
        var ctx = new ReadingContext { Chart = req, RequestId = requestId };
        await RestoreAsync(ctx, ct);

        var watch = Stopwatch.StartNew();

        foreach (var step in _steps)
        {
            if (ctx.Outputs.ContainsKey(step.Id))
            {
                _log.LogInformation("Reading {Id}: step '{Step}' restored from a previous attempt.",
                    requestId, step.Id);
                continue;
            }

            var stepWatch = Stopwatch.StartNew();
            var result = await step.RunAsync(ctx, ct);
            stepWatch.Stop();

            if (!result.Success || string.IsNullOrWhiteSpace(result.Data))
            {
                _log.LogError("Reading {Id}: step '{Step}' failed after {Ms}ms — {Reason}",
                    requestId, step.Id, stepWatch.ElapsedMilliseconds, result.Message);

                // Whatever completed stays on the row, so the retry starts where this stopped.
                return ApiResponse<AiReadingResponseDto>.Fail(
                    $"Reading generation failed at the '{step.Id}' stage: {result.Message}", 502);
            }

            ctx.Outputs[step.Id] = result.Data!;
            await PersistAsync(ctx, step.Id, result.Data!, ct);

            _log.LogInformation("Reading {Id}: step '{Step}' done in {Ms}ms ({Chars} chars).",
                requestId, step.Id, stepWatch.ElapsedMilliseconds, result.Data!.Length);
        }

        // Prefer the grounded text; fall back through polish → synthesis if grounding
        // was skipped or an earlier resume left it absent.
        var markdown = ctx.Get(GroundingCheckStep.StepId);
        if (string.IsNullOrWhiteSpace(markdown)) markdown = ctx.Get(LanguagePolishStep.StepId);
        if (string.IsNullOrWhiteSpace(markdown)) markdown = ctx.Get(SynthesisStep.StepId);
        if (string.IsNullOrWhiteSpace(markdown))
            return ApiResponse<AiReadingResponseDto>.Fail("The pipeline produced an empty reading.", 502);

        watch.Stop();
        _log.LogInformation("Reading {Id}: complete in {Sec}s ({Chars} chars).",
            requestId, watch.Elapsed.TotalSeconds.ToString("0.0"), markdown.Length);

        return ApiResponse<AiReadingResponseDto>.Ok(new AiReadingResponseDto
        {
            Markdown = markdown.Trim(),
            Model = _model.ModelId,
            GeneratedAt = DateTime.UtcNow,
        }, "Reading generated.");
    }

    public Task<ApiResponse<object>> CheckHealthAsync(CancellationToken ct = default)
        => _model.CheckHealthAsync(ct);

    /// <summary>Loads any steps a previous attempt finished.</summary>
    private async Task RestoreAsync(ReadingContext ctx, CancellationToken ct)
    {
        if (ctx.RequestId is not int id) return;

        try
        {
            var rows = await _db.ReadingStepOutputs
                .Where(o => o.ReadingRequestId == id)
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                var text = FieldCrypto.Decrypt(row.Content, _encKey);
                if (!string.IsNullOrWhiteSpace(text)) ctx.Outputs[row.StepId] = text;
            }
        }
        catch (Exception ex)
        {
            // Resumption is an optimisation; losing it costs tokens, not correctness.
            _log.LogWarning(ex, "Reading {Id}: could not restore intermediate steps — starting fresh.", id);
        }
    }

    private async Task PersistAsync(ReadingContext ctx, string stepId, string content, CancellationToken ct)
    {
        if (ctx.RequestId is not int id) return;

        try
        {
            var existing = await _db.ReadingStepOutputs
                .FirstOrDefaultAsync(o => o.ReadingRequestId == id && o.StepId == stepId, ct);

            if (existing is null)
            {
                _db.ReadingStepOutputs.Add(new ReadingStepOutput
                {
                    ReadingRequestId = id,
                    StepId = stepId,
                    Content = FieldCrypto.Encrypt(content, _encKey),
                    CreatedAt = DateTime.UtcNow,
                });
            }
            else
            {
                existing.Content = FieldCrypto.Encrypt(content, _encKey);
                existing.CreatedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reading {Id}: could not persist step '{Step}'.", id, stepId);
        }
    }
}
