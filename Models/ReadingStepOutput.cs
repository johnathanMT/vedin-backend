using System.ComponentModel.DataAnnotations;

namespace PortfolioApi.Models;

/// <summary>
/// One completed stage of a reading's generation pipeline.
/// <para>
/// The seven life-area drafts are the expensive part of a reading. Without this, a
/// provider timeout during synthesis threw them away and the retry paid for all of them
/// again; with it, a retry resumes from the last completed step.
/// </para>
/// <para>
/// Content is encrypted at rest like the finished reading — an intermediate draft is
/// just as personal as the final document.
/// </para>
/// </summary>
public class ReadingStepOutput
{
    public int Id { get; set; }

    public int ReadingRequestId { get; set; }

    /// <summary>Matches <c>IReadingStep.Id</c>.</summary>
    [MaxLength(40)]
    public string StepId { get; set; } = string.Empty;

    /// <summary>The step's output, encrypted with the astrology field key.</summary>
    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
