using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PortfolioApi.Common;
using PortfolioApi.Data;
using PortfolioApi.DTOs.Astrology;
using PortfolioApi.Interfaces;
using PortfolioApi.Models;
using PortfolioApi.Security;
using PortfolioApi.Services;
using PortfolioApi.Services.Ai;
using PortfolioApi.Services.Pdf;

namespace PortfolioApi.Controllers;

/// <summary>
/// Vedic astrology — computes a sidereal Rasi (D1) birth chart from birth details.
/// Public, stateless, rate-limited. POST /api/astrology/chart.
/// </summary>
[ApiController]
[Route("api/astrology")]
[Produces("application/json")]
public class AstrologyController : ControllerBase
{
    private readonly IAstrologyService _service;
    private readonly AppDbContext _db;
    private readonly IEmailService _email;
    private readonly IConfiguration _cfg;
    private readonly IAiReadingService _ai;
    private readonly IChatModel _model;
    private readonly IChartCache _cache;
    private readonly IReadingJobQueue _jobs;
    private readonly IReadingPdfService _pdf;
    private readonly string _encKey;

    public AstrologyController(IAstrologyService service, AppDbContext db, IEmailService email, IConfiguration cfg, IAiReadingService ai, IChatModel model, IChartCache cache, IReadingJobQueue jobs, IReadingPdfService pdf)
    {
        _pdf = pdf;
        _service = service;
        _db = db;
        _email = email;
        _cfg = cfg;
        _ai = ai;
        _model = model;
        _cache = cache;
        _jobs = jobs;
        // Dedicated key preferred; falls back to the JWT key so it works out of the box.
        _encKey = cfg["Astrology:EncryptionKey"] ?? cfg["Jwt:Key"] ?? "astrology-fallback-key-set-in-env";
    }

    /// <summary>Admin-only diagnostic: verify the AI provider key + model WITHOUT
    /// generating a reading. Calls the provider's ListModels endpoint and reports
    /// whether the key is valid and the configured model is available.</summary>
    /// <remarks>GET /api/astrology/ai-health</remarks>
    [HttpGet("ai-health")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> AiHealth(CancellationToken ct)
    {
        var result = await _ai.CheckHealthAsync(ct);
        return StatusCode(result.StatusCode, result);
    }

    /// <summary>Compute a sidereal Rasi (D1) chart.</summary>
    /// <remarks>
    ///     POST /api/astrology/chart
    ///     { "year":1998, "month":1, "day":1, "hour":12, "minute":0, "second":0,
    ///       "timeZone":"Asia/Yangon", "latitude":16.8409, "longitude":96.1735 }
    /// </remarks>
    [HttpPost("chart")]
    [EnableRateLimiting("astrology")]
    [ProducesResponseType(typeof(ApiResponse<BirthChartData>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Chart([FromBody] BirthChartRequest req, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse<object>.Fail(
                "Validation failed.", 400,
                ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList()));

        // ── Server-side compute cache ────────────────────────────────────────────
        // ComputeRasiChart is a pure, deterministic Swiss Ephemeris calculation:
        // identical birth inputs always yield the identical chart. Caching the
        // result keyed on those inputs turns repeat "check chart" clicks (same
        // person re-opening, switching tabs, retries) into instant hits and takes
        // the heavy ephemeris math off the CPU. Output is public (returned to the
        // caller anyway), so there's no per-user data-leak risk.
        //
        // The cache is two-tier: memory for hot reads, database underneath so a
        // Render cold start or deploy doesn't throw the whole cache away.
        var cacheKey = "chart:" +
            $"{req.Year:0000}-{req.Month:00}-{req.Day:00}T{req.Hour:00}:{req.Minute:00}:{req.Second:00}|" +
            $"{req.TimeZone}|{req.Latitude:F5}|{req.Longitude:F5}|{req.Ayanamsa?.ToLowerInvariant()}";

        // The chart is a pure function of its inputs, so the cache key doubles as a
        // strong validator (ETag). A client that already holds this exact chart and
        // sends If-None-Match gets a 304 with an empty body — zero recompute, zero
        // payload. (Browsers don't auto-condition POSTs, so this benefits API/CDN/
        // conditional clients; same-session repeats are already served from cache.)
        var etag = "\"" + System.Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cacheKey)))[..16] + "\"";
        if (string.Equals(Request.Headers.IfNoneMatch.ToString(), etag, StringComparison.Ordinal))
            return StatusCode(StatusCodes.Status304NotModified);
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "private, max-age=0, must-revalidate";

        var result = await _cache.GetAsync(cacheKey, ct);
        if (result is null)
        {
            result = _service.ComputeRasiChart(req);
            if (result.StatusCode == 200)
                await _cache.SetAsync(cacheKey, result, ct);   // only memoise successful computes
        }

        return result.StatusCode switch
        {
            200 => Ok(result),
            400 => BadRequest(result),
            _   => StatusCode(result.StatusCode, result),
        };
    }

    // ── Remedy (yatra) / contact request — public, stored encrypted ──────────────
    [HttpPost("remedy-request")]
    [EnableRateLimiting("general")]
    public async Task<IActionResult> RemedyRequest([FromBody] RemedyRequestDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse<object>.Fail("Validation failed.", 400));

        var row = new RemedyRequest
        {
            Name = FieldCrypto.Encrypt(dto.Name, _encKey),
            Contact = FieldCrypto.Encrypt(dto.Contact, _encKey),
            Area = dto.Area,
            Message = FieldCrypto.Encrypt(dto.Message, _encKey),
            BirthInfo = FieldCrypto.Encrypt($"{dto.BirthDate} {dto.BirthTime}".Trim(), _encKey),
            Handled = false,
            CreatedAt = DateTime.UtcNow,
        };
        _db.RemedyRequests.Add(row);
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Ok(new { row.Id }, "Remedy request received."));
    }

    // ── Opt-in chart save — public, stored encrypted only WITH consent ──────────
    [HttpPost("save-chart")]
    [EnableRateLimiting("astrology")]
    public async Task<IActionResult> SaveChart([FromBody] SaveChartDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse<object>.Fail("Validation failed.", 400));
        if (!dto.Consent)
            return Ok(ApiResponse<object>.Ok(new { stored = false }, "No consent — not stored."));

        var row = new QuerentChart
        {
            Name = FieldCrypto.Encrypt(dto.Name, _encKey),
            Gender = dto.Gender,
            BirthDate = FieldCrypto.Encrypt(dto.BirthDate, _encKey),
            BirthTime = FieldCrypto.Encrypt(dto.BirthTime, _encKey),
            TimeZone = dto.TimeZone,
            Location = FieldCrypto.Encrypt($"{dto.Latitude},{dto.Longitude}", _encKey),
            NayNan = dto.NayNan,
            Consent = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.QuerentCharts.Add(row);
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Ok(new { row.Id, stored = true }, "Saved."));
    }

    // ── Admin: remedy requests (decrypted) ──────────────────────────────────────
    [HttpGet("admin/remedies")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> AdminRemedies()
    {
        // Robust: never 500. Missing table/columns → return []; a single row that
        // fails to decrypt is shown with a "[decrypt-error]" marker (via SafeDecrypt)
        // rather than blowing up the whole list, so you can still delete bad rows.
        try
        {
            var rows = await _db.RemedyRequests.OrderByDescending(r => r.CreatedAt).Take(500).ToListAsync();
            var view = rows.Select(r => new RemedyView
            {
                Id = r.Id,
                Name = SafeDecrypt(r.Name),
                Contact = SafeDecrypt(r.Contact),
                Area = r.Area,
                Message = SafeDecrypt(r.Message),
                BirthInfo = SafeDecrypt(r.BirthInfo),
                Handled = r.Handled,
                Status = string.IsNullOrWhiteSpace(r.Status) ? "Pending" : r.Status,
                Notes = r.Notes ?? string.Empty,
                CreatedAt = r.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            }).ToList();
            return Ok(ApiResponse<List<RemedyView>>.Ok(view, "OK"));
        }
        catch (Exception)
        {
            return Ok(ApiResponse<List<RemedyView>>.Ok(new List<RemedyView>(), "OK"));
        }
    }

    private static readonly string[] ValidStatuses = { "Pending", "InProgress", "Completed", "Cancelled" };

    // ── Admin: set status (Pending / InProgress / Completed / Cancelled) ─────────
    [HttpPatch("admin/remedies/{id:int}/status")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> SetStatus(int id, [FromBody] StatusDto dto)
    {
        var row = await _db.RemedyRequests.FindAsync(id);
        if (row is null) return NotFound(ApiResponse<object>.Fail("Not found.", 404));
        if (!ValidStatuses.Contains(dto.Status)) return BadRequest(ApiResponse<object>.Fail("Invalid status.", 400));
        row.Status = dto.Status;
        row.Handled = dto.Status is "Completed" or "Cancelled";
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Ok(new { row.Id, row.Status }, "Status updated."));
    }

    // ── Admin: edit internal notes ──────────────────────────────────────────────
    [HttpPatch("admin/remedies/{id:int}/notes")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> SetNotes(int id, [FromBody] NotesDto dto)
    {
        var row = await _db.RemedyRequests.FindAsync(id);
        if (row is null) return NotFound(ApiResponse<object>.Fail("Not found.", 404));
        row.Notes = (dto.Notes ?? string.Empty).Length > 8000 ? dto.Notes![..8000] : dto.Notes ?? string.Empty;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Ok(new { row.Id }, "Notes saved."));
    }

    // ── Admin: send an astrological reading / reply to the client by email ───────
    [HttpPost("admin/remedies/{id:int}/reply")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Reply(int id, [FromBody] ReplyDto dto)
    {
        if (!ModelState.IsValid) return BadRequest(ApiResponse<object>.Fail("Validation failed.", 400));
        var row = await _db.RemedyRequests.FindAsync(id);
        if (row is null) return NotFound(ApiResponse<object>.Fail("Not found.", 404));

        string contact = FieldCrypto.Decrypt(row.Contact, _encKey).Trim();
        if (!contact.Contains('@')) return BadRequest(ApiResponse<object>.Fail("This client did not leave an email address.", 400));

        string name = FieldCrypto.Decrypt(row.Name, _encKey);
        string subject = string.IsNullOrWhiteSpace(dto.Subject) ? "Vedin — သင့် ဗေဒင်ဟောစာတမ်း" : dto.Subject;
        bool sent = await _email.SendAsync(contact, subject, ReadingReplyEmail(name, dto.Body));
        if (sent) { row.Status = "Completed"; row.Handled = true; await _db.SaveChangesAsync(); }
        return Ok(ApiResponse<object>.Ok(new { emailSent = sent }, sent ? "Reading emailed to the client." : "Could not send (SMTP not configured)."));
    }

    // ── Admin: delete a remedy request ──────────────────────────────────────────
    [HttpDelete("admin/remedies/{id:int}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> DeleteRemedy(int id)
    {
        var row = await _db.RemedyRequests.FindAsync(id);
        if (row is null) return NotFound(ApiResponse<object>.Fail("Not found.", 404));
        _db.RemedyRequests.Remove(row);
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Ok(new { id }, "Deleted."));
    }

    // ── Admin: delete a saved querent chart ─────────────────────────────────────
    [HttpDelete("admin/charts/{id:int}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> DeleteChart(int id)
    {
        var row = await _db.QuerentCharts.FindAsync(id);
        if (row is null) return NotFound(ApiResponse<object>.Fail("Not found.", 404));
        _db.QuerentCharts.Remove(row);
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Ok(new { id }, "Deleted."));
    }

    // Styled reading/reply email (purple / gold). Body is admin-authored text.
    private static string ReadingReplyEmail(string name, string body)
    {
        string greeting = string.IsNullOrWhiteSpace(name) ? "မင်္ဂလာပါ" : $"မင်္ဂလာပါ {System.Net.WebUtility.HtmlEncode(name)}";
        string safeBody = System.Net.WebUtility.HtmlEncode(body).Replace("\n", "<br>");
        const string tpl = """
<!doctype html><html><body style="margin:0;background:#0b0a14;font-family:Segoe UI,Helvetica,Arial,sans-serif">
  <div style="max-width:600px;margin:0 auto;padding:32px 20px">
    <div style="background:linear-gradient(135deg,#14121f,#1b1830);border:1px solid rgba(168,85,247,.35);border-radius:18px;padding:34px 28px;box-shadow:0 0 60px -20px rgba(168,85,247,.5)">
      <div style="font:600 12px 'Segoe UI';letter-spacing:.3em;text-transform:uppercase;color:#eab308;margin-bottom:14px">Vedin &middot; Sayar Bhone Min Thike Din</div>
      <p style="margin:0 0 14px;color:#f2ede0;font-size:15px">{{GREETING}},</p>
      <div style="color:#cfc7b6;font-size:14px;line-height:1.95">{{BODY}}</div>
      <p style="margin:22px 0 0;color:#726a5c;font-size:12px;line-height:1.8">ဆရာ ဘုန်းမင်းသိုက်ဒင် &middot; Vedin Vedic Astrology</p>
    </div>
  </div>
</body></html>
""";
        return tpl.Replace("{{GREETING}}", greeting).Replace("{{BODY}}", safeBody);
    }

    // ── Admin: saved querent charts (decrypted) ─────────────────────────────────
    [HttpGet("admin/charts")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> AdminCharts()
    {
        var rows = await _db.QuerentCharts.OrderByDescending(c => c.CreatedAt).Take(500).ToListAsync();
        var view = rows.Select(c => new QuerentChartView
        {
            Id = c.Id,
            Name = FieldCrypto.Decrypt(c.Name, _encKey),
            Gender = c.Gender,
            BirthDate = FieldCrypto.Decrypt(c.BirthDate, _encKey),
            BirthTime = FieldCrypto.Decrypt(c.BirthTime, _encKey),
            TimeZone = c.TimeZone,
            Location = FieldCrypto.Decrypt(c.Location, _encKey),
            NayNan = c.NayNan,
            CreatedAt = c.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
        }).ToList();
        return Ok(ApiResponse<List<QuerentChartView>>.Ok(view, "OK"));
    }

    // ── PDF request (public) — stored Pending, encrypted ────────────────────────
    [HttpPost("request-pdf")]
    [EnableRateLimiting("general")]
    public async Task<IActionResult> RequestPdf([FromBody] RequestPdfDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse<object>.Fail("Validation failed.", 400));
        var row = new PdfRequest
        {
            Email = FieldCrypto.Encrypt(dto.Email, _encKey),
            Name = FieldCrypto.Encrypt(dto.Name, _encKey),
            BirthInfo = FieldCrypto.Encrypt($"{dto.BirthDate} {dto.BirthTime}".Trim(), _encKey),
            ApprovalStatus = "Pending",
            CreatedAt = DateTime.UtcNow,
        };
        _db.PdfRequests.Add(row);
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Ok(new { row.Id }, "PDF request received — awaiting admin approval."));
    }

    // ── Admin: list PDF requests ────────────────────────────────────────────────
    [HttpGet("admin/pdf-requests")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> AdminPdfRequests()
    {
        var rows = await _db.PdfRequests.OrderByDescending(r => r.CreatedAt).Take(500).ToListAsync();
        var view = rows.Select(r => new PdfRequestView
        {
            Id = r.Id,
            Email = FieldCrypto.Decrypt(r.Email, _encKey),
            Name = FieldCrypto.Decrypt(r.Name, _encKey),
            BirthInfo = FieldCrypto.Decrypt(r.BirthInfo, _encKey),
            ApprovalStatus = r.ApprovalStatus,
            CreatedAt = r.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
        }).ToList();
        return Ok(ApiResponse<List<PdfRequestView>>.Ok(view, "OK"));
    }

    // ── Admin: approve + email a secure one-time link (48h) ─────────────────────
    [HttpPost("approve-pdf/{id:int}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> ApprovePdf(int id)
    {
        var row = await _db.PdfRequests.FindAsync(id);
        if (row is null) return NotFound(ApiResponse<object>.Fail("Not found.", 404));

        string token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        row.DownloadToken = token;
        row.TokenExpiry = DateTime.UtcNow.AddHours(48);
        row.ApprovalStatus = "Approved";
        await _db.SaveChangesAsync();

        string baseUrl = (_cfg["App:PdfDownloadBase"] ?? "https://myweb-zqv1.onrender.com/api/astrology/download-pdf").TrimEnd('/');
        string link = $"{baseUrl}?token={token}";
        string email = FieldCrypto.Decrypt(row.Email, _encKey);
        bool sent = await _email.SendAsync(email, "သင်၏ ဗေဒင်ဟောစာတမ်း (PDF) — Vedin", PdfApprovedEmail(link));

        return Ok(ApiResponse<object>.Ok(new { row.Id, row.ApprovalStatus, emailSent = sent }, sent ? "Approved & emailed." : "Approved (SMTP not configured — set Smtp__* env vars)."));
    }

    // ── Public: secure one-time PDF download ────────────────────────────────────
    [HttpGet("download-pdf")]
    [EnableRateLimiting("general")]
    public async Task<IActionResult> DownloadPdf([FromQuery] string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return BadRequest("Missing token.");
        var row = await _db.PdfRequests.FirstOrDefaultAsync(r => r.DownloadToken == token, ct);
        if (row is null || row.ApprovalStatus != "Approved" || row.TokenExpiry is null || row.TokenExpiry < DateTime.UtcNow)
            return StatusCode(410, "This link is invalid, already used, or expired.");

        string name = FieldCrypto.Decrypt(row.Name, _encKey);
        string birth = FieldCrypto.Decrypt(row.BirthInfo, _encKey);

        // Prefer the report already rendered by the background job for this querent;
        // fall back to a cover-only report so the link never dead-ends.
        var stored = await FindRenderedReport(name, ct);
        var pdf = stored ?? _pdf.Render(new VedinReportModel
        {
            QuerentName = name,
            BirthDate = string.IsNullOrWhiteSpace(birth) ? null : birth,
        });

        row.ApprovalStatus = "Downloaded";   // one-time: invalidate the link
        row.DownloadToken = string.Empty;
        await _db.SaveChangesAsync(ct);

        return File(pdf, "application/pdf", "vedin-reading.pdf");
    }

    /// <summary>Most recent pre-rendered report for a querent name, if one exists.</summary>
    private async Task<byte[]?> FindRenderedReport(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var candidates = await _db.ReadingRequests
            .Where(r => r.Status == "Approved" && r.PdfDocument != null)
            .OrderByDescending(r => r.ApprovedAt)
            .Take(50)
            .ToListAsync(ct);

        // QuerentName is encrypted at rest, so the match happens after decryption.
        return candidates
            .FirstOrDefault(r => string.Equals(SafeDecrypt(r.QuerentName), name, StringComparison.OrdinalIgnoreCase))
            ?.PdfDocument;
    }

    // Premium branded HTML email (purple / gold).
    private static string PdfApprovedEmail(string link)
    {
        const string tpl = """
<!doctype html><html><body style="margin:0;background:#0b0a14;font-family:Segoe UI,Helvetica,Arial,sans-serif">
  <div style="max-width:560px;margin:0 auto;padding:32px 20px">
    <div style="background:linear-gradient(135deg,#14121f,#1b1830);border:1px solid rgba(168,85,247,.35);border-radius:18px;padding:34px 28px;box-shadow:0 0 60px -20px rgba(168,85,247,.5)">
      <div style="font:600 12px 'Segoe UI';letter-spacing:.3em;text-transform:uppercase;color:#eab308;margin-bottom:14px">Vedin &middot; Vedic Astrology</div>
      <h1 style="margin:0 0 8px;font-size:22px;color:#f2ede0">Sayar Bhone Min Thike Din</h1>
      <p style="margin:0 0 22px;color:#b9b09b;font-size:14px;line-height:1.9">ဂုဏ်ယူပါသည်။ သင်၏ ဗေဒင်ဟောစာတမ်း (PDF) ကို Admin မှ အတည်ပြုပေးလိုက်ပါပြီ။ အောက်ပါလင့်ခ်မှတစ်ဆင့် လုံခြုံစွာ ရယူနိုင်ပါသည်။</p>
      <a href="{{LINK}}" style="display:inline-block;background:linear-gradient(135deg,#a855f7,#eab308);color:#14110d;font-weight:700;text-decoration:none;padding:14px 26px;border-radius:12px;font-size:15px">Download your reading (PDF)</a>
      <p style="margin:22px 0 0;color:#726a5c;font-size:12px;line-height:1.8">This secure link works once and expires in 48 hours. If you didn't request this, please ignore this email.</p>
    </div>
    <p style="text-align:center;color:#4a443b;font-size:11px;margin-top:16px">Vedin &middot; myothant.dev</p>
  </div>
</body></html>
""";
        return tpl.Replace("{{LINK}}", link);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  AI Reading — direct generation. LOCKED to admin: the public path is now the
    //  manual-approval workflow below (request-reading → admin approve), which keeps
    //  the API key from ever being triggered by an anonymous click.
    // ─────────────────────────────────────────────────────────────────────────────
    [HttpPost("generate-ai-reading")]
    [Authorize(Policy = "AdminOnly")]
    [EnableRateLimiting("ai")]
    [ProducesResponseType(typeof(ApiResponse<AiReadingResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GenerateAiReading([FromBody] AiReadingRequestDto req, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse<object>.Fail(
                "Validation failed.", 400,
                ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList()));

        // No request row to resume from — this is the admin's direct, ad-hoc generation.
        var result = await _ai.GenerateAsync(req, null, ct);

        // Auto-persist for signed-in customers so the reading isn't lost.
        if (result.Success && result.Data is not null && TryCustomerId(out int cid))
        {
            try
            {
                var title = string.IsNullOrWhiteSpace(req.Name)
                    ? $"Reading · {DateTime.UtcNow:yyyy-MM-dd}"
                    : $"{req.Name!.Trim()} · {DateTime.UtcNow:yyyy-MM-dd}";
                var row = new AiReading
                {
                    CustomerId = cid,
                    Title = FieldCrypto.Encrypt(title, _encKey),
                    Markdown = FieldCrypto.Encrypt(result.Data.Markdown, _encKey),
                    Model = result.Data.Model,
                    CreatedAt = DateTime.UtcNow,
                };
                _db.AiReadings.Add(row);
                await _db.SaveChangesAsync(ct);
                result.Data.SavedId = row.Id;
            }
            catch (Exception)
            {
                // Persistence is best-effort; never fail the reading over a save error.
            }
        }

        return StatusCode(result.StatusCode, result);
    }

    /// <summary>List the signed-in account's saved AI readings (decrypted, newest first).</summary>
    [HttpGet("my-readings")]
    [Authorize]
    public async Task<IActionResult> MyReadings()
    {
        if (!TryCustomerId(out int id))
            return Unauthorized(ApiResponse<object>.Fail("Not a customer token.", 401));

        var rows = await _db.AiReadings
            .Where(r => r.CustomerId == id)
            .OrderByDescending(r => r.CreatedAt)
            .Take(100)
            .ToListAsync();

        var view = rows.Select(r => new AiReadingView
        {
            Id = r.Id,
            Title = FieldCrypto.Decrypt(r.Title, _encKey),
            Markdown = FieldCrypto.Decrypt(r.Markdown, _encKey),
            Model = r.Model,
            CreatedAt = r.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
        }).ToList();

        return Ok(ApiResponse<List<AiReadingView>>.Ok(view, "OK"));
    }

    /// <summary>Delete one of the account's saved readings.</summary>
    [HttpDelete("my-readings/{id:int}")]
    [Authorize]
    public async Task<IActionResult> DeleteReading(int id)
    {
        if (!TryCustomerId(out int cid))
            return Unauthorized(ApiResponse<object>.Fail("Not a customer token.", 401));

        var row = await _db.AiReadings.FirstOrDefaultAsync(r => r.Id == id && r.CustomerId == cid);
        if (row is null) return NotFound(ApiResponse<object>.Fail("Reading not found.", 404));

        _db.AiReadings.Remove(row);
        await _db.SaveChangesAsync();
        return Ok(ApiResponse.OkNoData("Deleted."));
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  PREMIUM MANUAL-APPROVAL READING WORKFLOW
    //  request-reading (no AI call) → admin approve (AI call) → user views Approved.
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>Submit a reading request. Does NOT call the AI. Enforces one request
    /// per querent per 30 days, then stores a Pending record for the Sayar to review.</summary>
    [HttpPost("request-reading")]
    [EnableRateLimiting("ai")]
    [ProducesResponseType(typeof(ApiResponse<ReadingStatusView>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RequestReading([FromBody] AiReadingRequestDto req, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse<object>.Fail("Validation failed.", 400,
                ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList()));

        var hash = QuerentHash(req.Name, req.BirthDate, req.BirthTime, req.Location);
        var cutoff = DateTime.UtcNow.AddDays(-30);

        // 30-day rate limit / de-dup: return the existing request instead of a new one.
        var existing = await _db.ReadingRequests
            .Where(r => r.QuerentHash == hash && r.CreatedAt >= cutoff)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            var evw = ToStatusView(existing);
            evw.AlreadyRequested = true;
            return Ok(ApiResponse<ReadingStatusView>.Ok(evw,
                existing.Status == "Approved"
                    ? "Your reading is ready."
                    : "You have already requested a reading this month. The Sayar is reviewing it."));
        }

        TryCustomerId(out int cid);
        var row = new ReadingRequest
        {
            CustomerId = cid == 0 ? null : cid,
            QuerentHash = hash,
            QuerentName = FieldCrypto.Encrypt(string.IsNullOrWhiteSpace(req.Name) ? "(no name)" : req.Name!.Trim(), _encKey),
            PayloadJson = FieldCrypto.Encrypt(JsonSerializer.Serialize(req), _encKey),
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
        };
        _db.ReadingRequests.Add(row);
        await _db.SaveChangesAsync(ct);

        // Notify the Sayar that a new request is waiting (best-effort).
        try
        {
            var adminEmail = _cfg["App:AdminEmail"] ?? _cfg["Smtp:User"];
            if (!string.IsNullOrWhiteSpace(adminEmail))
                await _email.SendAsync(adminEmail, "🔔 ဟောစာတမ်း တောင်းဆိုမှုအသစ် — Vedin",
                    NotifyAdminEmail($"Querent: {(string.IsNullOrWhiteSpace(req.Name) ? "(no name)" : req.Name)}", "reading"));
        }
        catch { /* email is best-effort */ }

        return Ok(ApiResponse<ReadingStatusView>.Ok(ToStatusView(row),
            "Request received — awaiting the Sayar's review."));
    }

    /// <summary>Check the status of a querent's reading request (poll on page visit).</summary>
    [HttpPost("reading-status")]
    [EnableRateLimiting("general")]
    public async Task<IActionResult> ReadingStatus([FromBody] ReadingStatusQueryDto q, CancellationToken ct)
    {
        var hash = QuerentHash(q.Name, q.BirthDate, q.BirthTime, q.Location);
        var row = await _db.ReadingRequests
            .Where(r => r.QuerentHash == hash)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return row is null
            ? Ok(ApiResponse<ReadingStatusView>.Ok(new ReadingStatusView { Status = "None" }, "No request found."))
            : Ok(ApiResponse<ReadingStatusView>.Ok(ToStatusView(row), "OK"));
    }

    /// <summary>Ask the Sayar to email the approved reading as a PDF. The querent
    /// supplies the delivery email in the body: { "email": "…" }.</summary>
    [HttpPost("reading/{id:int}/request-pdf")]
    [EnableRateLimiting("general")]
    public async Task<IActionResult> RequestReadingPdf(int id, [FromBody] RequestPdfEmailDto dto, CancellationToken ct)
    {
        var row = await _db.ReadingRequests.FindAsync(new object?[] { id }, ct);
        if (row is null) return NotFound(ApiResponse<object>.Fail("Reading not found.", 404));
        if (row.Status != "Approved")
            return BadRequest(ApiResponse<object>.Fail("The reading has not been approved yet.", 400));

        var email = (dto?.Email ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return BadRequest(ApiResponse<object>.Fail("A valid email address is required.", 400));

        row.ClientEmail = FieldCrypto.Encrypt(email, _encKey);
        row.PdfRequested = true;
        row.PdfSent = false;
        await _db.SaveChangesAsync(ct);

        try
        {
            var adminEmail = _cfg["App:AdminEmail"] ?? _cfg["Smtp:User"];
            if (!string.IsNullOrWhiteSpace(adminEmail))
                await _email.SendAsync(adminEmail, "📄 PDF ဟောစာတမ်း တောင်းဆိုမှု — Vedin",
                    NotifyAdminEmail($"Reading request #{id} — the querent asked for the PDF at {email}.", "pdf"));
        }
        catch { /* best-effort */ }

        return Ok(ApiResponse.OkNoData("PDF request sent to the Sayar."));
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  GROUNDED CONVERSATIONAL FOLLOW-UP  (Task 6)
    //  The querent asks a question about THEIR finished reading; the model may use
    //  ONLY the computed chart facts + the reading text, and declines anything else.
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>Answer a follow-up question strictly grounded in the querent's own reading.</summary>
    [HttpPost("reading/{id:int}/ask")]
    [Authorize]
    [EnableRateLimiting("ai")]
    [ProducesResponseType(typeof(ApiResponse<ReadingAnswerDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AskReading(int id, [FromBody] ReadingAskDto dto, CancellationToken ct)
    {
        if (!TryCustomerId(out int cid))
            return Unauthorized(ApiResponse<object>.Fail("Not a customer token.", 401));
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(dto?.Question))
            return BadRequest(ApiResponse<object>.Fail("A question is required.", 400));

        var (row, chart, markdown) = await LoadOwnedReadingAsync(id, cid, ct);
        if (row is null) return NotFound(ApiResponse<object>.Fail("Reading not found.", 404));

        var burmese = chart is null || !string.Equals(chart.Language, "en", StringComparison.OrdinalIgnoreCase);
        var facts = chart is null ? "(chart facts unavailable)" : new ReadingContext { Chart = chart }.ChartFacts();

        // The question leads, so the model answers THAT rather than summarising the reading;
        // the chart facts + reading follow as the only source of truth.
        var user =
$"""
=== THE QUERENT'S QUESTION (answer THIS, specifically and completely) ===
{dto!.Question.Trim()}

Use ONLY the following as your source of truth. Pull in a placement or dasha only when it
directly helps answer the question above.

{facts}

=== THE READING ALREADY PREPARED FOR THE QUERENT ===
{markdown}
""";

        // 2000 tokens: a Burmese sentence spends far more tokens per character than English, so a
        // lower cap was cutting answers off mid-sentence. Temperature 0.3 keeps a warm, natural tone.
        var result = await _model.CompleteAsync(AskSystem(burmese), user,
            new ChatOptions { Temperature = 0.3, MaxOutputTokens = 2000 }, ct);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Data))
            return StatusCode(result.StatusCode, ApiResponse<object>.Fail(result.Message, result.StatusCode));

        return Ok(ApiResponse<ReadingAnswerDto>.Ok(new ReadingAnswerDto { Answer = result.Data!.Trim() }, "OK"));
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  STREAMING READING  (Task 7) — Server-Sent Events
    //  The reading is already generated AND grounding-checked, so this streams the
    //  STORED text token-by-token rather than re-invoking the model: re-running the
    //  LLM on every open would spend tokens and could drift from the approved,
    //  grounded reading. The SSE framing matches a live model stream, so the client
    //  hook is identical whether the tokens originate here or from IChatModel.
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>Stream the querent's approved reading token-by-token as SSE.</summary>
    [HttpGet("reading/{id:int}/stream")]
    [Authorize]
    [EnableRateLimiting("ai")]
    public async Task StreamReading(int id, CancellationToken ct)
    {
        // Resolve auth + ownership BEFORE any body is written, so a failure is a real
        // HTTP status rather than an SSE error event on a 200 response.
        if (!TryCustomerId(out int cid)) { Response.StatusCode = StatusCodes.Status401Unauthorized; return; }

        var (row, _, markdown) = await LoadOwnedReadingAsync(id, cid, ct);
        if (row is null || string.IsNullOrEmpty(markdown))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";   // stop nginx/CDN from buffering the stream

        try
        {
            foreach (var chunk in ChunkGraphemes(markdown!, 6))
            {
                if (ct.IsCancellationRequested) break;
                var payload = JsonSerializer.Serialize(new { t = chunk });
                await Response.WriteAsync($"data: {payload}\n\n", ct);
                await Response.Body.FlushAsync(ct);
                await Task.Delay(10, ct);   // gentle typing cadence
            }
            await Response.WriteAsync("event: done\ndata: 1\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
        catch (OperationCanceledException) { /* the querent navigated away — normal */ }
    }

    /// <summary>Load an Approved reading owned by this customer, with its chart snapshot
    /// (for grounding) and decrypted markdown. Returns nulls when there is no match.</summary>
    private async Task<(ReadingRequest? Row, AiReadingRequestDto? Chart, string? Markdown)>
        LoadOwnedReadingAsync(int id, int customerId, CancellationToken ct)
    {
        var row = await _db.ReadingRequests
            .FirstOrDefaultAsync(r => r.Id == id && r.CustomerId == customerId, ct);
        if (row is null || row.Status != "Approved" || string.IsNullOrEmpty(row.Markdown))
            return (null, null, null);

        AiReadingRequestDto? chart = null;
        try { chart = JsonSerializer.Deserialize<AiReadingRequestDto>(FieldCrypto.Decrypt(row.PayloadJson, _encKey)); }
        catch { /* an unreadable snapshot only costs grounding richness, not the answer */ }

        return (row, chart, SafeDecrypt(row.Markdown!));
    }

    /// <summary>Split text into small grapheme-cluster chunks so a Burmese stream reveals
    /// smoothly without ever slicing a combining cluster mid-character.</summary>
    private static IEnumerable<string> ChunkGraphemes(string text, int perChunk)
    {
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        var sb = new StringBuilder();
        var n = 0;
        while (e.MoveNext())
        {
            sb.Append((string)e.Current);
            if (++n >= perChunk) { yield return sb.ToString(); sb.Clear(); n = 0; }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>System prompt for a grounded follow-up: chart + reading are the only
    /// admissible facts, and anything outside that scope is politely declined.</summary>
    private static string AskSystem(bool burmese) =>
$"""
You are the astrologer's warm, friendly assistant. The querent has ALREADY received a full Vedic
reading and now asks ONE specific follow-up question. Your only job is to answer THAT question
directly, naturally, and completely. Rules you must never break:

1. Answer the querent's specific question directly. If they ask about love or relationships,
   answer about love; if about career, answer about career. Do NOT pivot to an unrelated topic —
   for example, never answer a love question with dasha or money analysis — unless that detail is
   genuinely needed to explain the answer to THIS question.
2. Ground every point ONLY in the CHART SNAPSHOT facts and the READING text provided. Do not
   invent placements, dashas, dates, yogas, or predictions that are not present in them. Bring in a
   placement, house, or dasha ONLY when it directly supports your answer to this question.
3. Do NOT include structural headers, scaffolding, or labels of any kind. Never write
   "Paragraph 1:", "Paragraph X:", "Section", "Dasha Connection", or similar. Never emit raw
   markdown tags such as #, ##, ###, *, or **. Write plain, flowing sentences only.
4. If the question is outside the scope of this chart or reading — about other people, general
   knowledge, or medical, legal, or financial decisions, or details the chart does not contain —
   politely decline in a sentence or two and suggest two or three relevant questions the querent
   COULD ask about their own chart.
5. Write a COMPLETE answer of two to four short paragraphs and FINISH every sentence — never stop
   mid-sentence or mid-thought. Keep it focused enough to finish naturally within the space.
6. Write your entire answer in {(burmese ? "natural, polite, conversational Burmese (မြန်မာဘာသာဖြင့်သာ)" : "English")},
   in a warm, friendly tone, as if speaking directly to the querent.
""";

    // ── Admin: list reading requests (optionally filter by status) ──────────────
    [HttpGet("admin/reading-requests")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> AdminReadingRequests([FromQuery] string? status)
    {
        var query = _db.ReadingRequests.AsQueryable();
        if (string.Equals(status, "Generating", StringComparison.OrdinalIgnoreCase))
        {
            // One admin tab for everything the worker owns, so a queued, in-flight or
            // failed reading can never fall between the Pending/Approved/Rejected tabs.
            query = query.Where(r => r.Status == "Queued" || r.Status == "Processing" || r.Status == "Failed");
        }
        else if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(r => r.Status == status);
        }

        var rows = await query.OrderByDescending(r => r.CreatedAt).Take(500).ToListAsync();
        var accounts = await LoadAccounts(rows);
        return Ok(ApiResponse<List<ReadingRequestAdminView>>.Ok(
            rows.Select(r => ToAdminView(r, Lookup(accounts, r))).ToList(), "OK"));
    }

    // ── Admin: reading requests awaiting a PDF email (queue) ─────────────────────
    [HttpGet("admin/pdf-reading-requests")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> AdminPdfReadingRequests()
    {
        var rows = await _db.ReadingRequests
            .Where(r => r.PdfRequested)
            .OrderByDescending(r => r.CreatedAt)
            .Take(500)
            .ToListAsync();
        var accounts = await LoadAccounts(rows);
        return Ok(ApiResponse<List<ReadingRequestAdminView>>.Ok(
            rows.Select(r => ToAdminView(r, Lookup(accounts, r))).ToList(), "OK"));
    }

    // Batch-load the Customer accounts referenced by a set of reading requests.
    private async Task<Dictionary<int, Customer>> LoadAccounts(List<ReadingRequest> rows)
    {
        var ids = rows.Where(r => r.CustomerId.HasValue).Select(r => r.CustomerId!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, Customer>();
        return await _db.Customers.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id);
    }
    private static Customer? Lookup(Dictionary<int, Customer> map, ReadingRequest r)
        => r.CustomerId.HasValue && map.TryGetValue(r.CustomerId.Value, out var c) ? c : null;

    /// <summary>Admin marks a PDF as manually sent — clears it from the PDF queue.</summary>
    [HttpPost("admin/reading-requests/{id:int}/mark-pdf-sent")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> MarkPdfSent(int id, CancellationToken ct)
    {
        var row = await _db.ReadingRequests.FindAsync(new object?[] { id }, ct);
        if (row is null) return NotFound(ApiResponse<object>.Fail("Request not found.", 404));
        row.PdfRequested = false;
        row.PdfSent = true;
        await _db.SaveChangesAsync(ct);
        return Ok(ApiResponse<object>.Ok(new { row.Id }, "Marked as sent."));
    }

    private ReadingRequestAdminView ToAdminView(ReadingRequest r, Customer? account) => new()
    {
        Id = r.Id,
        QuerentName = SafeDecrypt(r.QuerentName),
        ClientEmail = string.IsNullOrEmpty(r.ClientEmail) ? null : SafeDecrypt(r.ClientEmail),
        Status = r.Status,
        HasMarkdown = !string.IsNullOrEmpty(r.Markdown),
        PdfRequested = r.PdfRequested,
        CreatedAt = r.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
        ApprovedAt = r.ApprovedAt?.ToString("yyyy-MM-dd HH:mm"),
        Attempts = r.Attempts,
        LastError = r.LastError,
        // Registered-account context (decrypted) — null for guests.
        IsRegistered = account is not null,
        AccountEmail = account?.Email,
        AccountUsername = account?.Username,
        Gender = account?.Gender,
        Dob = account is null ? null : SafeDecryptOrNull(account.Dob),
        BirthTime = account is null ? null : SafeDecryptOrNull(account.BirthTime),
        LocationName = account is null ? null : SafeDecryptOrNull(account.LocationName),
        Latitude = account?.Latitude,
        Longitude = account?.Longitude,
        Timezone = account?.Timezone,
    };

    private string? SafeDecryptOrNull(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try { return FieldCrypto.Decrypt(cipher, _encKey); } catch { return null; }
    }

    /// <summary>Admin approves a request — THIS is the only path that triggers the AI.
    /// <para>
    /// Approval no longer generates inline. It validates the stored payload, marks the
    /// row Queued and hands it to the background worker, so the Sayar gets an immediate
    /// response instead of holding a request open for up to 60s, and a provider blip
    /// retries in the background rather than failing the approval outright.
    /// </para></summary>
    [HttpPost("admin/reading-requests/{id:int}/approve")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> ApproveReading(int id, CancellationToken ct)
    {
        var row = await _db.ReadingRequests.FindAsync(new object?[] { id }, ct);
        if (row is null) return NotFound(ApiResponse<object>.Fail("Request not found.", 404));
        if (row.Status == "Approved" && !string.IsNullOrEmpty(row.Markdown))
            return Ok(ApiResponse<object>.Ok(new { row.Id, row.Status }, "Already approved."));
        if (row.Status is "Queued" or "Processing")
            return Ok(ApiResponse<object>.Ok(new { row.Id, row.Status }, "Already generating."));

        // Fail fast on an unreadable payload — no point queueing work that cannot run.
        AiReadingRequestDto? payload;
        try { payload = JsonSerializer.Deserialize<AiReadingRequestDto>(FieldCrypto.Decrypt(row.PayloadJson, _encKey)); }
        catch { return StatusCode(500, ApiResponse<object>.Fail("Could not read the stored chart payload.", 500)); }
        if (payload is null) return StatusCode(500, ApiResponse<object>.Fail("Empty chart payload.", 500));

        row.Status = "Queued";
        row.Attempts = 0;
        row.LastError = null;
        await _db.SaveChangesAsync(ct);

        if (!_jobs.TryEnqueue(row.Id))
        {
            row.Status = "Pending";   // back-pressure: leave it reviewable rather than lost
            await _db.SaveChangesAsync(ct);
            return StatusCode(503, ApiResponse<object>.Fail(
                "The reading queue is saturated. Try again shortly.", 503));
        }

        return Accepted(ApiResponse<object>.Ok(
            new { row.Id, row.Status, queueDepth = _jobs.Depth },
            "Approved — the reading is generating in the background."));
    }

    /// <summary>Admin-only: how many readings are waiting on the worker.</summary>
    [HttpGet("admin/reading-queue")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> ReadingQueueDepth(CancellationToken ct)
    {
        var inFlight = await _db.ReadingRequests
            .CountAsync(r => r.Status == "Queued" || r.Status == "Processing", ct);
        var failed = await _db.ReadingRequests.CountAsync(r => r.Status == "Failed", ct);
        return Ok(ApiResponse<object>.Ok(new { queued = _jobs.Depth, inFlight, failed }));
    }

    /// <summary>Admin rejects a request (no AI call).</summary>
    [HttpPost("admin/reading-requests/{id:int}/reject")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> RejectReading(int id, CancellationToken ct)
    {
        var row = await _db.ReadingRequests.FindAsync(new object?[] { id }, ct);
        if (row is null) return NotFound(ApiResponse<object>.Fail("Request not found.", 404));
        row.Status = "Rejected";
        await _db.SaveChangesAsync(ct);
        return Ok(ApiResponse<object>.Ok(new { row.Id, row.Status }, "Rejected."));
    }

    // ── workflow helpers ─────────────────────────────────────────────────────────
    private ReadingStatusView ToStatusView(ReadingRequest r) => new()
    {
        Status = r.Status,
        RequestId = r.Id,
        Markdown = r.Status == "Approved" && !string.IsNullOrEmpty(r.Markdown) ? SafeDecrypt(r.Markdown!) : null,
        Model = r.Model,
        PdfRequested = r.PdfRequested,
        CreatedAt = r.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
        ApprovedAt = r.ApprovedAt?.ToString("yyyy-MM-dd HH:mm"),
    };

    private string SafeDecrypt(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return string.Empty;
        try { return FieldCrypto.Decrypt(cipher, _encKey); } catch { return "[decrypt-error]"; }
    }

    // SHA-256 of the querent's identity — the 30-day de-dup / rate-limit key.
    private static string QuerentHash(string? name, string? dob, string? time, string? loc)
    {
        var basis = $"{name?.Trim().ToLowerInvariant()}|{dob?.Trim()}|{time?.Trim()}|{loc?.Trim().ToLowerInvariant()}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(basis));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NotifyAdminEmail(string detail, string kind)
    {
        var title = kind == "pdf" ? "PDF ဟောစာတမ်း တောင်းဆိုမှု" : "ဟောစာတမ်း တောင်းဆိုမှုအသစ်";
        return $$"""
<div style="font-family:sans-serif;max-width:520px;margin:auto;padding:24px;border:1px solid #eee;border-radius:12px">
  <h2 style="color:#7c3aed;margin:0 0 8px">{{title}}</h2>
  <p style="color:#333;line-height:1.7">{{System.Net.WebUtility.HtmlEncode(detail)}}</p>
  <p style="color:#666;font-size:13px">Vedin admin panel မှတစ်ဆင့် ဝင်ရောက် စစ်ဆေးပြီး Approve လုပ်ပေးပါ။</p>
</div>
""";
    }

    // Reads the customer id from a customer JWT, if one was supplied. Returns false
    // for anonymous callers or admin tokens (this endpoint is not [Authorize]d).
    private bool TryCustomerId(out int id)
    {
        id = 0;
        if (User.FindFirst("ctype")?.Value != "customer") return false;
        return int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out id);
    }
}
