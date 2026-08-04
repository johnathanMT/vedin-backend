namespace PortfolioApi.Models;

/// <summary>
/// A premium "manual approval" reading request. The querent submits their computed
/// chart snapshot; NO AI call happens at request time (protects the API budget).
/// The Sayar (admin) reviews Pending requests and approves — only then is the
/// reading generated and stored. QuerentName / PayloadJson / Markdown are AES-GCM
/// encrypted at rest because they carry personal chart details.
/// </summary>
public class ReadingRequest
{
    public int Id { get; set; }

    /// <summary>Set when the requester is a signed-in customer (else null / anonymous).</summary>
    public int? CustomerId { get; set; }

    /// <summary>SHA-256 of (Name | BirthDate | BirthTime | Location) — the 30-day
    /// rate-limit / de-duplication key. Not reversible, so it stores no raw PII.</summary>
    public string QuerentHash { get; set; } = string.Empty;

    public string QuerentName { get; set; } = string.Empty;   // encrypted (admin display)
    public string PayloadJson { get; set; } = string.Empty;   // encrypted AiReadingRequestDto snapshot

    /// <summary>Pending | Queued | Processing | Approved | Rejected | Failed.
    /// Approval enqueues rather than generating inline, so a request passes through
    /// Queued/Processing before it reaches Approved.</summary>
    public string Status { get; set; } = "Pending";

    public string? Markdown { get; set; }   // encrypted, filled once generation succeeds
    public string? Model { get; set; }

    /// <summary>Generation attempts so far — the worker retries a transient provider
    /// failure a few times before parking the row as Failed.</summary>
    public int Attempts { get; set; }

    /// <summary>Why the last generation attempt failed, shown to the Sayar so a stuck
    /// reading is diagnosable without reading server logs.</summary>
    public string? LastError { get; set; }

    /// <summary>The rendered premium report, built in the same background job as the
    /// reading so the querent's download is a byte-stream rather than a render.</summary>
    public byte[]? PdfDocument { get; set; }
    public DateTime? PdfGeneratedAt { get; set; }

    /// <summary>The querent has asked for the finished reading as a PDF by email.</summary>
    public bool PdfRequested { get; set; }
    /// <summary>Set once the Sayar has manually emailed the PDF (clears the queue).</summary>
    public bool PdfSent { get; set; }
    /// <summary>Email the querent wants the PDF sent to (encrypted at rest).</summary>
    public string? ClientEmail { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAt { get; set; }
}
