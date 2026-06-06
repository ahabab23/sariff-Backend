using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SARIFF.Core.DTOs;
using SARIFF.Core.Entities;
using SARIFF.Core.Enums;
using SARIFF.Core.Interfaces;
using SARIFF.Infrastructure.Data;

namespace SARIFF.Infrastructure.Services;

public interface IBalanceAlertService
{
    Task<ApiResponse<List<BalanceAlertRuleDto>>> GetRulesAsync(Guid companyId, Guid? clientId = null);
    Task<ApiResponse<BalanceAlertRuleDto>> CreateRuleAsync(Guid companyId, Guid userId, CreateBalanceAlertDto dto);
    Task<ApiResponse<bool>> ToggleRuleAsync(Guid companyId, Guid ruleId);
    Task<ApiResponse<bool>> DeleteRuleAsync(Guid companyId, Guid ruleId);
    Task CheckAndNotifyAsync(Guid companyId, Guid clientId, string clientName, decimal balanceKES, decimal balanceUSD);
}

public class BalanceAlertService : IBalanceAlertService
{
    private readonly AppDbContext _context;
    private readonly INotificationService _notificationService;
    private readonly ILogger<BalanceAlertService> _logger;

    public BalanceAlertService(AppDbContext context, INotificationService notificationService, ILogger<BalanceAlertService> logger)
    {
        _context = context;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task<ApiResponse<List<BalanceAlertRuleDto>>> GetRulesAsync(Guid companyId, Guid? clientId = null)
    {
        var query = _context.BalanceAlertRules
            .Where(r => r.CompanyId == companyId && !r.IsDeleted);

        if (clientId.HasValue)
            query = query.Where(r => r.ClientId == clientId.Value || r.ClientId == null);
        
        var rules = await query.OrderByDescending(r => r.CreatedAt)
            .Select(r => new BalanceAlertRuleDto(
                r.Id, r.CompanyId, r.ClientId, r.ClientName,
                (int)r.Currency, (int)r.Direction, r.Threshold,
                r.IsActive, r.NotifyAllOfficeUsers, r.LastTriggeredAt, r.CreatedAt
            )).ToListAsync();

        return new ApiResponse<List<BalanceAlertRuleDto>>(true, "Alert rules", rules);
    }

    public async Task<ApiResponse<BalanceAlertRuleDto>> CreateRuleAsync(Guid companyId, Guid userId, CreateBalanceAlertDto dto)
    {
        string? clientName = null;
        if (dto.ClientId.HasValue)
        {
            var client = await _context.Users.FindAsync(dto.ClientId.Value);
            clientName = client?.FullName;
        }

        var rule = new BalanceAlertRule
        {
            CompanyId = companyId,
            ClientId = dto.ClientId,
            ClientName = clientName,
            Currency = (Currency)dto.Currency,
            Direction = (BalanceAlertDirection)dto.Direction,
            Threshold = dto.Threshold,
            NotifyAllOfficeUsers = dto.NotifyAllOfficeUsers,
            CreatedByUserId = userId,
        };

        _context.BalanceAlertRules.Add(rule);
        await _context.SaveChangesAsync();

        var result = new BalanceAlertRuleDto(
            rule.Id, rule.CompanyId, rule.ClientId, rule.ClientName,
            (int)rule.Currency, (int)rule.Direction, rule.Threshold,
            rule.IsActive, rule.NotifyAllOfficeUsers, null, rule.CreatedAt
        );

        _logger.LogInformation("Balance alert created: {Direction} {Currency} {Threshold} for {Client}",
            rule.Direction, rule.Currency, rule.Threshold, clientName ?? "ALL clients");

        return new ApiResponse<BalanceAlertRuleDto>(true, "Alert rule created", result);
    }

    public async Task<ApiResponse<bool>> ToggleRuleAsync(Guid companyId, Guid ruleId)
    {
        var rule = await _context.BalanceAlertRules
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.CompanyId == companyId && !r.IsDeleted);
        if (rule == null) return new ApiResponse<bool>(false, "Rule not found", false);
        
        rule.IsActive = !rule.IsActive;
        rule.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return new ApiResponse<bool>(true, rule.IsActive ? "Alert enabled" : "Alert disabled", true);
    }

    public async Task<ApiResponse<bool>> DeleteRuleAsync(Guid companyId, Guid ruleId)
    {
        var rule = await _context.BalanceAlertRules
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.CompanyId == companyId && !r.IsDeleted);
        if (rule == null) return new ApiResponse<bool>(false, "Rule not found", false);
        
        rule.IsDeleted = true;
        rule.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return new ApiResponse<bool>(true, "Alert deleted", true);
    }

    /// <summary>
    /// Check all matching rules after a transaction changes a client's balance.
    /// Called from TransactionService after SaveChanges.
    /// </summary>
    public async Task CheckAndNotifyAsync(Guid companyId, Guid clientId, string clientName, decimal balanceKES, decimal balanceUSD)
    {
        try
        {
            // Get all active rules for this company (global + per-client)
            var rules = await _context.BalanceAlertRules
                .Where(r => r.CompanyId == companyId && r.IsActive && !r.IsDeleted
                    && (r.ClientId == null || r.ClientId == clientId))
                .ToListAsync();

            if (!rules.Any()) return;

            var now = DateTime.UtcNow;

            foreach (var rule in rules)
            {
                // Skip if triggered within last 24 hours (prevent spam)
                if (rule.LastTriggeredAt.HasValue && (now - rule.LastTriggeredAt.Value).TotalHours < 24)
                    continue;

                var balance = rule.Currency == Currency.KES ? balanceKES : balanceUSD;
                var currencyLabel = rule.Currency == Currency.KES ? "KES" : "USD";
                bool triggered = false;

                if (rule.Direction == BalanceAlertDirection.Below && balance < rule.Threshold)
                    triggered = true;
                else if (rule.Direction == BalanceAlertDirection.Above && balance > rule.Threshold)
                    triggered = true;

                if (!triggered) continue;

                // Build notification
                var direction = rule.Direction == BalanceAlertDirection.Below ? "dropped below" : "exceeded";
                var title = $"⚠️ Balance Alert";
                var body = $"{clientName}'s {currencyLabel} balance {direction} {currencyLabel} {rule.Threshold:N2}. Current: {currencyLabel} {balance:N2}";

                _logger.LogWarning("[BALANCE ALERT] {Body}", body);

                // Get company name for the notification
                var companyName = await _context.Companies
                    .Where(c => c.Id == companyId)
                    .Select(c => c.Name)
                    .FirstOrDefaultAsync() ?? "SARIFF";

                // Notify the company (office user who created the rule, or all office staff)
                await _notificationService.NotifyCompanyAsync(companyId, $"🏦 {companyName}", body, new Dictionary<string, string>
                {
                    { "type", "balance_alert" },
                    { "clientId", clientId.ToString() },
                    { "clientName", clientName },
                    { "currency", currencyLabel },
                    { "balance", balance.ToString("F2") },
                    { "threshold", rule.Threshold.ToString("F2") },
                });

                // Create in-app alert for the office
                await _notificationService.CreateClientAlertAsync(
                    companyId, clientId, "warning", title, body);

                // Update last triggered
                rule.LastTriggeredAt = now;
            }

            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BALANCE ALERT] Failed to check/notify for client {ClientId}", clientId);
        }
    }
}