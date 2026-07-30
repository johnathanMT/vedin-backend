using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using PortfolioApi.Common;
using PortfolioApi.DTOs.Auth;
using PortfolioApi.Interfaces;
using PortfolioApi.Models;

namespace PortfolioApi.Services;

public class AuthService : IAuthService
{
    private readonly IUserRepository _userRepo;
    private readonly IConfiguration  _config;
    private readonly ILogger<AuthService> _logger;
    private readonly IEmailService _email;

    public AuthService(
        IUserRepository   userRepo,
        IConfiguration    config,
        ILogger<AuthService> logger,
        IEmailService     email)
    {
        _userRepo = userRepo;
        _config   = config;
        _logger   = logger;
        _email    = email;
    }

    // ──────────────────────────────────────────────────────────
    public async Task<ApiResponse<AuthResponseDto>> RegisterAsync(RegisterDto dto)
    {
        // 1. Duplicate checks
        if (await _userRepo.EmailExistsAsync(dto.Email))
            return ApiResponse<AuthResponseDto>.Fail("Email is already registered.", 409);

        if (await _userRepo.UsernameExistsAsync(dto.Username))
            return ApiResponse<AuthResponseDto>.Fail("Username is already taken.", 409);

        // 2. Determine role via admin secret
        var adminSecret = _config["AdminSecret"];
        var role        = (!string.IsNullOrWhiteSpace(dto.AdminSecret) &&
                           dto.AdminSecret == adminSecret)
                          ? "Admin"
                          : "Guest";

        // 3. Hash password — BCrypt with cost factor 12
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password, workFactor: 12);

        // 4. Persist
        var user = new User
        {
            Username     = dto.Username.Trim(),
            Email        = dto.Email.ToLower().Trim(),
            PasswordHash = passwordHash,
            Role         = role,
        };

        await _userRepo.CreateAsync(user);
        _logger.LogInformation("New user registered: {Email} as {Role}", user.Email, role);

        // 5. Issue token immediately so the user doesn't need to log in again
        var token = GenerateJwtToken(user);
        return ApiResponse<AuthResponseDto>.Created(BuildResponse(user, token), "Registration successful.");
    }

    // ──────────────────────────────────────────────────────────
    public async Task<ApiResponse<AuthResponseDto>> LoginAsync(LoginDto dto)
    {
        var user = await _userRepo.GetByEmailAsync(dto.Email);

        // Use constant-time compare to resist timing attacks
        if (user is null || !BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash))
        {
            _logger.LogWarning("Failed login attempt for email: {Email}", dto.Email);
            return ApiResponse<AuthResponseDto>.Fail("Invalid email or password.", 401);
        }

        var token = GenerateJwtToken(user);
        _logger.LogInformation("User logged in: {Email}", user.Email);
        return ApiResponse<AuthResponseDto>.Ok(BuildResponse(user, token), "Login successful.");
    }

    // ──────────────────────────────────────────────────────────
    /// <summary>
    /// Change the caller's password. Requires the current password (verified with
    /// BCrypt) and a new one that differs from it. The identity comes from the JWT
    /// (userId), never from the request body, so a caller can only change their own.
    /// </summary>
    public async Task<ApiResponse> ChangePasswordAsync(int userId, ChangePasswordDto dto)
    {
        var user = await _userRepo.GetByIdAsync(userId);
        if (user is null)
            return ApiResponse.Fail("User not found.", 404);

        if (!BCrypt.Net.BCrypt.Verify(dto.CurrentPassword, user.PasswordHash))
        {
            _logger.LogWarning("Change-password rejected (wrong current password) for {Email}", user.Email);
            return ApiResponse.Fail("Current password is incorrect.", 401);
        }

        // Defence-in-depth: the validator also blocks this, but never let the
        // new password equal the old one even if validation is bypassed.
        if (BCrypt.Net.BCrypt.Verify(dto.NewPassword, user.PasswordHash))
            return ApiResponse.Fail("New password must be different from the current one.", 400);

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword, workFactor: 12);
        await _userRepo.UpdateAsync(user);
        _logger.LogInformation("Password changed for {Email}", user.Email);
        return ApiResponse.OkNoData("Password updated successfully.");
    }

    // ──────────────────────────────────────────────────────────
    /// <summary>Admin forgot-password. ALWAYS returns a generic success (anti-enum);
    /// only actually emails a 15-minute reset link when the email maps to a user.</summary>
    public async Task<ApiResponse> ForgotPasswordAsync(ForgotPasswordDto dto)
    {
        var email = (dto.Email ?? string.Empty).ToLower().Trim();
        if (email.Length is > 3 and < 200 && email.Contains('@'))
        {
            var user = await _userRepo.GetByEmailAsync(email);
            if (user is not null)
            {
                var raw = NewSecret();
                user.ResetTokenHash = Sha256(raw);
                user.ResetTokenExpiry = DateTime.UtcNow.AddMinutes(15);
                await _userRepo.UpdateAsync(user);
                var link = $"{FrontendUrl()}/reset-password?scope=admin&token={raw}";
                try { await _email.SendAsync(email, "Vedin Admin — Password reset", ResetEmailHtml(link)); }
                catch (System.Exception ex) { _logger.LogError(ex, "Admin reset email failed for {Email}", email); }
            }
        }
        return ApiResponse.OkNoData("If an account exists for that email, a password-reset link has been sent.");
    }

    /// <summary>Admin reset-password. Verifies the (hashed) token + 15-min expiry,
    /// then stores a new BCrypt hash and invalidates the token.</summary>
    public async Task<ApiResponse> ResetPasswordAsync(ResetPasswordDto dto)
    {
        if (dto.NewPassword != dto.ConfirmPassword)
            return ApiResponse.Fail("Passwords do not match.", 400);

        var user = await _userRepo.GetByResetTokenHashAsync(Sha256(dto.Token.Trim()));
        if (user is null || user.ResetTokenExpiry is null || user.ResetTokenExpiry < DateTime.UtcNow)
            return ApiResponse.Fail("This reset link is invalid or has expired. Please request a new one.", 400);

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword, workFactor: 12);
        user.ResetTokenHash = null;
        user.ResetTokenExpiry = null;
        await _userRepo.UpdateAsync(user);
        _logger.LogInformation("Admin password reset for {Email}", user.Email);
        return ApiResponse.OkNoData("Your password has been reset. You can now sign in.");
    }

    private static string NewSecret() => System.Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Sha256(string s) => System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private string FrontendUrl() => (_config["Frontend:Url"] ?? "https://vedin.myothant.dev").TrimEnd('/');
    private static string ResetEmailHtml(string link) =>
        """
<!doctype html><html><body style="margin:0;background:#0b0a14;font-family:Segoe UI,Arial,sans-serif">
  <div style="max-width:560px;margin:0 auto;padding:32px 20px">
    <div style="background:linear-gradient(135deg,#14121f,#1b1830);border:1px solid rgba(168,85,247,.35);border-radius:18px;padding:34px 28px">
      <div style="font:600 12px 'Segoe UI';letter-spacing:.3em;text-transform:uppercase;color:#eab308;margin-bottom:14px">Vedin &middot; Admin</div>
      <h1 style="margin:0 0 8px;font-size:22px;color:#f2ede0">Reset your admin password</h1>
      <p style="margin:0 0 22px;color:#b9b09b;font-size:14px;line-height:1.8">A password reset was requested for your Vedin admin account. Click below to set a new password.</p>
      <a href="{{link}}" style="display:inline-block;background:linear-gradient(135deg,#a855f7,#eab308);color:#14110d;font-weight:700;text-decoration:none;padding:14px 26px;border-radius:12px;font-size:15px">Reset password</a>
      <p style="margin:22px 0 0;color:#726a5c;font-size:12px;line-height:1.8">This link expires in 15 minutes. If you didn't request this, ignore this email.</p>
    </div>
  </div>
</body></html>
""".Replace("{{link}}", link);

    // ──────────────────────────────────────────────────────────
    private string GenerateJwtToken(User user)
    {
        var jwtKey     = _config["Jwt:Key"]
                         ?? throw new InvalidOperationException("JWT Key not configured.");
        var issuer     = _config["Jwt:Issuer"]    ?? "PortfolioApi";
        var audience   = _config["Jwt:Audience"]  ?? "PortfolioApiUsers";
        var expHours   = int.Parse(_config["Jwt:ExpirationHours"] ?? "24");

        var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email,          user.Email),
            new(ClaimTypes.Name,           user.Username),
            new(ClaimTypes.Role,           user.Role),
            // Standard JWT claims
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer:             issuer,
            audience:           audience,
            claims:             claims,
            notBefore:          DateTime.UtcNow,
            expires:            DateTime.UtcNow.AddHours(expHours),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static AuthResponseDto BuildResponse(User user, string token)
    {
        var handler   = new JwtSecurityTokenHandler();
        var parsedJwt = handler.ReadJwtToken(token);

        return new AuthResponseDto
        {
            Id        = user.Id,
            Token     = token,
            Username  = user.Username,
            Email     = user.Email,
            Role      = user.Role,
            ExpiresAt = parsedJwt.ValidTo,
        };
    }
}
