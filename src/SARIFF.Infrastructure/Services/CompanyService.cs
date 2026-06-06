using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SARIFF.Core.DTOs;
using SARIFF.Core.Entities;
using SARIFF.Core.Enums;
using SARIFF.Core.Interfaces;
using SARIFF.Infrastructure.Data;

namespace SARIFF.Infrastructure.Services;

public class CompanyService : ICompanyService
{
    private readonly AppDbContext _context;
    private readonly INotificationService _notificationService;
    private readonly ISmsService _smsService;
    private readonly IConfiguration _config;
    private readonly ILogger<CompanyService> _logger;

    public CompanyService(AppDbContext context, INotificationService notificationService, ISmsService smsService, IConfiguration config, ILogger<CompanyService> logger)
    {
        _context = context;
        _notificationService = notificationService;
        _smsService = smsService;
        _config = config;
        _logger = logger;
    }

    public async Task<ApiResponse<CompanyResponseDto>> CreateAsync(CreateCompanyDto dto)
    {
        _logger.LogInformation("Creating company: {Name}, Phone: {Phone}", dto.Name, dto.WhatsAppNumber);
        
        // Input validation
        var validationError = ValidationHelper.FirstError(
            ValidationHelper.ValidateName(dto.Name, "Company name"),
            ValidationHelper.ValidateName(dto.OwnerName, "Owner name"),
            ValidationHelper.ValidatePhone(dto.WhatsAppNumber, "WhatsApp number"),
            ValidationHelper.ValidateEmail(dto.Email),
            ValidationHelper.ValidatePassword(dto.Password)
        );
        if (validationError != null)
            return new ApiResponse<CompanyResponseDto>(false, validationError, null);

        if (await _context.Companies.AnyAsync(c => c.WhatsAppNumber == dto.WhatsAppNumber))
            return new ApiResponse<CompanyResponseDto>(false, "WhatsApp number already registered", null);

        const int maxRetries = 3;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            // Generate or use provided code prefix (2-3 chars from company name)
            var codePrefix = !string.IsNullOrWhiteSpace(dto.CodePrefix) 
                ? dto.CodePrefix.Trim().ToUpper()
                : CodeGenerator.GenerateCodePrefix(dto.Name);
            
            // Ensure prefix is unique across companies
            if (await _context.Companies.AnyAsync(c => c.CodePrefix == codePrefix && !c.IsDeleted))
            {
                // Append a number to make it unique
                for (int i = 1; i <= 9; i++)
                {
                    var candidate = codePrefix.Length >= 3 ? codePrefix.Substring(0, 2) + i : codePrefix + i;
                    if (!await _context.Companies.AnyAsync(c => c.CodePrefix == candidate && !c.IsDeleted))
                    {
                        codePrefix = candidate;
                        break;
                    }
                }
            }

            var code = await CodeGenerator.GenerateCompanyCodeAsync(_context, codePrefix);
            _logger.LogInformation("Generated company code: {Code} (prefix: {Prefix})", code, codePrefix);

            var company = new Company
            {
                Code = code,
                CodePrefix = codePrefix,
                Name = dto.Name,
                OwnerName = dto.OwnerName,
                WhatsAppNumber = dto.WhatsAppNumber,
                Email = dto.Email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password),
                IsActive = true
            };

            _context.Companies.Add(company);

            // Create default cash accounts (KES and USD)
            var cashKes = new CashAccount { CompanyId = company.Id, Currency = Currency.KES, Code = "CASH-KES" };
            var cashUsd = new CashAccount { CompanyId = company.Id, Currency = Currency.USD, Code = "CASH-USD" };
            _context.CashAccounts.Add(cashKes);
            _context.CashAccounts.Add(cashUsd);

            try
            {
                await _context.SaveChangesAsync();
                
                _logger.LogInformation("Company created: {Code} - {Name}", company.Code, company.Name);

                // Send credentials via WhatsApp
                var websiteUrl = _config["Waha:WebsiteUrl"] ?? "https://app.sariff.com";
                await _notificationService.SendOfficeUserCredentialsAsync(
                    company.Id, dto.WhatsAppNumber, dto.Name, company.Code, dto.Password, websiteUrl);

                // Auto-send credentials via SMS
                try
                {
                    var smsMsg = $"Welcome to SARIFF! Your bureau account: Code: {company.Code}, Password: {dto.Password}. Login at the SARIFF app to manage your bureau.";
                    await _smsService.SendSmsAsync(dto.WhatsAppNumber, smsMsg);
                }
                catch { /* SMS failure should not block company creation */ }

                return new ApiResponse<CompanyResponseDto>(true, "Company created successfully", MapToResponse(company));
            }
            catch (DbUpdateException) when (attempt < maxRetries - 1)
            {
                _context.Entry(company).State = EntityState.Detached;
                _context.Entry(cashKes).State = EntityState.Detached;
                _context.Entry(cashUsd).State = EntityState.Detached;
                await Task.Delay(50 * (attempt + 1));
            }
        }
        return new ApiResponse<CompanyResponseDto>(false, "Failed to generate unique company code. Please try again.", null);
    }

    public async Task<ApiResponse<CompanyResponseDto>> GetByIdAsync(Guid id)
    {
        var company = await _context.Companies.FindAsync(id);
        if (company == null)
            return new ApiResponse<CompanyResponseDto>(false, "Company not found", null);

        return new ApiResponse<CompanyResponseDto>(true, "Success", MapToResponse(company));
    }

    public async Task<ApiResponse<PagedResult<CompanyResponseDto>>> GetAllAsync(int page, int pageSize, string? search = null)
    {
        var query = _context.Companies.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(c => c.Name.Contains(search) || 
                                    c.OwnerName.Contains(search) || 
                                    c.Code.Contains(search) ||
                                    c.WhatsAppNumber.Contains(search));
        }

        var totalCount = await query.CountAsync();
        var companies = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();  // FIX #22: Materialize first to avoid client-side evaluation
        
        var items = companies.Select(c => MapToResponse(c)).ToList();

        return new ApiResponse<PagedResult<CompanyResponseDto>>(true, "Success", 
            new PagedResult<CompanyResponseDto>(items, totalCount, page, pageSize));
    }

    public async Task<ApiResponse<CompanyResponseDto>> UpdateAsync(Guid id, UpdateCompanyDto dto)
    {
        var company = await _context.Companies.FindAsync(id);
        if (company == null)
            return new ApiResponse<CompanyResponseDto>(false, "Company not found", null);

        if (dto.Name != null)
        {
            var check = ValidationHelper.ValidateName(dto.Name, "Company name");
            if (!check.IsValid) return new ApiResponse<CompanyResponseDto>(false, check.Error!, null);
            company.Name = dto.Name;
        }
        if (dto.OwnerName != null)
        {
            var check = ValidationHelper.ValidateName(dto.OwnerName, "Owner name");
            if (!check.IsValid) return new ApiResponse<CompanyResponseDto>(false, check.Error!, null);
            company.OwnerName = dto.OwnerName;
        }
        if (dto.Email != null)
        {
            var check = ValidationHelper.ValidateEmail(dto.Email);
            if (!check.IsValid) return new ApiResponse<CompanyResponseDto>(false, check.Error!, null);
            company.Email = dto.Email;
        }
        if (dto.LogoUrl != null) company.LogoUrl = dto.LogoUrl;
        if (dto.TaxId != null) company.TaxId = dto.TaxId;
        if (dto.Website != null) company.Website = dto.Website;
        if (dto.Address != null) company.Address = dto.Address;

        await _context.SaveChangesAsync();

        return new ApiResponse<CompanyResponseDto>(true, "Company updated successfully", MapToResponse(company));
    }

    public async Task<ApiResponse<bool>> ActivateAsync(Guid id)
    {
        var company = await _context.Companies.FindAsync(id);
        if (company == null)
            return new ApiResponse<bool>(false, "Company not found", false);

        company.IsActive = true;
        company.LockedUntil = null;
        company.FailedLoginAttempts = 0;
        await _context.SaveChangesAsync();

        return new ApiResponse<bool>(true, "Company activated", true);
    }

    public async Task<ApiResponse<bool>> DeactivateAsync(Guid id)
    {
        var company = await _context.Companies.FindAsync(id);
        if (company == null)
            return new ApiResponse<bool>(false, "Company not found", false);

        company.IsActive = false;
        await _context.SaveChangesAsync();

        return new ApiResponse<bool>(true, "Company deactivated", true);
    }

    public async Task<ApiResponse<bool>> ResetPasswordAsync(Guid id, AdminResetPasswordDto dto)
    {
        var company = await _context.Companies.FindAsync(id);
        if (company == null)
            return new ApiResponse<bool>(false, "Company not found", false);

        if (string.IsNullOrEmpty(dto.NewPassword) || dto.NewPassword.Length < 6)
            return new ApiResponse<bool>(false, "Password must be at least 6 characters", false);

        company.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
        company.FailedLoginAttempts = 0;
        company.LockedUntil = null;
        await _context.SaveChangesAsync();

        return new ApiResponse<bool>(true, "Password reset successfully", true);
    }

    public async Task<ApiResponse<CompanySummaryDto>> GetSummaryAsync(Guid id)
    {
        var company = await _context.Companies.FindAsync(id);
        if (company == null)
            return new ApiResponse<CompanySummaryDto>(false, "Company not found", null);

        var totalClients = await _context.Users.CountAsync(u => u.CompanyId == id && u.Role == UserRole.Client);
        var totalTransactions = await _context.Transactions.CountAsync(t => t.CompanyId == id);
        var clientBalances = await _context.Users
            .Where(u => u.CompanyId == id && u.Role == UserRole.Client)
            .GroupBy(u => 1)
            .Select(g => new { TotalKES = g.Sum(u => u.BalanceKES), TotalUSD = g.Sum(u => u.BalanceUSD) })
            .FirstOrDefaultAsync();

        return new ApiResponse<CompanySummaryDto>(true, "Success", new CompanySummaryDto(
            company.Id,
            company.Code,  // <-- THIS WAS MISSING!
            company.Name,
            company.OwnerName,
            totalClients,
            totalTransactions,
            clientBalances?.TotalKES ?? 0,
            clientBalances?.TotalUSD ?? 0,
            company.IsActive
        ));
    }

    public async Task<ApiResponse<List<CompanySummaryDto>>> GetAllSummariesAsync()
    {
        var companies = await _context.Companies.ToListAsync();
        var companyIds = companies.Select(c => c.Id).ToList();

        // PERF: Batch all counts in 2 queries instead of 3 per company
        var clientCounts = await _context.Users
            .Where(u => companyIds.Contains(u.CompanyId ?? Guid.Empty) && u.Role == UserRole.Client && !u.IsDeleted)
            .GroupBy(u => u.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count(), TotalKES = g.Sum(u => u.BalanceKES), TotalUSD = g.Sum(u => u.BalanceUSD) })
            .ToListAsync();

        var txnCounts = await _context.Transactions
            .Where(t => companyIds.Contains(t.CompanyId) && !t.IsDeleted)
            .GroupBy(t => t.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToListAsync();

        var summaries = companies.Select(company =>
        {
            var clients = clientCounts.FirstOrDefault(c => c.CompanyId == company.Id);
            var txns = txnCounts.FirstOrDefault(t => t.CompanyId == company.Id);

            return new CompanySummaryDto(
                company.Id, company.Code, company.Name, company.OwnerName,
                clients?.Count ?? 0, txns?.Count ?? 0,
                clients?.TotalKES ?? 0, clients?.TotalUSD ?? 0,
                company.IsActive);
        }).ToList();

        return new ApiResponse<List<CompanySummaryDto>>(true, "Success", summaries);
    }

    private static CompanyResponseDto MapToResponse(Company c) => new(
        c.Id,
        c.Code,  // <-- THIS WAS MISSING!
        c.Name,
        c.OwnerName,
        c.WhatsAppNumber,
        c.Email,
        c.LogoUrl,
        c.TaxId,
        c.Website,
        c.Address,
        c.IsActive,
        c.CreatedAt,
        c.LastLoginAt
    );
}