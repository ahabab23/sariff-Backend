
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using SARIFF.Core.DTOs;
using SARIFF.Core.Entities;
using SARIFF.Core.Enums;
using SARIFF.Core.Interfaces;
using SARIFF.Infrastructure.Data;

namespace SARIFF.Infrastructure.Services;

public class AuthService : IAuthService
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _config;
    private readonly INotificationService _notificationService;
    private readonly ISmsService _smsService;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        AppDbContext context, 
        IConfiguration config, 
        INotificationService notificationService,
        ISmsService smsService,
        ILogger<AuthService> logger)
    {
        _context = context;
        _config = config;
        _notificationService = notificationService;
        _smsService = smsService;
        _logger = logger;
    }

    /// <summary>
    /// UNIFIED LOGIN - One endpoint for SuperAdmin, OfficeUser, and Client
    /// - SuperAdmin/OfficeUser: Returns OTP requirement
    /// - Client: Returns tokens directly (no OTP)
    /// </summary>
    public async Task<ApiResponse<object>> UnifiedLoginAsync(UnifiedLoginDto request, string ipAddress, string? userAgent)
    {
        // Input validation
        if (string.IsNullOrWhiteSpace(request.Code))
            return new ApiResponse<object>(false, "Code is required", null);

        var phoneCheck = ValidationHelper.ValidatePhone(request.PhoneNumber, "Phone number");
        if (!phoneCheck.IsValid)
            return new ApiResponse<object>(false, phoneCheck.Error!, null);

        if (string.IsNullOrEmpty(request.Password))
            return new ApiResponse<object>(false, "Password is required", null);

        _logger.LogInformation("========================================");
        _logger.LogInformation("UNIFIED LOGIN ATTEMPT");
        _logger.LogInformation("Code: {Code}", request.Code);
        _logger.LogInformation("========================================");

        // Determine user type by code format
        // SA-2026-001         → SuperAdmin
        // FB-CL-2026-0001     → Client (contains -CL-)
        // FB-2026-0001        → OfficeUser (company prefix)
        // Legacy: CO-2026-0001 → OfficeUser, CL-2026-0001 → Client
        var code = request.Code?.Trim().ToUpper() ?? "";
        var codePrefix = code.Split('-').FirstOrDefault() ?? "";
        
        _logger.LogInformation("Code prefix: {Prefix}", codePrefix);

        if (codePrefix == "SA")
            return await LoginSuperAdminAsync(request, ipAddress, userAgent);
        
        if (code.Contains("-CL-") || codePrefix == "CL")
            return await LoginClientAsync(request, ipAddress, userAgent);
        
        // Everything else is an OfficeUser (CO- or company prefix like FB-, AFB-, etc.)
        return await LoginOfficeUserAsync(request, ipAddress, userAgent);
    }

    private async Task<ApiResponse<object>> LoginSuperAdminAsync(UnifiedLoginDto request, string ipAddress, string? userAgent)
    {
        _logger.LogInformation("Processing as SUPER ADMIN login");

        var superAdmin = await _context.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Code == request.Code && 
                                      u.WhatsAppNumber == request.PhoneNumber && 
                                      u.Role == UserRole.SuperAdmin &&
                                      !u.IsDeleted);

        if (superAdmin == null)
        {
            _logger.LogWarning("SuperAdmin not found: Code={Code}, Phone={Phone}", request.Code, request.PhoneNumber);
            await LogLoginAttempt(null, null, UserRole.SuperAdmin, ipAddress, userAgent, false, "User not found");
            return new ApiResponse<object>(false, "Invalid credentials", null);
        }

        _logger.LogInformation("Found SuperAdmin: {Name}", superAdmin.FullName);

        // Validate account status
        var validationResult = ValidateAccountStatus(superAdmin.IsActive, superAdmin.LockedUntil, superAdmin.PasswordHash, request.Code);
        if (validationResult != null) return validationResult;

        // Verify password
        if (!BCrypt.Net.BCrypt.Verify(request.Password, superAdmin.PasswordHash))
        {
            superAdmin.FailedLoginAttempts++;
            if (superAdmin.FailedLoginAttempts >= 5)
                superAdmin.LockedUntil = DateTime.UtcNow.AddMinutes(30);
            await _context.SaveChangesAsync();
            
            _logger.LogWarning("Invalid password for SuperAdmin {Code}", request.Code);
            await LogLoginAttempt(null, superAdmin.Id, UserRole.SuperAdmin, ipAddress, userAgent, false, "Invalid password");
            return new ApiResponse<object>(false, "Invalid credentials", null);
        }

        // === DEVICE TRUST CHECK — Skip OTP on trusted devices ===
        if (!string.IsNullOrEmpty(request.DeviceId))
        {
            var trustedDevice = await _context.TrustedDevices
                .FirstOrDefaultAsync(d => d.UserId == superAdmin.Id 
                    && d.DeviceId == request.DeviceId 
                    && !d.IsDeleted
                    && d.TrustedUntil > DateTime.UtcNow);

            if (trustedDevice != null)
            {
                trustedDevice.LastUsedAt = DateTime.UtcNow;
                superAdmin.LastLoginAt = DateTime.UtcNow;
                superAdmin.FailedLoginAttempts = 0;
                superAdmin.LockedUntil = null;

                var session = CreateSession(null, superAdmin.Id, UserRole.SuperAdmin, ipAddress, userAgent);
                _context.UserSessions.Add(session);
                await _context.SaveChangesAsync();

                await LogLoginAttempt(null, superAdmin.Id, UserRole.SuperAdmin, ipAddress, userAgent, true, "Trusted device");
                _logger.LogInformation("Trusted device login for SuperAdmin {Code}", request.Code);

                var token = GenerateJwtToken(null, superAdmin.Id, UserRole.SuperAdmin, superAdmin.FullName, superAdmin.Code);
                return new ApiResponse<object>(true, "Login successful", new TokenResponseDto(
                    token, session.RefreshToken, session.ExpiresAt,
                    UserRole.SuperAdmin, superAdmin.FullName, superAdmin.Code, null, superAdmin.FullName
                ));
            }
        }

        // Generate OTP and send via SMS
        var otp = await GenerateAndSaveOtpAsync(request.PhoneNumber, request.Code, UserRole.SuperAdmin);
        
        // Send OTP via Africa's Talking SMS
        var smsSent = await _smsService.SendOtpAsync(request.PhoneNumber, otp);
        _logger.LogInformation("OTP {Status} via SMS for SuperAdmin: {Code}", smsSent ? "sent" : "FAILED", request.Code);
        _logger.LogDebug("OTP generated for {Phone}", request.PhoneNumber);

        return new ApiResponse<object>(true, smsSent ? "OTP sent to your phone via SMS" : "OTP generated (SMS delivery pending)", new
        {
            requiresOtp = true,
            role = "SuperAdmin",
            // devOtp removed for security
        });
    }

    private async Task<ApiResponse<object>> LoginOfficeUserAsync(UnifiedLoginDto request, string ipAddress, string? userAgent)
    {
        _logger.LogInformation("Processing as OFFICE USER login");

        var company = await _context.Companies
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Code == request.Code && 
                                      c.WhatsAppNumber == request.PhoneNumber && 
                                      !c.IsDeleted);

        if (company == null)
        {
            _logger.LogWarning("Company not found: Code={Code}, Phone={Phone}", request.Code, request.PhoneNumber);
            await LogLoginAttempt(null, null, UserRole.OfficeUser, ipAddress, userAgent, false, "Company not found");
            return new ApiResponse<object>(false, "Invalid credentials", null);
        }

        _logger.LogInformation("Found Company: {Name}, Owner: {Owner}", company.Name, company.OwnerName);

        // Validate account status
        var validationResult = ValidateAccountStatus(company.IsActive, company.LockedUntil, company.PasswordHash, request.Code);
        if (validationResult != null) return validationResult;

        // Verify password
        if (!BCrypt.Net.BCrypt.Verify(request.Password, company.PasswordHash))
        {
            company.FailedLoginAttempts++;
            if (company.FailedLoginAttempts >= 5)
                company.LockedUntil = DateTime.UtcNow.AddMinutes(30);
            await _context.SaveChangesAsync();
            
            _logger.LogWarning("Invalid password for Company {Code}", request.Code);
            await LogLoginAttempt(company.Id, null, UserRole.OfficeUser, ipAddress, userAgent, false, "Invalid password");
            return new ApiResponse<object>(false, "Invalid credentials", null);
        }

        // === DEVICE TRUST CHECK — Skip OTP on trusted devices ===
        if (!string.IsNullOrEmpty(request.DeviceId))
        {
            var trustedDevice = await _context.TrustedDevices
                .FirstOrDefaultAsync(d => d.UserId == company.Id 
                    && d.DeviceId == request.DeviceId 
                    && !d.IsDeleted
                    && d.TrustedUntil > DateTime.UtcNow);

            if (trustedDevice != null)
            {
                trustedDevice.LastUsedAt = DateTime.UtcNow;
                company.LastLoginAt = DateTime.UtcNow;
                company.FailedLoginAttempts = 0;
                company.LockedUntil = null;

                var session = CreateSession(company.Id, company.Id, UserRole.OfficeUser, ipAddress, userAgent);
                _context.UserSessions.Add(session);
                await _context.SaveChangesAsync();

                await LogLoginAttempt(company.Id, company.Id, UserRole.OfficeUser, ipAddress, userAgent, true, "Trusted device");
                _logger.LogInformation("Trusted device login for OfficeUser {Code}", request.Code);

                var token = GenerateJwtToken(company.Id, company.Id, UserRole.OfficeUser, company.Name, company.Code);
                return new ApiResponse<object>(true, "Login successful", new TokenResponseDto(
                    token, session.RefreshToken, session.ExpiresAt,
                    UserRole.OfficeUser, company.Name, company.Code, company.Id, company.OwnerName
                ));
            }
        }

        // Generate OTP and send via SMS
        var otp = await GenerateAndSaveOtpAsync(request.PhoneNumber, request.Code, UserRole.OfficeUser);
        
        // Send OTP via Africa's Talking SMS
        var smsSent = await _smsService.SendOtpAsync(request.PhoneNumber, otp);
        _logger.LogInformation("OTP {Status} via SMS for OfficeUser: {Code}", smsSent ? "sent" : "FAILED", request.Code);
        _logger.LogDebug("OTP generated for {Phone}", request.PhoneNumber);

        return new ApiResponse<object>(true, smsSent ? "OTP sent to your phone via SMS" : "OTP generated (SMS delivery pending)", new
        {
            requiresOtp = true,
            role = "OfficeUser",
            // devOtp removed for security
        });
    }

    private async Task<ApiResponse<object>> LoginClientAsync(UnifiedLoginDto request, string ipAddress, string? userAgent)
    {
        _logger.LogInformation("Processing as CLIENT login (NO OTP)");

        var client = await _context.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Code == request.Code && 
                                      u.WhatsAppNumber == request.PhoneNumber && 
                                      u.Role == UserRole.Client &&
                                      u.ClientType == ClientType.Permanent &&
                                      !u.IsDeleted);

        if (client == null)
        {
            _logger.LogWarning("Client not found: Code={Code}, Phone={Phone}", request.Code, request.PhoneNumber);
            await LogLoginAttempt(null, null, UserRole.Client, ipAddress, userAgent, false, "Client not found");
            return new ApiResponse<object>(false, "Invalid credentials", null);
        }

        _logger.LogInformation("Found Client: {Name}", client.FullName);

        // Validate account status
        var validationResult = ValidateAccountStatus(client.IsActive, client.LockedUntil, client.PasswordHash, request.Code);
        if (validationResult != null) return validationResult;

        // Verify password
        if (!BCrypt.Net.BCrypt.Verify(request.Password, client.PasswordHash))
        {
            client.FailedLoginAttempts++;
            if (client.FailedLoginAttempts >= 5)
                client.LockedUntil = DateTime.UtcNow.AddMinutes(30);
            await _context.SaveChangesAsync();
            
            _logger.LogWarning("Invalid password for Client {Code}", request.Code);
            await LogLoginAttempt(client.CompanyId, client.Id, UserRole.Client, ipAddress, userAgent, false, "Invalid password");
            return new ApiResponse<object>(false, "Invalid credentials", null);
        }

        // CLIENT: No OTP - Issue tokens directly
        client.FailedLoginAttempts = 0;
        client.LockedUntil = null;
        client.LastLoginAt = DateTime.UtcNow;

        var session = CreateSession(client.CompanyId, client.Id, UserRole.Client, ipAddress, userAgent);
        _context.UserSessions.Add(session);
        await _context.SaveChangesAsync();

        await LogLoginAttempt(client.CompanyId, client.Id, UserRole.Client, ipAddress, userAgent, true, null);

        var token = GenerateJwtToken(client.CompanyId, client.Id, UserRole.Client, client.FullName, client.Code);

        _logger.LogInformation("========================================");
        _logger.LogInformation("CLIENT LOGIN SUCCESSFUL (NO OTP)");
        _logger.LogInformation("Client: {Name} ({Code})", client.FullName, client.Code);
        _logger.LogInformation("========================================");

        // CLIENT: OwnerName is null (not applicable)
        return new ApiResponse<object>(true, "Login successful", new TokenResponseDto(
            token,
            session.RefreshToken,
            session.ExpiresAt,
            UserRole.Client,
            client.FullName,
            client.Code,
            client.CompanyId,
            null  // OwnerName - not applicable for clients
        ));
    }

    private ApiResponse<object>? ValidateAccountStatus(bool isActive, DateTime? lockedUntil, string? passwordHash, string code)
    {
        if (!isActive)
        {
            _logger.LogWarning("Account {Code} is inactive", code);
            return new ApiResponse<object>(false, "Account is inactive", null);
        }

        if (lockedUntil.HasValue && lockedUntil > DateTime.UtcNow)
        {
            _logger.LogWarning("Account {Code} is locked until {Until}", code, lockedUntil);
            return new ApiResponse<object>(false, $"Account is locked. Try again after {lockedUntil:HH:mm}", null);
        }

        if (string.IsNullOrEmpty(passwordHash))
        {
            _logger.LogError("Account {Code} has no password configured", code);
            return new ApiResponse<object>(false, "Account not configured. Contact support.", null);
        }

        return null;
    }

    /// <summary>
    /// Verify OTP - Step 2 for SuperAdmin/OfficeUser
    /// </summary>
    public async Task<ApiResponse<TokenResponseDto>> VerifyOtpAsync(OtpVerifyWithCodeDto request, string ipAddress, string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
            return new ApiResponse<TokenResponseDto>(false, "User code is required", null);

        if (string.IsNullOrWhiteSpace(request.Otp) || request.Otp.Length != 6)
            return new ApiResponse<TokenResponseDto>(false, "OTP must be 6 digits", null);

        var phoneCheck = ValidationHelper.ValidatePhone(request.PhoneNumber, "Phone number");
        if (!phoneCheck.IsValid)
            return new ApiResponse<TokenResponseDto>(false, phoneCheck.Error!, null);

        _logger.LogInformation("OTP verification attempt for user: {Code}", request.Code);

        var otp = await _context.OtpCodes
            .Where(o => o.PhoneNumber == request.PhoneNumber && 
                       o.UserCode == request.Code &&
                       o.Code == request.Otp &&
                       !o.IsUsed &&
                       o.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync();

        if (otp == null)
        {
            _logger.LogWarning("Invalid or expired OTP for {Code}", request.Code);
            return new ApiResponse<TokenResponseDto>(false, "Invalid or expired OTP", null);
        }

        otp.IsUsed = true;

        string name, code;
        string? ownerName = null;  // NEW: For personalized greeting
        Guid? companyId = null;
        Guid? userId = null;

        if (otp.Role == UserRole.SuperAdmin)
        {
            var superAdmin = await _context.Users.FirstOrDefaultAsync(u => u.Code == request.Code && u.Role == UserRole.SuperAdmin);
            if (superAdmin == null)
                return new ApiResponse<TokenResponseDto>(false, "User not found", null);

            userId = superAdmin.Id;
            name = superAdmin.FullName;
            code = superAdmin.Code;
            ownerName = superAdmin.FullName;  // SuperAdmin: Use their own name
            superAdmin.LastLoginAt = DateTime.UtcNow;
            superAdmin.FailedLoginAttempts = 0;
            superAdmin.LockedUntil = null;
        }
        else // OfficeUser
        {
            var company = await _context.Companies.FirstOrDefaultAsync(c => c.Code == request.Code);
            if (company == null)
                return new ApiResponse<TokenResponseDto>(false, "Company not found", null);

            companyId = company.Id;
            userId = company.Id; // OfficeUser: UserId = CompanyId (no separate user row)
            name = company.Name;
            code = company.Code;
            ownerName = company.OwnerName;  // OFFICE USER: Use company owner name!
            company.LastLoginAt = DateTime.UtcNow;
            company.FailedLoginAttempts = 0;
            company.LockedUntil = null;
        }

        var session = CreateSession(companyId, userId, otp.Role, ipAddress, userAgent);
        _context.UserSessions.Add(session);
        await _context.SaveChangesAsync();

        await LogLoginAttempt(companyId, userId, otp.Role, ipAddress, userAgent, true, null);

        // === SAVE TRUSTED DEVICE — Skip OTP next time from this device ===
        if (!string.IsNullOrEmpty(request.DeviceId) && userId.HasValue)
        {
            var existingTrust = await _context.TrustedDevices
                .FirstOrDefaultAsync(d => d.UserId == userId.Value && d.DeviceId == request.DeviceId);
            
            if (existingTrust != null)
            {
                existingTrust.LastUsedAt = DateTime.UtcNow;
                existingTrust.TrustedUntil = DateTime.UtcNow.AddDays(90);
                existingTrust.DeviceName = request.DeviceName ?? existingTrust.DeviceName;
            }
            else
            {
                _context.TrustedDevices.Add(new TrustedDevice
                {
                    UserId = userId.Value,
                    DeviceId = request.DeviceId,
                    Platform = request.DeviceName?.Contains("android", StringComparison.OrdinalIgnoreCase) == true ? "android"
                             : request.DeviceName?.Contains("ios", StringComparison.OrdinalIgnoreCase) == true ? "ios" : "web",
                    DeviceName = request.DeviceName ?? "Unknown device",
                    TrustedUntil = DateTime.UtcNow.AddDays(90),
                });
            }
            await _context.SaveChangesAsync();
            _logger.LogInformation("Device trusted for {Name}: {DeviceId}", name, request.DeviceId);
        }

        var token = GenerateJwtToken(companyId, userId, otp.Role, name, code);

        _logger.LogInformation("========================================");
        _logger.LogInformation("LOGIN COMPLETE - TOKEN ISSUED");
        _logger.LogInformation("User: {Name} ({Code}), Role: {Role}, Owner: {Owner}", name, code, otp.Role, ownerName);
        _logger.LogInformation("========================================");

        return new ApiResponse<TokenResponseDto>(true, "Login successful", new TokenResponseDto(
            token, 
            session.RefreshToken, 
            session.ExpiresAt, 
            otp.Role, 
            name, 
            code, 
            companyId,
            ownerName  // NEW: Include owner name for greeting
        ));
    }

    public async Task<ApiResponse<TokenResponseDto>> RefreshTokenAsync(RefreshTokenDto request, string ipAddress)
    {
        var tokenHash = HashToken(request.RefreshToken);
        var session = await _context.UserSessions.FirstOrDefaultAsync(s =>
            s.RefreshTokenHash == tokenHash && !s.IsRevoked && s.ExpiresAt > DateTime.UtcNow);

        if (session == null)
            return new ApiResponse<TokenResponseDto>(false, "Invalid or expired refresh token", null);

        string name, code;
        string? ownerName = null;  // NEW
        Guid? companyId = session.CompanyId;

        if (session.UserRole == UserRole.SuperAdmin && session.UserId.HasValue)
        {
            var user = await _context.Users.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Id == session.UserId && !u.IsDeleted && u.IsActive);
            if (user == null)
                return new ApiResponse<TokenResponseDto>(false, "Account no longer active", null);
            name = user.FullName;
            code = user.Code;
            ownerName = user.FullName;
        }
        else if (session.UserRole == UserRole.OfficeUser && session.CompanyId.HasValue)
        {
            var company = await _context.Companies.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == session.CompanyId && !c.IsDeleted && c.IsActive);
            if (company == null)
                return new ApiResponse<TokenResponseDto>(false, "Company no longer active", null);
            name = company.Name;
            code = company.Code;
            ownerName = company.OwnerName;
        }
        else // Client
        {
            var client = await _context.Users.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Id == session.UserId && !u.IsDeleted && u.IsActive);
            if (client == null)
                return new ApiResponse<TokenResponseDto>(false, "Account no longer active", null);
            name = client.FullName;
            code = client.Code;
            companyId = client.CompanyId;
            ownerName = null;
        }

        session.IsRevoked = true;
        var newSession = CreateSession(session.CompanyId, session.UserId, session.UserRole, ipAddress, session.UserAgent);
        _context.UserSessions.Add(newSession);
        await _context.SaveChangesAsync();

        var token = GenerateJwtToken(session.CompanyId, session.UserId, session.UserRole, name, code);
        
        return new ApiResponse<TokenResponseDto>(true, "Token refreshed", new TokenResponseDto(
            token, 
            newSession.RefreshToken, 
            newSession.ExpiresAt, 
            session.UserRole, 
            name, 
            code, 
            companyId,
            ownerName  // NEW: Include owner name
        ));
    }

    public async Task<ApiResponse<bool>> LogoutAsync(Guid userId)
    {
        // Find all active (non-revoked) sessions for this user
        // For OfficeUser, userId equals companyId — check both fields
        var sessions = await _context.UserSessions
            .Where(s => !s.IsRevoked && (s.UserId == userId || s.CompanyId == userId))
            .ToListAsync();
        
        foreach (var session in sessions)
        {
            session.IsRevoked = true;
        }
        
        if (sessions.Any())
            await _context.SaveChangesAsync();
        
        return new ApiResponse<bool>(true, "Logged out successfully", true);
    }

    // ==================== HELPERS ====================

    private async Task<string> GenerateAndSaveOtpAsync(string phoneNumber, string userCode, UserRole role)
    {
        var oldOtps = await _context.OtpCodes.Where(o => o.PhoneNumber == phoneNumber && !o.IsUsed).ToListAsync();
        foreach (var o in oldOtps) o.IsUsed = true;

        var code = Random.Shared.Next(100000, 999999).ToString();
        _context.OtpCodes.Add(new OtpCode
        {
            PhoneNumber = phoneNumber,
            UserCode = userCode,
            Code = code,
            Role = role,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });
        await _context.SaveChangesAsync();
        
        await _notificationService.SendOtpAsync(phoneNumber, code);
        return code;
    }

    private string GenerateJwtToken(Guid? companyId, Guid? userId, UserRole role, string name, string code)
    {
        // var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:SecretKey"]!));
        var jwtKey = Environment.GetEnvironmentVariable("JWT_SECRET_KEY") ?? _config["Jwt:SecretKey"]!;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var claims = new List<Claim>
        {
            new(ClaimTypes.Role, role.ToString()),
            new(ClaimTypes.Name, name),
            new("role_enum", ((int)role).ToString()),
            new("code", code)
        };
        if (companyId.HasValue) claims.Add(new Claim("company_id", companyId.Value.ToString()));
        if (userId.HasValue) claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(int.Parse(_config["Jwt:ExpiryMinutes"] ?? "60")),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        );
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private UserSession CreateSession(Guid? companyId, Guid? userId, UserRole role, string ipAddress, string? userAgent)
    {
        var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        return new UserSession
        {
            CompanyId = companyId,
            UserId = userId,
            UserRole = role,
            RefreshToken = refreshToken,
            RefreshTokenHash = HashToken(refreshToken),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IpAddress = ipAddress,
            UserAgent = userAgent
        };
    }

    private string HashToken(string token)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(token)));
    }

    private async Task LogLoginAttempt(Guid? companyId, Guid? userId, UserRole role, string ipAddress, string? userAgent, bool success, string? failureReason)
    {
        _context.LoginHistories.Add(new LoginHistory
        {
            CompanyId = companyId,
            UserId = userId,
            UserRole = role,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            IsSuccessful = success,
            FailureReason = failureReason,
            LoginAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
    }
}