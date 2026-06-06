//
// using Microsoft.EntityFrameworkCore;
// using Microsoft.Extensions.Logging;
// using SARIFF.Core.DTOs;
// using SARIFF.Core.Entities;
// using SARIFF.Core.Enums;
// using SARIFF.Core.Interfaces;
// using SARIFF.Infrastructure.Data;
// using System.Diagnostics;
// using System.Text;
//
// namespace SARIFF.Infrastructure.Services;
//
// public class SuperAdminService : ISuperAdminService
// {
//     private readonly AppDbContext _context;
//     private readonly ILogger<SuperAdminService> _logger;
//     private static readonly DateTime _startTime = DateTime.UtcNow;
//
//     public SuperAdminService(AppDbContext context, ILogger<SuperAdminService> logger)
//     {
//         _context = context;
//         _logger = logger;
//     }
//
//     // ==================== DASHBOARD ====================
//
//     public async Task<ApiResponse<SuperAdminDashboardExtendedDto>> GetDashboardAsync()
//     {
//         try
//         {
//             var now = DateTime.UtcNow;
//             var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
//             var lastMonth = startOfMonth.AddMonths(-1);
//             var today = now.Date;
//
//             // PERF: Use DB-level aggregation instead of loading all entities into memory
//             
//             // Company stats — single query with conditional counts
//             var companies = await _context.Companies.ToListAsync(); // Companies table is small (< 100 rows typically)
//             var companyIds = companies.Select(c => c.Id).ToList();
//             var totalCompanies = companies.Count;
//             var activeCompanies = companies.Count(c => c.IsActive && c.SubscriptionStatus == SubscriptionStatus.Active);
//             var trialCompanies = companies.Count(c => c.SubscriptionStatus == SubscriptionStatus.Trial);
//             var expiredCompanies = companies.Count(c => c.SubscriptionStatus == SubscriptionStatus.Expired);
//             var suspendedCompanies = companies.Count(c => c.SubscriptionStatus == SubscriptionStatus.Suspended || !c.IsActive);
//
//             // User stats — COUNT in DB, not ToListAsync
//             var totalUsers = await _context.Users.CountAsync();
//             var totalClients = await _context.Users.CountAsync(u => u.Role == UserRole.Client);
//             var activeUsersToday = await _context.LoginHistories
//                 .Where(l => l.LoginAt >= today && l.IsSuccessful)
//                 .Select(l => l.UserId ?? l.CompanyId)
//                 .Distinct()
//                 .CountAsync();
//
//             // Transaction stats — COUNT and SUM in DB
//             var totalTransactionsCount = await _context.Transactions.CountAsync();
//             var monthlyTransactionsCount = await _context.Transactions.CountAsync(t => t.TransactionDate >= startOfMonth);
//             var totalVolume = await _context.Transactions.SumAsync(t => t.Amount);
//             var monthlyVolume = await _context.Transactions
//                 .Where(t => t.TransactionDate >= startOfMonth)
//                 .SumAsync(t => t.Amount);
//
//             // Revenue (from subscription payments)
//             var monthlyRevenue = companies.Where(c => c.IsActive).Sum(c => c.MonthlyFee);
//             var totalRevenue = companies.Sum(c => c.TotalPaid);
//             var pendingPayments = 0m; // Would come from SubscriptionPayments with Status = Pending
//
//             // Growth calculations (simplified - compare to last month)
//             var lastMonthCompanies = companies.Count(c => c.CreatedAt < startOfMonth);
//             var companiesGrowth = lastMonthCompanies > 0 ? ((totalCompanies - lastMonthCompanies) / (decimal)lastMonthCompanies) * 100 : 0;
//
//             // System health summary
//             var errorsLast24h = await _context.SystemLogs.CountAsync(l => l.Level == "Error" && l.CreatedAt >= now.AddHours(-24));
//             var securityAlerts = await _context.Set<SecurityAlert>().CountAsync(a => !a.IsResolved && !a.IsDeleted);
//
//             // Top companies by volume — DB-level aggregation
//             var topByVolume = await _context.Transactions
//                 .GroupBy(t => t.CompanyId)
//                 .Select(g => new { CompanyId = g.Key, Volume = g.Sum(t => t.Amount) })
//                 .OrderByDescending(x => x.Volume)
//                 .Take(5)
//                 .ToListAsync();
//
//             var topCompaniesByVolume = new List<CompanyRankDto>();
//             var rank = 1;
//             foreach (var item in topByVolume)
//             {
//                 var company = companies.FirstOrDefault(c => c.Id == item.CompanyId);
//                 if (company != null)
//                 {
//                     topCompaniesByVolume.Add(new CompanyRankDto(company.Id, company.Name, company.Code, item.Volume, rank++));
//                 }
//             }
//
//             // Top by transaction count — DB-level aggregation
//             var topByCount = await _context.Transactions
//                 .GroupBy(t => t.CompanyId)
//                 .Select(g => new { CompanyId = g.Key, Count = g.Count() })
//                 .OrderByDescending(x => x.Count)
//                 .Take(5)
//                 .ToListAsync();
//
//             var topCompaniesByTransactions = new List<CompanyRankDto>();
//             rank = 1;
//             foreach (var item in topByCount)
//             {
//                 var company = companies.FirstOrDefault(c => c.Id == item.CompanyId);
//                 if (company != null)
//                 {
//                     topCompaniesByTransactions.Add(new CompanyRankDto(company.Id, company.Name, company.Code, item.Count, rank++));
//                 }
//             }
//
//             // Recent signups
//             // Load users and transactions for per-company stats (MapToCompanyStats, companySummaries)
//             var users = await _context.Users.Where(u => companyIds.Contains(u.CompanyId ?? Guid.Empty)).ToListAsync();
//             var transactions = await _context.Transactions.Where(t => companyIds.Contains(t.CompanyId)).ToListAsync();
//
//             var recentSignups = companies
//                 .OrderByDescending(c => c.CreatedAt)
//                 .Take(5)
//                 .Select(c => MapToCompanyStats(c, users, transactions, now))
//                 .ToList();
//
//             // Expiring subscriptions (next 30 days)
//             var expiringSubscriptions = companies
//                 .Where(c => c.SubscriptionExpiresAt.HasValue && c.SubscriptionExpiresAt <= now.AddDays(30) && c.SubscriptionExpiresAt > now)
//                 .OrderBy(c => c.SubscriptionExpiresAt)
//                 .Take(5)
//                 .Select(c => MapToCompanyStats(c, users, transactions, now))
//                 .ToList();
//
//             // For backward compatibility - basic company summaries
//             var companySummaries = companies.Select(c => new CompanySummaryDto(
//                 c.Id, c.Code, c.Name, c.OwnerName,
//                 users.Count(u => u.CompanyId == c.Id && u.Role == UserRole.Client),
//                 transactions.Count(t => t.CompanyId == c.Id),
//                 0, 0, c.IsActive
//             )).ToList();
//
//             // Recent errors
//             var recentErrors = await _context.SystemLogs
//                 .Where(l => l.Level == "Error")
//                 .OrderByDescending(l => l.CreatedAt)
//                 .Take(10)
//                 .Select(e => new SystemLogResponseDto(e.Id, e.Level, e.Source, e.Message, e.CompanyId, e.CreatedAt))
//                 .ToListAsync();
//
//             var dashboard = new SuperAdminDashboardExtendedDto(
//                 TotalCompanies: totalCompanies,
//                 ActiveCompanies: activeCompanies,
//                 TrialCompanies: trialCompanies,
//                 ExpiredCompanies: expiredCompanies,
//                 SuspendedCompanies: suspendedCompanies,
//                 CompaniesGrowth: companiesGrowth,
//                 TotalUsers: totalUsers,
//                 TotalClients: totalClients,
//                 ActiveUsersToday: activeUsersToday,
//                 UsersGrowth: 0,
//                 MonthlyRecurringRevenue: monthlyRevenue,
//                 TotalRevenue: totalRevenue,
//                 PendingPayments: pendingPayments,
//                 RevenueGrowth: 0,
//                 TotalTransactionsVolume: totalVolume,
//                 MonthlyTransactionsVolume: monthlyVolume,
//                 TotalTransactionsCount: totalTransactionsCount,
//                 MonthlyTransactionsCount: monthlyTransactionsCount,
//                 VolumeGrowth: 0,
//                 SystemStatus: errorsLast24h > 10 ? "Degraded" : "Healthy",
//                 ErrorsLast24h: errorsLast24h,
//                 SecurityAlertsActive: securityAlerts,
//                 TopCompaniesByVolume: topCompaniesByVolume,
//                 TopCompaniesByTransactions: topCompaniesByTransactions,
//                 RecentSignups: recentSignups,
//                 ExpiringSubscriptions: expiringSubscriptions,
//                 RecentErrors: recentErrors,
//                 Companies: companySummaries
//             );
//
//             return new ApiResponse<SuperAdminDashboardExtendedDto>(true, "Dashboard loaded", dashboard);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error getting SuperAdmin dashboard");
//             return new ApiResponse<SuperAdminDashboardExtendedDto>(false, "Error loading dashboard", null!);
//         }
//     }
//
//     private CompanyStatsDto MapToCompanyStats(Company c, List<User> allUsers, List<Transaction> allTransactions, DateTime now)
//     {
//         var companyUsers = allUsers.Where(u => u.CompanyId == c.Id).ToList();
//         var companyTransactions = allTransactions.Where(t => t.CompanyId == c.Id).ToList();
//         var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
//
//         return new CompanyStatsDto(
//             c.Id, c.Code, c.Name, c.OwnerName, c.Email, c.WhatsAppNumber, c.IsActive, c.CreatedAt, c.LastLoginAt,
//             c.SubscriptionPlan, c.SubscriptionStatus, c.SubscriptionStartDate, c.SubscriptionExpiresAt,
//             c.MonthlyFee, c.TotalPaid, c.LastPaymentDate,
//             companyUsers.Count,
//             companyUsers.Count(u => u.IsActive),
//             companyUsers.Count(u => u.Role == UserRole.Client),
//             companyUsers.Count(u => u.Role == UserRole.Client && u.IsActive),
//             companyTransactions.Count,
//             companyTransactions.Count(t => t.TransactionDate >= startOfMonth),
//             companyTransactions.Sum(t => t.Amount),
//             companyTransactions.Where(t => t.TransactionDate >= startOfMonth).Sum(t => t.Amount),
//             companyTransactions.OrderByDescending(t => t.CreatedAt).FirstOrDefault()?.CreatedAt,
//             0 // Error count - would need to query SystemLogs
//         );
//     }
//
//     // ==================== COMPANIES ====================
//
//     public async Task<ApiResponse<PagedResult<CompanyStatsDto>>> GetAllCompaniesWithStatsAsync(
//         int page, int pageSize, string? search = null, string? status = null)
//     {
//         try
//         {
//             var query = _context.Companies.AsQueryable();
//
//             if (!string.IsNullOrWhiteSpace(search))
//             {
//                 search = search.ToLower();
//                 query = query.Where(c =>
//                     c.Name.ToLower().Contains(search) ||
//                     c.Code.ToLower().Contains(search) ||
//                     c.OwnerName.ToLower().Contains(search) ||
//                     (c.Email != null && c.Email.ToLower().Contains(search)));
//             }
//
//             if (!string.IsNullOrWhiteSpace(status))
//             {
//                 switch (status.ToLower())
//                 {
//                     case "active":
//                         query = query.Where(c => c.IsActive && c.SubscriptionStatus == SubscriptionStatus.Active);
//                         break;
//                     case "trial":
//                         query = query.Where(c => c.SubscriptionStatus == SubscriptionStatus.Trial);
//                         break;
//                     case "expired":
//                         query = query.Where(c => c.SubscriptionStatus == SubscriptionStatus.Expired);
//                         break;
//                     case "suspended":
//                         query = query.Where(c => !c.IsActive || c.SubscriptionStatus == SubscriptionStatus.Suspended);
//                         break;
//                 }
//             }
//
//             var totalCount = await query.CountAsync();
//             var companies = await query
//                 .OrderByDescending(c => c.CreatedAt)
//                 .Skip((page - 1) * pageSize)
//                 .Take(pageSize)
//                 .ToListAsync();
//
//             var now = DateTime.UtcNow;
//             var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
//             var companyIds = companies.Select(c => c.Id).ToList();
//
//             var users = await _context.Users.Where(u => companyIds.Contains(u.CompanyId ?? Guid.Empty)).ToListAsync();
//             var transactions = await _context.Transactions.Where(t => companyIds.Contains(t.CompanyId)).ToListAsync();
//
//             var companyStats = companies.Select(c => MapToCompanyStats(c, users, transactions, now)).ToList();
//
//             var result = new PagedResult<CompanyStatsDto>(companyStats, totalCount, page, pageSize);
//             return new ApiResponse<PagedResult<CompanyStatsDto>>(true, "Companies retrieved", result);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error getting companies");
//             return new ApiResponse<PagedResult<CompanyStatsDto>>(false, "Error retrieving companies", null!);
//         }
//     }
//
//     public async Task<ApiResponse<CompanyDetailDto>> GetCompanyDetailAsync(Guid companyId)
//     {
//         try
//         {
//             var company = await _context.Companies.FirstOrDefaultAsync(c => c.Id == companyId);
//             if (company == null)
//                 return new ApiResponse<CompanyDetailDto>(false, "Company not found", null!);
//
//             var now = DateTime.UtcNow;
//             var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
//
//             var users = await _context.Users.Where(u => u.CompanyId == companyId).ToListAsync();
//             var transactions = await _context.Transactions.Where(t => t.CompanyId == companyId).ToListAsync();
//             var bankAccounts = await _context.BankAccounts.Where(b => b.CompanyId == companyId).ToListAsync();
//             var mpesaAgents = await _context.MpesaAgents.Where(m => m.CompanyId == companyId).ToListAsync();
//             var cashAccounts = await _context.CashAccounts.Where(c => c.CompanyId == companyId).ToListAsync();
//
//             var loginHistory = await _context.LoginHistories
//                 .Where(l => l.CompanyId == companyId)
//                 .OrderByDescending(l => l.LoginAt)
//                 .Take(10)
//                 .Select(l => new AdminLoginHistoryDto(
//                     l.Id, l.CompanyId, company.Name, l.UserId, null, l.UserRole,
//                     l.IpAddress, l.Location, l.IsSuccessful, l.FailureReason, l.LoginAt
//                 ))
//                 .ToListAsync();
//
//             var recentTxns = transactions
//                 .OrderByDescending(t => t.TransactionDate)
//                 .Take(5)
//                 .Select(t => new RecentTransactionDto(
//                     t.Id, t.Code, t.Description, t.Amount, t.Currency.ToString(), t.TransactionType.ToString(), t.TransactionDate
//                 ))
//                 .ToList();
//
//             var unreconciledCount = transactions.Count(t => t.ReconciliationStatus == ReconciliationStatus.Pending);
//             var errorCount = await _context.SystemLogs.CountAsync(l => l.CompanyId == companyId && l.Level == "Error" && l.CreatedAt >= startOfMonth);
//
//             var detail = new CompanyDetailDto(
//                 company.Id, company.Code, company.Name, company.OwnerName, company.Email, company.WhatsAppNumber,
//                 company.LogoUrl, company.TaxId, company.Website, company.Address,
//                 company.IsActive, company.CreatedAt, company.LastLoginAt,
//                 company.SubscriptionPlan, company.SubscriptionStatus, company.SubscriptionStartDate, company.SubscriptionExpiresAt,
//                 company.MonthlyFee, company.TotalPaid, company.LastPaymentDate,
//                 users.Count, users.Count(u => u.IsActive),
//                 users.Count(u => u.Role == UserRole.Client), users.Count(u => u.Role == UserRole.Client && u.IsActive),
//                 transactions.Count, transactions.Count(t => t.TransactionDate >= startOfMonth),
//                 transactions.Sum(t => t.Amount), transactions.Where(t => t.TransactionDate >= startOfMonth).Sum(t => t.Amount),
//                 bankAccounts.Count, mpesaAgents.Count,
//                 cashAccounts.Where(c => c.Currency == Currency.KES).Sum(c => c.Balance),
//                 cashAccounts.Where(c => c.Currency == Currency.USD).Sum(c => c.Balance),
//                 bankAccounts.Where(b => b.Currency == Currency.KES).Sum(b => b.Balance),
//                 bankAccounts.Where(b => b.Currency == Currency.USD).Sum(b => b.Balance),
//                 mpesaAgents.Sum(m => m.Balance),
//                 users.Where(u => u.Role == UserRole.Client).Sum(u => u.BalanceKES),
//                 users.Where(u => u.Role == UserRole.Client).Sum(u => u.BalanceUSD),
//                 recentTxns, loginHistory, unreconciledCount, errorCount
//             );
//
//             return new ApiResponse<CompanyDetailDto>(true, "Company details retrieved", detail);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error getting company details for {CompanyId}", companyId);
//             return new ApiResponse<CompanyDetailDto>(false, "Error retrieving company details", null!);
//         }
//     }
//
//     public async Task<ApiResponse<bool>> UpdateSubscriptionAsync(Guid companyId, UpdateSubscriptionDto dto)
//     {
//         try
//         {
//             var company = await _context.Companies.FindAsync(companyId);
//             if (company == null)
//                 return new ApiResponse<bool>(false, "Company not found", false);
//
//             company.SubscriptionPlan = dto.Plan;
//             company.MonthlyFee = dto.MonthlyFee;
//             if (dto.ExpiresAt.HasValue)
//                 company.SubscriptionExpiresAt = dto.ExpiresAt;
//
//             company.SubscriptionStatus = dto.Status ?? SubscriptionStatus.Active;
//             await _context.SaveChangesAsync();
//
//             _logger.LogInformation("Subscription updated for company {CompanyId}: {Plan}", companyId, dto.Plan);
//             return new ApiResponse<bool>(true, "Subscription updated", true);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error updating subscription for {CompanyId}", companyId);
//             return new ApiResponse<bool>(false, "Error updating subscription", false);
//         }
//     }
//
//     public async Task<ApiResponse<bool>> SuspendCompanyAsync(Guid companyId, SuspendCompanyDto dto)
//     {
//         try
//         {
//             var company = await _context.Companies.FindAsync(companyId);
//             if (company == null)
//                 return new ApiResponse<bool>(false, "Company not found", false);
//
//             company.IsActive = false;
//             company.SubscriptionStatus = SubscriptionStatus.Suspended;
//             await _context.SaveChangesAsync();
//
//             // Log audit
//             _context.AuditLogs.Add(new AuditLog
//             {
//                 CompanyId = companyId,
//                 Action = AuditAction.Update,
//                 EntityType = "Company",
//                 EntityId = companyId,
//                 NewValues = $"Suspended: {dto.Reason}"
//             });
//             await _context.SaveChangesAsync();
//
//             _logger.LogWarning("Company {CompanyId} suspended: {Reason}", companyId, dto.Reason);
//             return new ApiResponse<bool>(true, "Company suspended", true);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error suspending company {CompanyId}", companyId);
//             return new ApiResponse<bool>(false, "Error suspending company", false);
//         }
//     }
//
//     public async Task<ApiResponse<bool>> ActivateCompanyAsync(Guid companyId)
//     {
//         try
//         {
//             var company = await _context.Companies.FindAsync(companyId);
//             if (company == null)
//                 return new ApiResponse<bool>(false, "Company not found", false);
//
//             company.IsActive = true;
//             company.SubscriptionStatus = SubscriptionStatus.Active;
//             company.FailedLoginAttempts = 0;
//             company.LockedUntil = null;
//             await _context.SaveChangesAsync();
//
//             return new ApiResponse<bool>(true, "Company activated", true);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error activating company {CompanyId}", companyId);
//             return new ApiResponse<bool>(false, "Error activating company", false);
//         }
//     }
//
//     /// <summary>
//     /// Reset password for an Office User (Company)
//     /// </summary>
//     public async Task<ApiResponse<bool>> ResetOfficeUserPasswordAsync(Guid companyId, AdminResetPasswordDto dto)
//     {
//         try
//         {
//             var pwdCheck = ValidationHelper.ValidatePassword(dto.NewPassword);
//             if (!pwdCheck.IsValid)
//                 return new ApiResponse<bool>(false, pwdCheck.Error!, false);
//
//             var company = await _context.Companies.FindAsync(companyId);
//             if (company == null)
//                 return new ApiResponse<bool>(false, "Company not found", false);
//
//             company.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
//             company.FailedLoginAttempts = 0;
//             company.LockedUntil = null;
//             await _context.SaveChangesAsync();
//
//             // Log audit
//             _context.AuditLogs.Add(new AuditLog
//             {
//                 CompanyId = companyId,
//                 Action = AuditAction.Update,
//                 EntityType = "Company",
//                 EntityId = companyId,
//                 NewValues = "Password reset by SuperAdmin"
//             });
//             await _context.SaveChangesAsync();
//
//             _logger.LogInformation("Password reset for company {CompanyId} by SuperAdmin", companyId);
//             return new ApiResponse<bool>(true, "Password reset successfully", true);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error resetting password for company {CompanyId}", companyId);
//             return new ApiResponse<bool>(false, "Error resetting password", false);
//         }
//     }
//
//     // ==================== SYSTEM HEALTH ====================
//
//     public async Task<ApiResponse<SystemHealthDto>> GetSystemHealthAsync()
//     {
//         try
//         {
//             var now = DateTime.UtcNow;
//             var last24h = now.AddHours(-24);
//
//             // Database health check
//             var dbStopwatch = Stopwatch.StartNew();
//             var canConnect = await _context.Database.CanConnectAsync();
//             dbStopwatch.Stop();
//
//             var dbStatus = canConnect ? "Healthy" : "Down";
//             var dbLatency = (int)dbStopwatch.ElapsedMilliseconds;
//
//             // Get log counts
//             var errorsLast24h = await _context.SystemLogs.CountAsync(l => l.CreatedAt >= last24h && l.Level == "Error");
//             var warningsLast24h = await _context.SystemLogs.CountAsync(l => l.CreatedAt >= last24h && l.Level == "Warning");
//             var criticalLast24h = await _context.SystemLogs.CountAsync(l => l.CreatedAt >= last24h && l.Level == "Critical");
//
//             var overallStatus = "Healthy";
//             if (!canConnect) overallStatus = "Down";
//             else if (errorsLast24h > 10 || dbLatency > 1000) overallStatus = "Degraded";
//
//             var uptimeSeconds = (long)(now - _startTime).TotalSeconds;
//
//             var health = new SystemHealthDto(
//                 overallStatus, "Healthy", 45, 120, errorsLast24h > 0 ? Math.Min((decimal)errorsLast24h / 100 * 100, 5) : 0,
//                 dbStatus, dbLatency, 10, 35, 60, 45, 25, uptimeSeconds, now,
//                 errorsLast24h, warningsLast24h, criticalLast24h
//             );
//
//             return new ApiResponse<SystemHealthDto>(true, "Health check complete", health);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error getting system health");
//             return new ApiResponse<SystemHealthDto>(false, "Health check failed",
//                 new SystemHealthDto("Down", "Down", 0, 0, 100, "Down", 0, 0, 0, 0, 0, 0, 0, DateTime.UtcNow, 0, 0, 0));
//         }
//     }
//
//     // ==================== SECURITY ====================
//
//     public async Task<ApiResponse<SecurityOverviewDto>> GetSecurityOverviewAsync()
//     {
//         try
//         {
//             var now = DateTime.UtcNow;
//             var last24h = now.AddHours(-24);
//             var last7d = now.AddDays(-7);
//
//             var failedLogins24h = await _context.LoginHistories.CountAsync(l => l.LoginAt >= last24h && !l.IsSuccessful);
//             var failedLogins7d = await _context.LoginHistories.CountAsync(l => l.LoginAt >= last7d && !l.IsSuccessful);
//
//             var lockedCompanies = await _context.Companies.CountAsync(c => c.LockedUntil != null && c.LockedUntil > now);
//             var lockedUsers = await _context.Users.CountAsync(u => u.LockedUntil != null && u.LockedUntil > now);
//
//             var activeAlerts = await _context.Set<SecurityAlert>().CountAsync(a => !a.IsResolved && !a.IsDeleted);
//             var blockedIPs = await _context.Set<BlockedIP>().CountAsync(b => b.IsActive && !b.IsDeleted);
//
//             var recentAlerts = await _context.Set<SecurityAlert>()
//                 .Where(a => !a.IsDeleted)
//                 .OrderByDescending(a => a.CreatedAt)
//                 .Take(10)
//                 .Select(a => new SecurityAlertDto(
//                     a.Id, a.AlertType, a.Severity, a.Message, a.CompanyId, null, a.IpAddress, a.IsResolved, a.CreatedAt, a.ResolvedAt
//                 ))
//                 .ToListAsync();
//
//             var recentFailedLogins = await _context.LoginHistories
//                 .Where(l => !l.IsSuccessful)
//                 .OrderByDescending(l => l.LoginAt)
//                 .Take(20)
//                 .Select(l => new AdminLoginHistoryDto(
//                     l.Id, l.CompanyId, null, l.UserId, null, l.UserRole, l.IpAddress, l.Location, l.IsSuccessful, l.FailureReason, l.LoginAt
//                 ))
//                 .ToListAsync();
//
//             var overview = new SecurityOverviewDto(
//                 failedLogins24h, failedLogins7d, lockedCompanies + lockedUsers, 0, blockedIPs, activeAlerts, recentAlerts, recentFailedLogins
//             );
//
//             return new ApiResponse<SecurityOverviewDto>(true, "Security overview retrieved", overview);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error getting security overview");
//             return new ApiResponse<SecurityOverviewDto>(false, "Error retrieving security overview", null!);
//         }
//     }
//
//     public async Task<ApiResponse<PagedResult<SecurityAlertDto>>> GetSecurityAlertsAsync(int page, int pageSize, bool? resolved = null)
//     {
//         try
//         {
//             var query = _context.Set<SecurityAlert>().Where(a => !a.IsDeleted);
//             if (resolved.HasValue)
//                 query = query.Where(a => a.IsResolved == resolved.Value);
//
//             var totalCount = await query.CountAsync();
//             var alerts = await query
//                 .OrderByDescending(a => a.CreatedAt)
//                 .Skip((page - 1) * pageSize)
//                 .Take(pageSize)
//                 .Select(a => new SecurityAlertDto(
//                     a.Id, a.AlertType, a.Severity, a.Message, a.CompanyId, null, a.IpAddress, a.IsResolved, a.CreatedAt, a.ResolvedAt
//                 ))
//                 .ToListAsync();
//
//             return new ApiResponse<PagedResult<SecurityAlertDto>>(true, "Alerts retrieved",
//                 new PagedResult<SecurityAlertDto>(alerts, totalCount, page, pageSize));
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error getting security alerts");
//             return new ApiResponse<PagedResult<SecurityAlertDto>>(false, "Error retrieving alerts", null!);
//         }
//     }
//
//     public async Task<ApiResponse<bool>> ResolveSecurityAlertAsync(Guid alertId, ResolveAlertDto dto)
//     {
//         try
//         {
//             var alert = await _context.Set<SecurityAlert>().FindAsync(alertId);
//             if (alert == null)
//                 return new ApiResponse<bool>(false, "Alert not found", false);
//
//             alert.IsResolved = true;
//             alert.ResolvedAt = DateTime.UtcNow;
//             alert.ResolutionNotes = dto.Notes;
//             await _context.SaveChangesAsync();
//
//             return new ApiResponse<bool>(true, "Alert resolved", true);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error resolving alert {AlertId}", alertId);
//             return new ApiResponse<bool>(false, "Error resolving alert", false);
//         }
//     }
//
//     public async Task<ApiResponse<bool>> BlockIPAsync(BlockIPDto dto)
//     {
//         try
//         {
//             var blockedIP = new BlockedIP
//             {
//                 IpAddress = dto.IpAddress,
//                 Reason = dto.Reason,
//                 BlockedUntil = dto.BlockUntil,
//                 IsActive = true
//             };
//
//             _context.Set<BlockedIP>().Add(blockedIP);
//             await _context.SaveChangesAsync();
//
//             _logger.LogWarning("IP blocked: {IP} - {Reason}", dto.IpAddress, dto.Reason);
//             return new ApiResponse<bool>(true, "IP blocked", true);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error blocking IP {IP}", dto.IpAddress);
//             return new ApiResponse<bool>(false, "Error blocking IP", false);
//         }
//     }
//
//     public async Task<ApiResponse<bool>> UnblockIPAsync(Guid id)
//     {
//         try
//         {
//             var blockedIP = await _context.Set<BlockedIP>().FindAsync(id);
//             if (blockedIP == null)
//                 return new ApiResponse<bool>(false, "Blocked IP not found", false);
//
//             blockedIP.IsActive = false;
//             await _context.SaveChangesAsync();
//
//             return new ApiResponse<bool>(true, "IP unblocked", true);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error unblocking IP {Id}", id);
//             return new ApiResponse<bool>(false, "Error unblocking IP", false);
//         }
//     }
//
//     public async Task<ApiResponse<List<IPWhitelistDto>>> GetIPWhitelistAsync()
//     {
//         try
//         {
//             var whitelist = await _context.AdminIpWhitelists
//                 .Where(w => !w.IsDeleted)
//                 .Select(w => new IPWhitelistDto(w.Id, w.IpAddress, w.Description, w.IsActive, w.CreatedAt))
//                 .ToListAsync();
//
//             return new ApiResponse<List<IPWhitelistDto>>(true, "Whitelist retrieved", whitelist);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error getting IP whitelist");
//             return new ApiResponse<List<IPWhitelistDto>>(false, "Error retrieving whitelist", null!);
//         }
//     }
//
//     public async Task<ApiResponse<bool>> AddIPToWhitelistAsync(AddIPWhitelistDto dto, Guid addedByUserId)
//     {
//         try
//         {
//             var entry = new AdminIpWhitelist
//             {
//                 IpAddress = dto.IpAddress,
//                 Description = dto.Description,
//                 IsActive = true,
//                 AddedByUserId = addedByUserId
//             };
//
//             _context.AdminIpWhitelists.Add(entry);
//             await _context.SaveChangesAsync();
//
//             return new ApiResponse<bool>(true, "IP added to whitelist", true);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error adding IP to whitelist");
//             return new ApiResponse<bool>(false, "Error adding IP", false);
//         }
//     }
//
//     public async Task<ApiResponse<bool>> RemoveIPFromWhitelistAsync(Guid id)
//     {
//         try
//         {
//             var entry = await _context.AdminIpWhitelists.FindAsync(id);
//             if (entry == null)
//                 return new ApiResponse<bool>(false, "IP not found", false);
//
//             entry.IsDeleted = true;
//             await _context.SaveChangesAsync();
//
//             return new ApiResponse<bool>(true, "IP removed from whitelist", true);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error removing IP from whitelist");
//             return new ApiResponse<bool>(false, "Error removing IP", false);
//         }
//     }
//
//     // ==================== FINANCIAL ====================
//
//     public async Task<ApiResponse<FinancialOverviewDto>> GetFinancialOverviewAsync()
//     {
//         try
//         {
//             var now = DateTime.UtcNow;
//             var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
//             var startOfYear = new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
//
//             var companies = await _context.Companies.ToListAsync(); // Small table
//
//             // PERF: DB-level aggregation for transactions
//             var totalVolume = await _context.Transactions.Where(t => !t.IsDeleted).SumAsync(t => t.Amount);
//             var monthlyVolume = await _context.Transactions.Where(t => !t.IsDeleted && t.TransactionDate >= startOfMonth).SumAsync(t => t.Amount);
//             var txnCount = await _context.Transactions.Where(t => !t.IsDeleted).CountAsync();
//             var avgTransactionSize = txnCount > 0 ? totalVolume / txnCount : 0;
//
//             var monthlyRevenue = companies.Where(c => c.IsActive).Sum(c => c.MonthlyFee);
//             var yearlyRevenue = monthlyRevenue * now.Month;
//             var totalRevenue = companies.Sum(c => c.TotalPaid);
//
//             var revenueByPlan = Enum.GetValues<SubscriptionPlan>()
//                 .Select(plan => {
//                     var planCompanies = companies.Where(c => c.SubscriptionPlan == plan).ToList();
//                     return new RevenueByPlanDto(
//                         plan.ToString(),
//                         planCompanies.Sum(c => c.MonthlyFee),
//                         planCompanies.Count,
//                         companies.Any() ? (decimal)planCompanies.Count / companies.Count * 100 : 0
//                     );
//                 })
//                 .Where(r => r.CompanyCount > 0)
//                 .ToList();
//
//             // PERF: DB-level GroupBy for transaction type stats
//             var txnByType = await _context.Transactions
//                 .Where(t => !t.IsDeleted)
//                 .GroupBy(t => t.TransactionType)
//                 .Select(g => new TransactionTypeStatsDto(g.Key.ToString(), g.Count(), g.Sum(t => t.Amount)))
//                 .ToListAsync();
//
//             var overview = new FinancialOverviewDto(
//                 totalRevenue, monthlyRevenue, yearlyRevenue, 0, 0,
//                 revenueByPlan, totalVolume, monthlyVolume, avgTransactionSize, txnByType
//             );
//
//             return new ApiResponse<FinancialOverviewDto>(true, "Financial overview retrieved", overview);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error getting financial overview");
//             return new ApiResponse<FinancialOverviewDto>(false, "Error retrieving financial overview", null!);
//         }
//     }
//
//     public async Task<ApiResponse<PagedResult<PaymentHistoryDto>>> GetPaymentHistoryAsync(int page, int pageSize, Guid? companyId = null)
//     {
//         try
//         {
//             var query = _context.Set<SubscriptionPayment>().Where(p => !p.IsDeleted);
//             if (companyId.HasValue)
//                 query = query.Where(p => p.CompanyId == companyId.Value);
//
//             var totalCount = await query.CountAsync();
//             var payments = await query
//                 .OrderByDescending(p => p.CreatedAt)
//                 .Skip((page - 1) * pageSize)
//                 .Take(pageSize)
//                 .ToListAsync();
//
//             var companyIds = payments.Select(p => p.CompanyId).Distinct().ToList();
//             var companies = await _context.Companies.Where(c => companyIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);
//
//             var dtos = payments.Select(p => new PaymentHistoryDto(
//                 p.Id, p.CompanyId, companies.GetValueOrDefault(p.CompanyId, "Unknown"),
//                 p.Amount, p.Currency, p.PaymentMethod, p.Status, p.Reference, p.PaidAt, p.PeriodStart, p.PeriodEnd
//             )).ToList();
//
//             return new ApiResponse<PagedResult<PaymentHistoryDto>>(true, "Payment history retrieved",
//                 new PagedResult<PaymentHistoryDto>(dtos, totalCount, page, pageSize));
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error getting payment history");
//             return new ApiResponse<PagedResult<PaymentHistoryDto>>(false, "Error retrieving payment history", null!);
//         }
//     }
//
//     public async Task<ApiResponse<PaymentHistoryDto>> RecordPaymentAsync(RecordPaymentDto dto)
//     {
//         try
//         {
//             var company = await _context.Companies.FindAsync(dto.CompanyId);
//             if (company == null)
//                 return new ApiResponse<PaymentHistoryDto>(false, "Company not found", null!);
//
//             var payment = new SubscriptionPayment
//             {
//                 CompanyId = dto.CompanyId,
//                 Amount = dto.Amount,
//                 Currency = dto.Currency,
//                 PaymentMethod = dto.PaymentMethod,
//                 Reference = dto.Reference,
//                 Status = "Completed",
//                 PaidAt = DateTime.UtcNow,
//                 PeriodStart = DateTime.UtcNow,
//                 PeriodEnd = DateTime.UtcNow.AddMonths(1),
//                 Notes = dto.Notes
//             };
//
//             _context.Set<SubscriptionPayment>().Add(payment);
//
//             // Update company
//             company.TotalPaid += dto.Amount;
//             company.LastPaymentDate = DateTime.UtcNow;
//             company.SubscriptionStatus = SubscriptionStatus.Active;
//             company.SubscriptionExpiresAt = DateTime.UtcNow.AddMonths(1);
//
//             await _context.SaveChangesAsync();
//
//             return new ApiResponse<PaymentHistoryDto>(true, "Payment recorded",
//                 new PaymentHistoryDto(payment.Id, dto.CompanyId, company.Name, dto.Amount, dto.Currency,
//                     dto.PaymentMethod, "Completed", dto.Reference, payment.PaidAt, payment.PeriodStart, payment.PeriodEnd));
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error recording payment");
//             return new ApiResponse<PaymentHistoryDto>(false, "Error recording payment", null!);
//         }
//     }
//
//     // ==================== ANALYTICS ====================
//
//     public async Task<ApiResponse<AnalyticsOverviewDto>> GetAnalyticsOverviewAsync(DateTime? startDate = null, DateTime? endDate = null)
//     {
//         try
//         {
//             var now = DateTime.UtcNow;
//             var today = now.Date;
//             var weekAgo = today.AddDays(-7);
//             var monthAgo = today.AddDays(-30);
//
//             var dailyActiveUsers = await _context.LoginHistories
//                 .Where(l => l.LoginAt >= today && l.IsSuccessful)
//                 .Select(l => l.UserId ?? l.CompanyId)
//                 .Distinct()
//                 .CountAsync();
//
//             var weeklyActiveUsers = await _context.LoginHistories
//                 .Where(l => l.LoginAt >= weekAgo && l.IsSuccessful)
//                 .Select(l => l.UserId ?? l.CompanyId)
//                 .Distinct()
//                 .CountAsync();
//
//             var monthlyActiveUsers = await _context.LoginHistories
//                 .Where(l => l.LoginAt >= monthAgo && l.IsSuccessful)
//                 .Select(l => l.UserId ?? l.CompanyId)
//                 .Distinct()
//                 .CountAsync();
//
//             // Feature usage (placeholder - would need feature tracking)
//             var featureUsage = new List<FeatureUsageDto>
//             {
//                 new("Transactions", await _context.Transactions.CountAsync(), await _context.Transactions.Select(t => t.CompanyId).Distinct().CountAsync()),
//                 new("Invoices", await _context.Invoices.CountAsync(), await _context.Invoices.Select(i => i.CompanyId).Distinct().CountAsync()),
//                 new("Expenses", await _context.Expenses.CountAsync(), await _context.Expenses.Select(e => e.CompanyId).Distinct().CountAsync()),
//                 new("Reconciliation", await _context.Reconciliations.CountAsync(), await _context.Reconciliations.Select(r => r.CompanyId).Distinct().CountAsync())
//             };
//
//             var overview = new AnalyticsOverviewDto(
//                 dailyActiveUsers, weeklyActiveUsers, monthlyActiveUsers, 1800, // 30 min avg session
//                 featureUsage,
//                 new List<GrowthDataPointDto>(),
//                 new List<GrowthDataPointDto>(),
//                 new List<GrowthDataPointDto>()
//             );
//
//             return new ApiResponse<AnalyticsOverviewDto>(true, "Analytics retrieved", overview);
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error getting analytics overview");
//             return new ApiResponse<AnalyticsOverviewDto>(false, "Error retrieving analytics", null!);
//         }
//     }
//
//     // ==================== AUDIT LOGS ====================
//
//     public async Task<ApiResponse<PagedResult<AdminAuditLogDto>>> GetAuditLogsExtendedAsync(
//         int page, int pageSize, AuditLogFilterDto? filter = null)
//     {
//         try
//         {
//             var query = _context.AuditLogs.AsQueryable();
//
//             if (filter != null)
//             {
//                 if (filter.CompanyId.HasValue)
//                     query = query.Where(l => l.CompanyId == filter.CompanyId);
//                 if (filter.UserId.HasValue)
//                     query = query.Where(l => l.UserId == filter.UserId);
//                 if (!string.IsNullOrWhiteSpace(filter.Action))
//                     query = query.Where(l => l.Action.ToString() == filter.Action);
//                 if (!string.IsNullOrWhiteSpace(filter.EntityType))
//                     query = query.Where(l => l.EntityType == filter.EntityType);
//                 if (filter.StartDate.HasValue)
//                     query = query.Where(l => l.CreatedAt >= filter.StartDate.Value);
//                 if (filter.EndDate.HasValue)
//                     query = query.Where(l => l.CreatedAt <= filter.EndDate.Value);
//             }
//
//             var totalCount = await query.CountAsync();
//             var logs = await query
//                 .OrderByDescending(l => l.CreatedAt)
//                 .Skip((page - 1) * pageSize)
//                 .Take(pageSize)
//                 .ToListAsync();
//
//             var companyIds = logs.Where(l => l.CompanyId.HasValue).Select(l => l.CompanyId!.Value).Distinct();
//             var userIds = logs.Where(l => l.UserId.HasValue).Select(l => l.UserId!.Value).Distinct();
//
//             var companies = await _context.Companies.Where(c => companyIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);
//             var users = await _context.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName);
//
//             var dtos = logs.Select(l => new AdminAuditLogDto(
//                 l.Id, l.CompanyId,
//                 l.CompanyId.HasValue && companies.ContainsKey(l.CompanyId.Value) ? companies[l.CompanyId.Value] : null,
//                 l.UserId,
//                 l.UserId.HasValue && users.ContainsKey(l.UserId.Value) ? users[l.UserId.Value] : null,
//                 l.Action, l.EntityType, l.EntityId, l.OldValues, l.NewValues, l.CreatedAt
//             )).ToList();
//
//             return new ApiResponse<PagedResult<AdminAuditLogDto>>(true, "Audit logs retrieved",
//                 new PagedResult<AdminAuditLogDto>(dtos, totalCount, page, pageSize));
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error getting audit logs");
//             return new ApiResponse<PagedResult<AdminAuditLogDto>>(false, "Error retrieving audit logs", null!);
//         }
//     }
//
//     public async Task<ApiResponse<byte[]>> ExportAuditLogsAsync(AuditLogFilterDto? filter = null)
//     {
//         try
//         {
//             var response = await GetAuditLogsExtendedAsync(1, 10000, filter);
//             if (!response.Success || response.Data == null)
//                 return new ApiResponse<byte[]>(false, "Error exporting logs", null!);
//
//             var csv = new StringBuilder();
//             csv.AppendLine("Timestamp,User,Company,Action,Entity,Details,IP Address");
//
//             foreach (var log in response.Data.Items)
//             {
//                 csv.AppendLine($"\"{log.CreatedAt:yyyy-MM-dd HH:mm:ss}\",\"{log.UserName ?? "System"}\",\"{log.CompanyName ?? "Platform"}\",\"{log.Action}\",\"{log.EntityType}\",\"{log.NewValues ?? ""}\",\"\"");
//             }
//
//             return new ApiResponse<byte[]>(true, "Export complete", Encoding.UTF8.GetBytes(csv.ToString()));
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Error exporting audit logs");
//             return new ApiResponse<byte[]>(false, "Error exporting logs", null!);
//         }
//     }
// }
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SARIFF.Core.DTOs;
using SARIFF.Core.Entities;
using SARIFF.Core.Enums;
using SARIFF.Core.Interfaces;
using SARIFF.Infrastructure.Data;
using System.Diagnostics;
using System.Text;

namespace SARIFF.Infrastructure.Services;

public class SuperAdminService : ISuperAdminService
{
    private readonly AppDbContext _context;
    private readonly ILogger<SuperAdminService> _logger;
    private static readonly DateTime _startTime = DateTime.UtcNow;

    public SuperAdminService(AppDbContext context, ILogger<SuperAdminService> logger)
    {
        _context = context;
        _logger = logger;
    }

    // ==================== DASHBOARD ====================

    public async Task<ApiResponse<SuperAdminDashboardExtendedDto>> GetDashboardAsync()
    {
        try
        {
            var now = DateTime.UtcNow;
            var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var lastMonth = startOfMonth.AddMonths(-1);
            var today = now.Date;

            // PERF: Use DB-level aggregation instead of loading all entities into memory
            
            // Company stats — single query with conditional counts
            var companies = await _context.Companies.ToListAsync(); // Companies table is small (< 100 rows typically)
            var companyIds = companies.Select(c => c.Id).ToList();
            var totalCompanies = companies.Count;
            var activeCompanies = companies.Count(c => c.IsActive && c.SubscriptionStatus == SubscriptionStatus.Active);
            var trialCompanies = companies.Count(c => c.SubscriptionStatus == SubscriptionStatus.Trial);
            var expiredCompanies = companies.Count(c => c.SubscriptionStatus == SubscriptionStatus.Expired);
            var suspendedCompanies = companies.Count(c => c.SubscriptionStatus == SubscriptionStatus.Suspended || !c.IsActive);

            // User stats — COUNT in DB, not ToListAsync
            var totalUsers = await _context.Users.CountAsync();
            var totalClients = await _context.Users.CountAsync(u => u.Role == UserRole.Client);
            var activeUsersToday = await _context.LoginHistories
                .Where(l => l.LoginAt >= today && l.IsSuccessful)
                .Select(l => l.UserId ?? l.CompanyId)
                .Distinct()
                .CountAsync();

            // Transaction stats — COUNT and SUM in DB
            var totalTransactionsCount = await _context.Transactions.CountAsync();
            var monthlyTransactionsCount = await _context.Transactions.CountAsync(t => t.TransactionDate >= startOfMonth);
            var totalVolume = await _context.Transactions.SumAsync(t => t.Amount);
            var monthlyVolume = await _context.Transactions
                .Where(t => t.TransactionDate >= startOfMonth)
                .SumAsync(t => t.Amount);

            // Revenue (from subscription payments)
            var monthlyRevenue = companies.Where(c => c.IsActive).Sum(c => c.MonthlyFee);
            var totalRevenue = companies.Sum(c => c.TotalPaid);
            var pendingPayments = 0m; // Would come from SubscriptionPayments with Status = Pending

            // Growth calculations (simplified - compare to last month)
            var lastMonthCompanies = companies.Count(c => c.CreatedAt < startOfMonth);
            var companiesGrowth = lastMonthCompanies > 0 ? ((totalCompanies - lastMonthCompanies) / (decimal)lastMonthCompanies) * 100 : 0;

            // System health summary
            var errorsLast24h = await _context.SystemLogs.CountAsync(l => l.Level == "Error" && l.CreatedAt >= now.AddHours(-24));
            var securityAlerts = await _context.Set<SecurityAlert>().CountAsync(a => !a.IsResolved && !a.IsDeleted);

            // Top companies by volume — DB-level aggregation
            var topByVolume = await _context.Transactions
                .GroupBy(t => t.CompanyId)
                .Select(g => new { CompanyId = g.Key, Volume = g.Sum(t => t.Amount) })
                .OrderByDescending(x => x.Volume)
                .Take(5)
                .ToListAsync();

            var topCompaniesByVolume = new List<CompanyRankDto>();
            var rank = 1;
            foreach (var item in topByVolume)
            {
                var company = companies.FirstOrDefault(c => c.Id == item.CompanyId);
                if (company != null)
                {
                    topCompaniesByVolume.Add(new CompanyRankDto(company.Id, company.Name, company.Code, item.Volume, rank++));
                }
            }

            // Top by transaction count — DB-level aggregation
            var topByCount = await _context.Transactions
                .GroupBy(t => t.CompanyId)
                .Select(g => new { CompanyId = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToListAsync();

            var topCompaniesByTransactions = new List<CompanyRankDto>();
            rank = 1;
            foreach (var item in topByCount)
            {
                var company = companies.FirstOrDefault(c => c.Id == item.CompanyId);
                if (company != null)
                {
                    topCompaniesByTransactions.Add(new CompanyRankDto(company.Id, company.Name, company.Code, item.Count, rank++));
                }
            }

            // Recent signups
            // Load users and transactions for per-company stats (MapToCompanyStats, companySummaries)
            var users = await _context.Users.Where(u => companyIds.Contains(u.CompanyId ?? Guid.Empty)).ToListAsync();
            var transactions = await _context.Transactions.Where(t => companyIds.Contains(t.CompanyId)).ToListAsync();

            var recentSignups = companies
                .OrderByDescending(c => c.CreatedAt)
                .Take(5)
                .Select(c => MapToCompanyStats(c, users, transactions, now))
                .ToList();

            // Expiring subscriptions (next 30 days)
            var expiringSubscriptions = companies
                .Where(c => c.SubscriptionExpiresAt.HasValue && c.SubscriptionExpiresAt <= now.AddDays(30) && c.SubscriptionExpiresAt > now)
                .OrderBy(c => c.SubscriptionExpiresAt)
                .Take(5)
                .Select(c => MapToCompanyStats(c, users, transactions, now))
                .ToList();

            // For backward compatibility - basic company summaries
            var companySummaries = companies.Select(c => new CompanySummaryDto(
                c.Id, c.Code, c.Name, c.OwnerName,
                users.Count(u => u.CompanyId == c.Id && u.Role == UserRole.Client),
                transactions.Count(t => t.CompanyId == c.Id),
                0, 0, c.IsActive
            )).ToList();

            // Recent errors
            var recentErrors = await _context.SystemLogs
                .Where(l => l.Level == "Error")
                .OrderByDescending(l => l.CreatedAt)
                .Take(10)
                .Select(e => new SystemLogResponseDto(e.Id, e.Level, e.Source, e.Message, e.CompanyId, e.CreatedAt))
                .ToListAsync();

            var dashboard = new SuperAdminDashboardExtendedDto(
                TotalCompanies: totalCompanies,
                ActiveCompanies: activeCompanies,
                TrialCompanies: trialCompanies,
                ExpiredCompanies: expiredCompanies,
                SuspendedCompanies: suspendedCompanies,
                CompaniesGrowth: companiesGrowth,
                TotalUsers: totalUsers,
                TotalClients: totalClients,
                ActiveUsersToday: activeUsersToday,
                UsersGrowth: 0,
                MonthlyRecurringRevenue: monthlyRevenue,
                TotalRevenue: totalRevenue,
                PendingPayments: pendingPayments,
                RevenueGrowth: 0,
                TotalTransactionsVolume: totalVolume,
                MonthlyTransactionsVolume: monthlyVolume,
                TotalTransactionsCount: totalTransactionsCount,
                MonthlyTransactionsCount: monthlyTransactionsCount,
                VolumeGrowth: 0,
                SystemStatus: errorsLast24h > 10 ? "Degraded" : "Healthy",
                ErrorsLast24h: errorsLast24h,
                SecurityAlertsActive: securityAlerts,
                TopCompaniesByVolume: topCompaniesByVolume,
                TopCompaniesByTransactions: topCompaniesByTransactions,
                RecentSignups: recentSignups,
                ExpiringSubscriptions: expiringSubscriptions,
                RecentErrors: recentErrors,
                Companies: companySummaries
            );

            return new ApiResponse<SuperAdminDashboardExtendedDto>(true, "Dashboard loaded", dashboard);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting SuperAdmin dashboard");
            return new ApiResponse<SuperAdminDashboardExtendedDto>(false, "Error loading dashboard", null!);
        }
    }

    private CompanyStatsDto MapToCompanyStats(Company c, List<User> allUsers, List<Transaction> allTransactions, DateTime now)
    {
        var companyUsers = allUsers.Where(u => u.CompanyId == c.Id).ToList();
        var companyTransactions = allTransactions.Where(t => t.CompanyId == c.Id).ToList();
        var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        return new CompanyStatsDto(
            c.Id, c.Code, c.Name, c.OwnerName, c.Email, c.WhatsAppNumber, c.IsActive, c.CreatedAt, c.LastLoginAt,
            c.SubscriptionPlan, c.SubscriptionStatus, c.SubscriptionStartDate, c.SubscriptionExpiresAt,
            c.MonthlyFee, c.TotalPaid, c.LastPaymentDate,
            companyUsers.Count,
            companyUsers.Count(u => u.IsActive),
            companyUsers.Count(u => u.Role == UserRole.Client),
            companyUsers.Count(u => u.Role == UserRole.Client && u.IsActive),
            companyTransactions.Count,
            companyTransactions.Count(t => t.TransactionDate >= startOfMonth),
            companyTransactions.Sum(t => t.Amount),
            companyTransactions.Where(t => t.TransactionDate >= startOfMonth).Sum(t => t.Amount),
            companyTransactions.OrderByDescending(t => t.CreatedAt).FirstOrDefault()?.CreatedAt,
            0 // Error count - would need to query SystemLogs
        );
    }

    // ==================== COMPANIES ====================

    public async Task<ApiResponse<PagedResult<CompanyStatsDto>>> GetAllCompaniesWithStatsAsync(
        int page, int pageSize, string? search = null, string? status = null)
    {
        try
        {
            var query = _context.Companies.AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.ToLower();
                query = query.Where(c =>
                    c.Name.ToLower().Contains(search) ||
                    c.Code.ToLower().Contains(search) ||
                    c.OwnerName.ToLower().Contains(search) ||
                    (c.Email != null && c.Email.ToLower().Contains(search)));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                switch (status.ToLower())
                {
                    case "active":
                        query = query.Where(c => c.IsActive && c.SubscriptionStatus == SubscriptionStatus.Active);
                        break;
                    case "trial":
                        query = query.Where(c => c.SubscriptionStatus == SubscriptionStatus.Trial);
                        break;
                    case "expired":
                        query = query.Where(c => c.SubscriptionStatus == SubscriptionStatus.Expired);
                        break;
                    case "suspended":
                        query = query.Where(c => !c.IsActive || c.SubscriptionStatus == SubscriptionStatus.Suspended);
                        break;
                }
            }

            var totalCount = await query.CountAsync();
            var companies = await query
                .OrderByDescending(c => c.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var now = DateTime.UtcNow;
            var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var companyIds = companies.Select(c => c.Id).ToList();

            var users = await _context.Users.Where(u => companyIds.Contains(u.CompanyId ?? Guid.Empty)).ToListAsync();
            var transactions = await _context.Transactions.Where(t => companyIds.Contains(t.CompanyId)).ToListAsync();

            var companyStats = companies.Select(c => MapToCompanyStats(c, users, transactions, now)).ToList();

            var result = new PagedResult<CompanyStatsDto>(companyStats, totalCount, page, pageSize);
            return new ApiResponse<PagedResult<CompanyStatsDto>>(true, "Companies retrieved", result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting companies");
            return new ApiResponse<PagedResult<CompanyStatsDto>>(false, "Error retrieving companies", null!);
        }
    }

    public async Task<ApiResponse<CompanyDetailDto>> GetCompanyDetailAsync(Guid companyId)
    {
        try
        {
            var company = await _context.Companies.FirstOrDefaultAsync(c => c.Id == companyId);
            if (company == null)
                return new ApiResponse<CompanyDetailDto>(false, "Company not found", null!);

            var now = DateTime.UtcNow;
            var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            var users = await _context.Users.Where(u => u.CompanyId == companyId).ToListAsync();
            var transactions = await _context.Transactions.Where(t => t.CompanyId == companyId).ToListAsync();
            var bankAccounts = await _context.BankAccounts.Where(b => b.CompanyId == companyId).ToListAsync();
            var mpesaAgents = await _context.MpesaAgents.Where(m => m.CompanyId == companyId).ToListAsync();
            var cashAccounts = await _context.CashAccounts.Where(c => c.CompanyId == companyId).ToListAsync();

            var loginHistory = await _context.LoginHistories
                .Where(l => l.CompanyId == companyId)
                .OrderByDescending(l => l.LoginAt)
                .Take(10)
                .Select(l => new AdminLoginHistoryDto(
                    l.Id, l.CompanyId, company.Name, l.UserId, null, l.UserRole,
                    l.IpAddress, l.Location, l.IsSuccessful, l.FailureReason, l.LoginAt
                ))
                .ToListAsync();

            var recentTxns = transactions
                .OrderByDescending(t => t.TransactionDate)
                .Take(5)
                .Select(t => new RecentTransactionDto(
                    t.Id, t.Code, t.Description, t.Amount, t.Currency.ToString(), t.TransactionType.ToString(), t.TransactionDate
                ))
                .ToList();

            var unreconciledCount = transactions.Count(t => t.ReconciliationStatus == ReconciliationStatus.Pending);
            var errorCount = await _context.SystemLogs.CountAsync(l => l.CompanyId == companyId && l.Level == "Error" && l.CreatedAt >= startOfMonth);

            var detail = new CompanyDetailDto(
                company.Id, company.Code, company.Name, company.OwnerName, company.Email, company.WhatsAppNumber,
                company.LogoUrl, company.TaxId, company.Website, company.Address,
                company.IsActive, company.CreatedAt, company.LastLoginAt,
                company.SubscriptionPlan, company.SubscriptionStatus, company.SubscriptionStartDate, company.SubscriptionExpiresAt,
                company.MonthlyFee, company.TotalPaid, company.LastPaymentDate,
                users.Count, users.Count(u => u.IsActive),
                users.Count(u => u.Role == UserRole.Client), users.Count(u => u.Role == UserRole.Client && u.IsActive),
                transactions.Count, transactions.Count(t => t.TransactionDate >= startOfMonth),
                transactions.Sum(t => t.Amount), transactions.Where(t => t.TransactionDate >= startOfMonth).Sum(t => t.Amount),
                bankAccounts.Count, mpesaAgents.Count,
                cashAccounts.Where(c => c.Currency == Currency.KES).Sum(c => c.Balance),
                cashAccounts.Where(c => c.Currency == Currency.USD).Sum(c => c.Balance),
                bankAccounts.Where(b => b.Currency == Currency.KES).Sum(b => b.Balance),
                bankAccounts.Where(b => b.Currency == Currency.USD).Sum(b => b.Balance),
                mpesaAgents.Sum(m => m.Balance),
                users.Where(u => u.Role == UserRole.Client).Sum(u => u.BalanceKES),
                users.Where(u => u.Role == UserRole.Client).Sum(u => u.BalanceUSD),
                recentTxns, loginHistory, unreconciledCount, errorCount,
                company.IsTransactionPinEnabled, !string.IsNullOrEmpty(company.TransactionPinHash)
            );

            return new ApiResponse<CompanyDetailDto>(true, "Company details retrieved", detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting company details for {CompanyId}", companyId);
            return new ApiResponse<CompanyDetailDto>(false, "Error retrieving company details", null!);
        }
    }

    public async Task<ApiResponse<bool>> UpdateSubscriptionAsync(Guid companyId, UpdateSubscriptionDto dto)
    {
        try
        {
            var company = await _context.Companies.FindAsync(companyId);
            if (company == null)
                return new ApiResponse<bool>(false, "Company not found", false);

            company.SubscriptionPlan = dto.Plan;
            company.MonthlyFee = dto.MonthlyFee;
            if (dto.ExpiresAt.HasValue)
                company.SubscriptionExpiresAt = dto.ExpiresAt;

            company.SubscriptionStatus = dto.Status ?? SubscriptionStatus.Active;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Subscription updated for company {CompanyId}: {Plan}", companyId, dto.Plan);
            return new ApiResponse<bool>(true, "Subscription updated", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating subscription for {CompanyId}", companyId);
            return new ApiResponse<bool>(false, "Error updating subscription", false);
        }
    }

    public async Task<ApiResponse<bool>> SuspendCompanyAsync(Guid companyId, SuspendCompanyDto dto)
    {
        try
        {
            var company = await _context.Companies.FindAsync(companyId);
            if (company == null)
                return new ApiResponse<bool>(false, "Company not found", false);

            company.IsActive = false;
            company.SubscriptionStatus = SubscriptionStatus.Suspended;
            await _context.SaveChangesAsync();

            // Log audit
            _context.AuditLogs.Add(new AuditLog
            {
                CompanyId = companyId,
                Action = AuditAction.Update,
                EntityType = "Company",
                EntityId = companyId,
                NewValues = $"Suspended: {dto.Reason}"
            });
            await _context.SaveChangesAsync();

            _logger.LogWarning("Company {CompanyId} suspended: {Reason}", companyId, dto.Reason);
            return new ApiResponse<bool>(true, "Company suspended", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error suspending company {CompanyId}", companyId);
            return new ApiResponse<bool>(false, "Error suspending company", false);
        }
    }

    public async Task<ApiResponse<bool>> ActivateCompanyAsync(Guid companyId)
    {
        try
        {
            var company = await _context.Companies.FindAsync(companyId);
            if (company == null)
                return new ApiResponse<bool>(false, "Company not found", false);

            company.IsActive = true;
            company.SubscriptionStatus = SubscriptionStatus.Active;
            company.FailedLoginAttempts = 0;
            company.LockedUntil = null;
            await _context.SaveChangesAsync();

            return new ApiResponse<bool>(true, "Company activated", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error activating company {CompanyId}", companyId);
            return new ApiResponse<bool>(false, "Error activating company", false);
        }
    }

    /// <summary>
    /// Reset password for an Office User (Company)
    /// </summary>
    public async Task<ApiResponse<bool>> ResetOfficeUserPasswordAsync(Guid companyId, AdminResetPasswordDto dto)
    {
        try
        {
            var pwdCheck = ValidationHelper.ValidatePassword(dto.NewPassword);
            if (!pwdCheck.IsValid)
                return new ApiResponse<bool>(false, pwdCheck.Error!, false);

            var company = await _context.Companies.FindAsync(companyId);
            if (company == null)
                return new ApiResponse<bool>(false, "Company not found", false);

            company.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
            company.FailedLoginAttempts = 0;
            company.LockedUntil = null;
            await _context.SaveChangesAsync();

            // Log audit
            _context.AuditLogs.Add(new AuditLog
            {
                CompanyId = companyId,
                Action = AuditAction.Update,
                EntityType = "Company",
                EntityId = companyId,
                NewValues = "Password reset by SuperAdmin"
            });
            await _context.SaveChangesAsync();

            _logger.LogInformation("Password reset for company {CompanyId} by SuperAdmin", companyId);
            return new ApiResponse<bool>(true, "Password reset successfully", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting password for company {CompanyId}", companyId);
            return new ApiResponse<bool>(false, "Error resetting password", false);
        }
    }

    // ==================== SYSTEM HEALTH ====================

    public async Task<ApiResponse<SystemHealthDto>> GetSystemHealthAsync()
    {
        try
        {
            var now = DateTime.UtcNow;
            var last24h = now.AddHours(-24);

            // Database health check
            var dbStopwatch = Stopwatch.StartNew();
            var canConnect = await _context.Database.CanConnectAsync();
            dbStopwatch.Stop();

            var dbStatus = canConnect ? "Healthy" : "Down";
            var dbLatency = (int)dbStopwatch.ElapsedMilliseconds;

            // Get log counts
            var errorsLast24h = await _context.SystemLogs.CountAsync(l => l.CreatedAt >= last24h && l.Level == "Error");
            var warningsLast24h = await _context.SystemLogs.CountAsync(l => l.CreatedAt >= last24h && l.Level == "Warning");
            var criticalLast24h = await _context.SystemLogs.CountAsync(l => l.CreatedAt >= last24h && l.Level == "Critical");

            var overallStatus = "Healthy";
            if (!canConnect) overallStatus = "Down";
            else if (errorsLast24h > 10 || dbLatency > 1000) overallStatus = "Degraded";

            var uptimeSeconds = (long)(now - _startTime).TotalSeconds;

            var health = new SystemHealthDto(
                overallStatus, "Healthy", 45, 120, errorsLast24h > 0 ? Math.Min((decimal)errorsLast24h / 100 * 100, 5) : 0,
                dbStatus, dbLatency, 10, 35, 60, 45, 25, uptimeSeconds, now,
                errorsLast24h, warningsLast24h, criticalLast24h
            );

            return new ApiResponse<SystemHealthDto>(true, "Health check complete", health);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting system health");
            return new ApiResponse<SystemHealthDto>(false, "Health check failed",
                new SystemHealthDto("Down", "Down", 0, 0, 100, "Down", 0, 0, 0, 0, 0, 0, 0, DateTime.UtcNow, 0, 0, 0));
        }
    }

    // ==================== SECURITY ====================

    public async Task<ApiResponse<SecurityOverviewDto>> GetSecurityOverviewAsync()
    {
        try
        {
            var now = DateTime.UtcNow;
            var last24h = now.AddHours(-24);
            var last7d = now.AddDays(-7);

            var failedLogins24h = await _context.LoginHistories.CountAsync(l => l.LoginAt >= last24h && !l.IsSuccessful);
            var failedLogins7d = await _context.LoginHistories.CountAsync(l => l.LoginAt >= last7d && !l.IsSuccessful);

            var lockedCompanies = await _context.Companies.CountAsync(c => c.LockedUntil != null && c.LockedUntil > now);
            var lockedUsers = await _context.Users.CountAsync(u => u.LockedUntil != null && u.LockedUntil > now);

            var activeAlerts = await _context.Set<SecurityAlert>().CountAsync(a => !a.IsResolved && !a.IsDeleted);
            var blockedIPs = await _context.Set<BlockedIP>().CountAsync(b => b.IsActive && !b.IsDeleted);

            var recentAlerts = await _context.Set<SecurityAlert>()
                .Where(a => !a.IsDeleted)
                .OrderByDescending(a => a.CreatedAt)
                .Take(10)
                .Select(a => new SecurityAlertDto(
                    a.Id, a.AlertType, a.Severity, a.Message, a.CompanyId, null, a.IpAddress, a.IsResolved, a.CreatedAt, a.ResolvedAt
                ))
                .ToListAsync();

            var recentFailedLogins = await _context.LoginHistories
                .Where(l => !l.IsSuccessful)
                .OrderByDescending(l => l.LoginAt)
                .Take(20)
                .Select(l => new AdminLoginHistoryDto(
                    l.Id, l.CompanyId, null, l.UserId, null, l.UserRole, l.IpAddress, l.Location, l.IsSuccessful, l.FailureReason, l.LoginAt
                ))
                .ToListAsync();

            var overview = new SecurityOverviewDto(
                failedLogins24h, failedLogins7d, lockedCompanies + lockedUsers, 0, blockedIPs, activeAlerts, recentAlerts, recentFailedLogins
            );

            return new ApiResponse<SecurityOverviewDto>(true, "Security overview retrieved", overview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting security overview");
            return new ApiResponse<SecurityOverviewDto>(false, "Error retrieving security overview", null!);
        }
    }

    public async Task<ApiResponse<PagedResult<SecurityAlertDto>>> GetSecurityAlertsAsync(int page, int pageSize, bool? resolved = null)
    {
        try
        {
            var query = _context.Set<SecurityAlert>().Where(a => !a.IsDeleted);
            if (resolved.HasValue)
                query = query.Where(a => a.IsResolved == resolved.Value);

            var totalCount = await query.CountAsync();
            var alerts = await query
                .OrderByDescending(a => a.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(a => new SecurityAlertDto(
                    a.Id, a.AlertType, a.Severity, a.Message, a.CompanyId, null, a.IpAddress, a.IsResolved, a.CreatedAt, a.ResolvedAt
                ))
                .ToListAsync();

            return new ApiResponse<PagedResult<SecurityAlertDto>>(true, "Alerts retrieved",
                new PagedResult<SecurityAlertDto>(alerts, totalCount, page, pageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting security alerts");
            return new ApiResponse<PagedResult<SecurityAlertDto>>(false, "Error retrieving alerts", null!);
        }
    }

    public async Task<ApiResponse<bool>> ResolveSecurityAlertAsync(Guid alertId, ResolveAlertDto dto)
    {
        try
        {
            var alert = await _context.Set<SecurityAlert>().FindAsync(alertId);
            if (alert == null)
                return new ApiResponse<bool>(false, "Alert not found", false);

            alert.IsResolved = true;
            alert.ResolvedAt = DateTime.UtcNow;
            alert.ResolutionNotes = dto.Notes;
            await _context.SaveChangesAsync();

            return new ApiResponse<bool>(true, "Alert resolved", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving alert {AlertId}", alertId);
            return new ApiResponse<bool>(false, "Error resolving alert", false);
        }
    }

    public async Task<ApiResponse<bool>> BlockIPAsync(BlockIPDto dto)
    {
        try
        {
            var blockedIP = new BlockedIP
            {
                IpAddress = dto.IpAddress,
                Reason = dto.Reason,
                BlockedUntil = dto.BlockUntil,
                IsActive = true
            };

            _context.Set<BlockedIP>().Add(blockedIP);
            await _context.SaveChangesAsync();

            _logger.LogWarning("IP blocked: {IP} - {Reason}", dto.IpAddress, dto.Reason);
            return new ApiResponse<bool>(true, "IP blocked", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error blocking IP {IP}", dto.IpAddress);
            return new ApiResponse<bool>(false, "Error blocking IP", false);
        }
    }

    public async Task<ApiResponse<bool>> UnblockIPAsync(Guid id)
    {
        try
        {
            var blockedIP = await _context.Set<BlockedIP>().FindAsync(id);
            if (blockedIP == null)
                return new ApiResponse<bool>(false, "Blocked IP not found", false);

            blockedIP.IsActive = false;
            await _context.SaveChangesAsync();

            return new ApiResponse<bool>(true, "IP unblocked", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unblocking IP {Id}", id);
            return new ApiResponse<bool>(false, "Error unblocking IP", false);
        }
    }

    public async Task<ApiResponse<List<IPWhitelistDto>>> GetIPWhitelistAsync()
    {
        try
        {
            var whitelist = await _context.AdminIpWhitelists
                .Where(w => !w.IsDeleted)
                .Select(w => new IPWhitelistDto(w.Id, w.IpAddress, w.Description, w.IsActive, w.CreatedAt))
                .ToListAsync();

            return new ApiResponse<List<IPWhitelistDto>>(true, "Whitelist retrieved", whitelist);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting IP whitelist");
            return new ApiResponse<List<IPWhitelistDto>>(false, "Error retrieving whitelist", null!);
        }
    }

    public async Task<ApiResponse<bool>> AddIPToWhitelistAsync(AddIPWhitelistDto dto, Guid addedByUserId)
    {
        try
        {
            var entry = new AdminIpWhitelist
            {
                IpAddress = dto.IpAddress,
                Description = dto.Description,
                IsActive = true,
                AddedByUserId = addedByUserId
            };

            _context.AdminIpWhitelists.Add(entry);
            await _context.SaveChangesAsync();

            return new ApiResponse<bool>(true, "IP added to whitelist", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding IP to whitelist");
            return new ApiResponse<bool>(false, "Error adding IP", false);
        }
    }

    public async Task<ApiResponse<bool>> RemoveIPFromWhitelistAsync(Guid id)
    {
        try
        {
            var entry = await _context.AdminIpWhitelists.FindAsync(id);
            if (entry == null)
                return new ApiResponse<bool>(false, "IP not found", false);

            entry.IsDeleted = true;
            await _context.SaveChangesAsync();

            return new ApiResponse<bool>(true, "IP removed from whitelist", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing IP from whitelist");
            return new ApiResponse<bool>(false, "Error removing IP", false);
        }
    }

    // ==================== FINANCIAL ====================

    public async Task<ApiResponse<FinancialOverviewDto>> GetFinancialOverviewAsync()
    {
        try
        {
            var now = DateTime.UtcNow;
            var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var startOfYear = new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var companies = await _context.Companies.ToListAsync(); // Small table

            // PERF: DB-level aggregation for transactions
            var totalVolume = await _context.Transactions.Where(t => !t.IsDeleted).SumAsync(t => t.Amount);
            var monthlyVolume = await _context.Transactions.Where(t => !t.IsDeleted && t.TransactionDate >= startOfMonth).SumAsync(t => t.Amount);
            var txnCount = await _context.Transactions.Where(t => !t.IsDeleted).CountAsync();
            var avgTransactionSize = txnCount > 0 ? totalVolume / txnCount : 0;

            var monthlyRevenue = companies.Where(c => c.IsActive).Sum(c => c.MonthlyFee);
            var yearlyRevenue = monthlyRevenue * now.Month;
            var totalRevenue = companies.Sum(c => c.TotalPaid);

            var revenueByPlan = Enum.GetValues<SubscriptionPlan>()
                .Select(plan => {
                    var planCompanies = companies.Where(c => c.SubscriptionPlan == plan).ToList();
                    return new RevenueByPlanDto(
                        plan.ToString(),
                        planCompanies.Sum(c => c.MonthlyFee),
                        planCompanies.Count,
                        companies.Any() ? (decimal)planCompanies.Count / companies.Count * 100 : 0
                    );
                })
                .Where(r => r.CompanyCount > 0)
                .ToList();

            // PERF: DB-level GroupBy for transaction type stats
            var txnByType = await _context.Transactions
                .Where(t => !t.IsDeleted)
                .GroupBy(t => t.TransactionType)
                .Select(g => new TransactionTypeStatsDto(g.Key.ToString(), g.Count(), g.Sum(t => t.Amount)))
                .ToListAsync();

            var overview = new FinancialOverviewDto(
                totalRevenue, monthlyRevenue, yearlyRevenue, 0, 0,
                revenueByPlan, totalVolume, monthlyVolume, avgTransactionSize, txnByType
            );

            return new ApiResponse<FinancialOverviewDto>(true, "Financial overview retrieved", overview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting financial overview");
            return new ApiResponse<FinancialOverviewDto>(false, "Error retrieving financial overview", null!);
        }
    }

    public async Task<ApiResponse<PagedResult<PaymentHistoryDto>>> GetPaymentHistoryAsync(int page, int pageSize, Guid? companyId = null)
    {
        try
        {
            var query = _context.Set<SubscriptionPayment>().Where(p => !p.IsDeleted);
            if (companyId.HasValue)
                query = query.Where(p => p.CompanyId == companyId.Value);

            var totalCount = await query.CountAsync();
            var payments = await query
                .OrderByDescending(p => p.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var companyIds = payments.Select(p => p.CompanyId).Distinct().ToList();
            var companies = await _context.Companies.Where(c => companyIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);

            var dtos = payments.Select(p => new PaymentHistoryDto(
                p.Id, p.CompanyId, companies.GetValueOrDefault(p.CompanyId, "Unknown"),
                p.Amount, p.Currency, p.PaymentMethod, p.Status, p.Reference, p.PaidAt, p.PeriodStart, p.PeriodEnd
            )).ToList();

            return new ApiResponse<PagedResult<PaymentHistoryDto>>(true, "Payment history retrieved",
                new PagedResult<PaymentHistoryDto>(dtos, totalCount, page, pageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting payment history");
            return new ApiResponse<PagedResult<PaymentHistoryDto>>(false, "Error retrieving payment history", null!);
        }
    }

    public async Task<ApiResponse<PaymentHistoryDto>> RecordPaymentAsync(RecordPaymentDto dto)
    {
        try
        {
            var company = await _context.Companies.FindAsync(dto.CompanyId);
            if (company == null)
                return new ApiResponse<PaymentHistoryDto>(false, "Company not found", null!);

            var payment = new SubscriptionPayment
            {
                CompanyId = dto.CompanyId,
                Amount = dto.Amount,
                Currency = dto.Currency,
                PaymentMethod = dto.PaymentMethod,
                Reference = dto.Reference,
                Status = "Completed",
                PaidAt = DateTime.UtcNow,
                PeriodStart = DateTime.UtcNow,
                PeriodEnd = DateTime.UtcNow.AddMonths(1),
                Notes = dto.Notes
            };

            _context.Set<SubscriptionPayment>().Add(payment);

            // Update company
            company.TotalPaid += dto.Amount;
            company.LastPaymentDate = DateTime.UtcNow;
            company.SubscriptionStatus = SubscriptionStatus.Active;
            company.SubscriptionExpiresAt = DateTime.UtcNow.AddMonths(1);

            await _context.SaveChangesAsync();

            return new ApiResponse<PaymentHistoryDto>(true, "Payment recorded",
                new PaymentHistoryDto(payment.Id, dto.CompanyId, company.Name, dto.Amount, dto.Currency,
                    dto.PaymentMethod, "Completed", dto.Reference, payment.PaidAt, payment.PeriodStart, payment.PeriodEnd));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording payment");
            return new ApiResponse<PaymentHistoryDto>(false, "Error recording payment", null!);
        }
    }

    // ==================== ANALYTICS ====================

    public async Task<ApiResponse<AnalyticsOverviewDto>> GetAnalyticsOverviewAsync(DateTime? startDate = null, DateTime? endDate = null)
    {
        try
        {
            var now = DateTime.UtcNow;
            var today = now.Date;
            var weekAgo = today.AddDays(-7);
            var monthAgo = today.AddDays(-30);

            var dailyActiveUsers = await _context.LoginHistories
                .Where(l => l.LoginAt >= today && l.IsSuccessful)
                .Select(l => l.UserId ?? l.CompanyId)
                .Distinct()
                .CountAsync();

            var weeklyActiveUsers = await _context.LoginHistories
                .Where(l => l.LoginAt >= weekAgo && l.IsSuccessful)
                .Select(l => l.UserId ?? l.CompanyId)
                .Distinct()
                .CountAsync();

            var monthlyActiveUsers = await _context.LoginHistories
                .Where(l => l.LoginAt >= monthAgo && l.IsSuccessful)
                .Select(l => l.UserId ?? l.CompanyId)
                .Distinct()
                .CountAsync();

            // Feature usage (placeholder - would need feature tracking)
            var featureUsage = new List<FeatureUsageDto>
            {
                new("Transactions", await _context.Transactions.CountAsync(), await _context.Transactions.Select(t => t.CompanyId).Distinct().CountAsync()),
                new("Invoices", await _context.Invoices.CountAsync(), await _context.Invoices.Select(i => i.CompanyId).Distinct().CountAsync()),
                new("Expenses", await _context.Expenses.CountAsync(), await _context.Expenses.Select(e => e.CompanyId).Distinct().CountAsync()),
                new("Reconciliation", await _context.Reconciliations.CountAsync(), await _context.Reconciliations.Select(r => r.CompanyId).Distinct().CountAsync())
            };

            var overview = new AnalyticsOverviewDto(
                dailyActiveUsers, weeklyActiveUsers, monthlyActiveUsers, 1800, // 30 min avg session
                featureUsage,
                new List<GrowthDataPointDto>(),
                new List<GrowthDataPointDto>(),
                new List<GrowthDataPointDto>()
            );

            return new ApiResponse<AnalyticsOverviewDto>(true, "Analytics retrieved", overview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting analytics overview");
            return new ApiResponse<AnalyticsOverviewDto>(false, "Error retrieving analytics", null!);
        }
    }

    // ==================== AUDIT LOGS ====================

    public async Task<ApiResponse<PagedResult<AdminAuditLogDto>>> GetAuditLogsExtendedAsync(
        int page, int pageSize, AuditLogFilterDto? filter = null)
    {
        try
        {
            var query = _context.AuditLogs.AsQueryable();

            if (filter != null)
            {
                if (filter.CompanyId.HasValue)
                    query = query.Where(l => l.CompanyId == filter.CompanyId);
                if (filter.UserId.HasValue)
                    query = query.Where(l => l.UserId == filter.UserId);
                if (!string.IsNullOrWhiteSpace(filter.Action))
                    query = query.Where(l => l.Action.ToString() == filter.Action);
                if (!string.IsNullOrWhiteSpace(filter.EntityType))
                    query = query.Where(l => l.EntityType == filter.EntityType);
                if (filter.StartDate.HasValue)
                    query = query.Where(l => l.CreatedAt >= filter.StartDate.Value);
                if (filter.EndDate.HasValue)
                    query = query.Where(l => l.CreatedAt <= filter.EndDate.Value);
            }

            var totalCount = await query.CountAsync();
            var logs = await query
                .OrderByDescending(l => l.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var companyIds = logs.Where(l => l.CompanyId.HasValue).Select(l => l.CompanyId!.Value).Distinct();
            var userIds = logs.Where(l => l.UserId.HasValue).Select(l => l.UserId!.Value).Distinct();

            var companies = await _context.Companies.Where(c => companyIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);
            var users = await _context.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName);

            var dtos = logs.Select(l => new AdminAuditLogDto(
                l.Id, l.CompanyId,
                l.CompanyId.HasValue && companies.ContainsKey(l.CompanyId.Value) ? companies[l.CompanyId.Value] : null,
                l.UserId,
                l.UserId.HasValue && users.ContainsKey(l.UserId.Value) ? users[l.UserId.Value] : null,
                l.Action, l.EntityType, l.EntityId, l.OldValues, l.NewValues, l.CreatedAt
            )).ToList();

            return new ApiResponse<PagedResult<AdminAuditLogDto>>(true, "Audit logs retrieved",
                new PagedResult<AdminAuditLogDto>(dtos, totalCount, page, pageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting audit logs");
            return new ApiResponse<PagedResult<AdminAuditLogDto>>(false, "Error retrieving audit logs", null!);
        }
    }

    public async Task<ApiResponse<byte[]>> ExportAuditLogsAsync(AuditLogFilterDto? filter = null)
    {
        try
        {
            var response = await GetAuditLogsExtendedAsync(1, 10000, filter);
            if (!response.Success || response.Data == null)
                return new ApiResponse<byte[]>(false, "Error exporting logs", null!);

            var csv = new StringBuilder();
            csv.AppendLine("Timestamp,User,Company,Action,Entity,Details,IP Address");

            foreach (var log in response.Data.Items)
            {
                csv.AppendLine($"\"{log.CreatedAt:yyyy-MM-dd HH:mm:ss}\",\"{log.UserName ?? "System"}\",\"{log.CompanyName ?? "Platform"}\",\"{log.Action}\",\"{log.EntityType}\",\"{log.NewValues ?? ""}\",\"\"");
            }

            return new ApiResponse<byte[]>(true, "Export complete", Encoding.UTF8.GetBytes(csv.ToString()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting audit logs");
            return new ApiResponse<byte[]>(false, "Error exporting logs", null!);
        }
    }
}