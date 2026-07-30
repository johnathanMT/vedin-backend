using System.ComponentModel.DataAnnotations;

namespace PortfolioApi.DTOs.Auth;

/// <summary>Request a password-reset email. Response is ALWAYS a generic success
/// (anti-enumeration) — it never reveals whether the email exists.</summary>
public class ForgotPasswordDto
{
    [Required, EmailAddress, StringLength(200)]
    public string Email { get; set; } = string.Empty;
}

/// <summary>Confirm an email from the frontend /confirm route (Task 2).</summary>
public class ConfirmEmailDto
{
    [Required, StringLength(200)]
    public string Token { get; set; } = string.Empty;
}

/// <summary>Complete a password reset with the token from the emailed link.</summary>
public class ResetPasswordDto
{
    [Required, StringLength(200)]
    public string Token { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 8)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    public string ConfirmPassword { get; set; } = string.Empty;
}
