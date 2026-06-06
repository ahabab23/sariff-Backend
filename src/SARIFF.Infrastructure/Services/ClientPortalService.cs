
using Microsoft.EntityFrameworkCore;
using SARIFF.Core.DTOs;
using SARIFF.Core.Entities;
using SARIFF.Core.Enums;
using SARIFF.Core.Interfaces;
using SARIFF.Infrastructure.Data;
using System.Globalization;
using System.Text;

namespace SARIFF.Infrastructure.Services;

public class ClientPortalService : IClientPortalService
{
    private readonly AppDbContext _context;

    public ClientPortalService(AppDbContext context)
    {
        _context = context;
    }

    #region Dashboard

    public async Task<ApiResponse<ClientDashboardDto>> GetDashboardAsync(Guid companyId, Guid clientId)
    {
        var client = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == clientId && u.CompanyId == companyId && 
                                      u.Role == UserRole.Client && !u.IsDeleted);

        if (client == null)
            return new ApiResponse<ClientDashboardDto>(false, "Client not found", null);

        // Get profile with totals
        var profile = await BuildProfileAsync(companyId, client);

        // Recent transactions (last 10)
        var recentTxns = await GetClientTransactionsQuery(companyId, clientId)
            .OrderByDescending(t => t.TransactionDate)
            .ThenByDescending(t => t.CreatedAt)
            .Take(10)
            .ToListAsync();

        var recentTransactions = recentTxns.Select(t => MapToClientTransactionDto(t, clientId)).ToList();

        // Recent alerts (last 5)
        var recentAlerts = await _context.Set<ClientAlert>()
            .Where(a => a.ClientId == clientId && a.CompanyId == companyId && !a.IsDeleted)
            .OrderByDescending(a => a.CreatedAt)
            .Take(5)
            .Select(a => new ClientAlertDto
            {
                Id = a.Id,
                Type = a.Type,
                Title = a.Title,
                Message = a.Message,
                IsRead = a.IsRead,
                CreatedAt = a.CreatedAt,
                RelatedTransactionId = a.RelatedTransactionId
            })
            .ToListAsync();

        // Quick stats for this month
        var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthTxns = await GetClientTransactionsQuery(companyId, clientId)
            .Where(t => t.TransactionDate >= startOfMonth)
            .ToListAsync();

        var (inKES, outKES, inUSD, outUSD) = CalculateInOut(monthTxns, clientId);
        
        var unreadCount = await _context.Set<ClientAlert>()
            .CountAsync(a => a.ClientId == clientId && a.CompanyId == companyId && !a.IsDeleted && !a.IsRead);

        var quickStats = new QuickStatsDto
        {
            ThisMonthInKES = inKES,
            ThisMonthOutKES = outKES,
            ThisMonthInUSD = inUSD,
            ThisMonthOutUSD = outUSD,
            TransactionCount = await GetClientTransactionsQuery(companyId, clientId).CountAsync(),
            UnreadAlerts = unreadCount
        };

        return new ApiResponse<ClientDashboardDto>(true, "Success", new ClientDashboardDto
        {
            Profile = profile,
            RecentTransactions = recentTransactions,
            RecentAlerts = recentAlerts,
            QuickStats = quickStats
        });
    }

    #endregion

    #region Profile

    public async Task<ApiResponse<ClientProfileDto>> GetProfileAsync(Guid companyId, Guid clientId)
    {
        var client = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == clientId && u.CompanyId == companyId && 
                                      u.Role == UserRole.Client && !u.IsDeleted);

        if (client == null)
            return new ApiResponse<ClientProfileDto>(false, "Client not found", null);

        return new ApiResponse<ClientProfileDto>(true, "Success", await BuildProfileAsync(companyId, client));
    }

    public async Task<ApiResponse<ClientProfileDto>> UpdateProfileAsync(Guid companyId, Guid clientId, UpdateClientProfileDto dto)
    {
        var client = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == clientId && u.CompanyId == companyId && 
                                      u.Role == UserRole.Client && !u.IsDeleted);

        if (client == null)
            return new ApiResponse<ClientProfileDto>(false, "Client not found", null);

        if (!string.IsNullOrWhiteSpace(dto.Email))
        {
            var emailCheck = ValidationHelper.ValidateEmail(dto.Email);
            if (!emailCheck.IsValid)
                return new ApiResponse<ClientProfileDto>(false, emailCheck.Error!, null);
            client.Email = dto.Email;
        }

        if (!string.IsNullOrWhiteSpace(dto.WhatsAppNumber))
        {
            var phoneCheck = ValidationHelper.ValidatePhone(dto.WhatsAppNumber, "WhatsApp number");
            if (!phoneCheck.IsValid)
                return new ApiResponse<ClientProfileDto>(false, phoneCheck.Error!, null);

            // Check if another client has this number
            var exists = await _context.Users.AnyAsync(u => 
                u.Id != clientId && u.CompanyId == companyId && 
                u.WhatsAppNumber == dto.WhatsAppNumber && !u.IsDeleted);
            
            if (exists)
                return new ApiResponse<ClientProfileDto>(false, "WhatsApp number already in use", null);
            
            client.WhatsAppNumber = dto.WhatsAppNumber;
        }

        await _context.SaveChangesAsync();

        return new ApiResponse<ClientProfileDto>(true, "Profile updated", await BuildProfileAsync(companyId, client));
    }

    #endregion

    #region Transactions

    public async Task<ApiResponse<PagedResult<ClientTransactionDto>>> GetTransactionsAsync(
        Guid companyId, Guid clientId, int page, int pageSize, TransactionFilters filters)
    {
        // Ensure dates are UTC for PostgreSQL
        if (filters.StartDate.HasValue)
            filters.StartDate = DateTime.SpecifyKind(filters.StartDate.Value, DateTimeKind.Utc);
        if (filters.EndDate.HasValue)
            filters.EndDate = DateTime.SpecifyKind(filters.EndDate.Value, DateTimeKind.Utc);

        var query = GetClientTransactionsQuery(companyId, clientId);

        // Apply filters
        if (filters.StartDate.HasValue)
            query = query.Where(t => t.TransactionDate >= filters.StartDate.Value);

        if (filters.EndDate.HasValue)
            query = query.Where(t => t.TransactionDate <= filters.EndDate.Value.AddDays(1));

        if (filters.Currency.HasValue)
            query = query.Where(t => t.Currency == filters.Currency.Value);  // FIX #12: Only match primary currency

        if (filters.Type.HasValue)
            query = query.Where(t => t.TransactionType == filters.Type.Value);

        if (!string.IsNullOrWhiteSpace(filters.Search))
        {
            var search = filters.Search.ToLower();
            query = query.Where(t =>
                t.Description.ToLower().Contains(search) ||
                t.Reference.ToLower().Contains(search) ||
                t.Code.ToLower().Contains(search));
        }

        var totalCount = await query.CountAsync();

        // FIX: Compute running balances dynamically
        // Get all matching transactions up to end of current page (chronological order)
        var allUpToPage = await query
            .OrderBy(t => t.TransactionDate)
            .ThenBy(t => t.CreatedAt)
            .Take(page * pageSize)  // Only need up to end of current page
            .ToListAsync();

        var allMapped = allUpToPage.Select(t => MapToClientTransactionDto(t, clientId)).ToList();

        // Get client opening balances
        var client = await _context.Users.FindAsync(clientId);
        var kesBalance = client?.OpeningBalanceKES ?? 0;
        var usdBalance = client?.OpeningBalanceUSD ?? 0;

        // If there are filters, we also need to account for transactions BEFORE the filtered period
        if (filters.StartDate.HasValue)
        {
            var priorKes = await GetClientTransactionsQuery(companyId, clientId)
                .Where(t => t.TransactionDate < filters.StartDate.Value && t.Currency == Currency.KES)
                .ToListAsync();
            foreach (var t in priorKes)
            {
                var dto = MapToClientTransactionDto(t, clientId);
                kesBalance += dto.Type == "Credit" ? dto.Amount : -dto.Amount;
            }

            var priorUsd = await GetClientTransactionsQuery(companyId, clientId)
                .Where(t => t.TransactionDate < filters.StartDate.Value && t.Currency == Currency.USD)
                .ToListAsync();
            foreach (var t in priorUsd)
            {
                var dto = MapToClientTransactionDto(t, clientId);
                usdBalance += dto.Type == "Credit" ? dto.Amount : -dto.Amount;
            }
        }

        // Compute running balances through all items up to current page
        foreach (var item in allMapped)
        {
            if (item.Currency == Currency.KES)
            {
                item.BalanceBefore = kesBalance;
                kesBalance += item.Type == "Credit" ? item.Amount : -item.Amount;
                item.BalanceAfter = kesBalance;
            }
            else
            {
                item.BalanceBefore = usdBalance;
                usdBalance += item.Type == "Credit" ? item.Amount : -item.Amount;
                item.BalanceAfter = usdBalance;
            }
        }

        // Extract only current page items, reversed for newest-first display
        var skipCount = (page - 1) * pageSize;
        var pageItems = allMapped.Skip(skipCount).Take(pageSize).Reverse().ToList();

        return new ApiResponse<PagedResult<ClientTransactionDto>>(true, "Success",
            new PagedResult<ClientTransactionDto>(pageItems, totalCount, page, pageSize));
    }

    public async Task<ApiResponse<ClientTransactionDto>> GetTransactionByIdAsync(Guid companyId, Guid clientId, Guid transactionId)
    {
        var transaction = await GetClientTransactionsQuery(companyId, clientId)
            .FirstOrDefaultAsync(t => t.Id == transactionId);

        if (transaction == null)
            return new ApiResponse<ClientTransactionDto>(false, "Transaction not found", null);

        return new ApiResponse<ClientTransactionDto>(true, "Success", MapToClientTransactionDto(transaction, clientId));
    }

    public async Task<byte[]> GenerateTransactionReceiptAsync(Guid companyId, Guid clientId, Guid transactionId)
    {
        var transaction = await GetClientTransactionsQuery(companyId, clientId)
            .FirstOrDefaultAsync(t => t.Id == transactionId);

        if (transaction == null)
            throw new Exception("Transaction not found");

        var client = await _context.Users.FindAsync(clientId);
        var dto = MapToClientTransactionDto(transaction, clientId);

        var sb = new StringBuilder();
        sb.AppendLine("=====================================");
        sb.AppendLine("         TRANSACTION RECEIPT");
        sb.AppendLine("=====================================");
        sb.AppendLine();
        sb.AppendLine($"Receipt No: {transaction.Code}");
        sb.AppendLine($"Date: {transaction.TransactionDate:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"Client: {client?.FullName}");
        sb.AppendLine($"Client ID: {client?.Code}");
        sb.AppendLine();
        sb.AppendLine("-------------------------------------");
        sb.AppendLine($"Type: {dto.Type}");
        sb.AppendLine($"Description: {transaction.Description}");
        sb.AppendLine($"Reference: {transaction.Reference}");
        sb.AppendLine();
        sb.AppendLine($"Amount: {dto.Currency} {dto.Amount:N2}");
        sb.AppendLine($"Balance Before: {dto.Currency} {dto.BalanceBefore:N2}");
        sb.AppendLine($"Balance After: {dto.Currency} {dto.BalanceAfter:N2}");
        sb.AppendLine();
        if (dto.ExchangeRate.HasValue)
        {
            sb.AppendLine($"Exchange Rate: {dto.ExchangeRate:N4}");
            sb.AppendLine($"Counter Amount: {dto.CounterCurrency} {dto.CounterAmount:N2}");
            sb.AppendLine();
        }
        sb.AppendLine($"Status: {transaction.ReconciliationStatus}");
        sb.AppendLine();
        sb.AppendLine("-------------------------------------");
        sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine("=====================================");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    #endregion

    #region Statement

    public async Task<ApiResponse<ClientStatementDto>> GetStatementAsync(
        Guid companyId, Guid clientId, DateTime startDate, DateTime endDate, Currency? currency)
    {
        // Ensure dates are UTC for PostgreSQL
        startDate = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
        endDate = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

        var client = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == clientId && u.CompanyId == companyId && 
                                      u.Role == UserRole.Client && !u.IsDeleted);

        if (client == null)
            return new ApiResponse<ClientStatementDto>(false, "Client not found", null);

        var query = GetClientTransactionsQuery(companyId, clientId)
            .Where(t => t.TransactionDate >= startDate && t.TransactionDate <= endDate.AddDays(1));

        if (currency.HasValue)
            query = query.Where(t => t.Currency == currency.Value);  // FIX #12: Only match primary currency

        // Sort chronologically (oldest first) for running balance computation
        var transactions = await query
            .OrderBy(t => t.TransactionDate)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync();

        var statementCurrency = currency ?? Currency.KES;
        var items = transactions.Select(t => MapToClientTransactionDto(t, clientId)).ToList();

        // Compute opening balances for BOTH currencies
        var openingKES = client.OpeningBalanceKES;
        var openingUSD = client.OpeningBalanceUSD;
        
        // Sum all transactions BEFORE the start date to get correct period opening balances
        var priorTransactions = await GetClientTransactionsQuery(companyId, clientId)
            .Where(t => t.TransactionDate < startDate)
            .ToListAsync();
        
        var priorItems = priorTransactions.Select(t => MapToClientTransactionDto(t, clientId)).ToList();
        var periodOpeningKES = openingKES;
        var periodOpeningUSD = openingUSD;
        foreach (var item in priorItems)
        {
            var delta = item.Type == "Credit" ? item.Amount : -item.Amount;
            if (item.Currency == Currency.KES)
                periodOpeningKES += delta;
            else if (item.Currency == Currency.USD)
                periodOpeningUSD += delta;
        }

        // Compute running balances PER CURRENCY
        decimal totalCredits = 0, totalDebits = 0;
        var runningKES = periodOpeningKES;
        var runningUSD = periodOpeningUSD;
        
        foreach (var item in items)
        {
            if (item.Currency == Currency.KES)
            {
                item.BalanceBefore = runningKES;
                if (item.Type == "Credit")
                {
                    runningKES += item.Amount;
                    if (statementCurrency == Currency.KES) totalCredits += item.Amount;
                }
                else
                {
                    runningKES -= item.Amount;
                    if (statementCurrency == Currency.KES) totalDebits += item.Amount;
                }
                item.BalanceAfter = runningKES;
            }
            else if (item.Currency == Currency.USD)
            {
                item.BalanceBefore = runningUSD;
                if (item.Type == "Credit")
                {
                    runningUSD += item.Amount;
                    if (statementCurrency == Currency.USD) totalCredits += item.Amount;
                }
                else
                {
                    runningUSD -= item.Amount;
                    if (statementCurrency == Currency.USD) totalDebits += item.Amount;
                }
                item.BalanceAfter = runningUSD;
            }
        }

        var closingBalance = statementCurrency == Currency.KES ? runningKES : runningUSD;
        var periodOpeningBalance = statementCurrency == Currency.KES ? periodOpeningKES : periodOpeningUSD;

        // Reverse to show newest first for display
        items.Reverse();

        return new ApiResponse<ClientStatementDto>(true, "Success", new ClientStatementDto
        {
            AccountName = client.FullName,
            AccountCode = client.Code,
            Currency = statementCurrency,
            PeriodStart = startDate,
            PeriodEnd = endDate,
            OpeningBalance = periodOpeningBalance,
            ClosingBalance = closingBalance,
            TotalCredits = totalCredits,
            TotalDebits = totalDebits,
            NetMovement = closingBalance - periodOpeningBalance,
            Transactions = items
        });
    }

    public async Task<byte[]> GenerateStatementPdfAsync(
        Guid companyId, Guid clientId, DateTime startDate, DateTime endDate, Currency? currency)
    {
        var result = await GetStatementAsync(companyId, clientId, startDate, endDate, currency);
        if (!result.Success || result.Data == null)
            throw new Exception(result.Message);

        var statement = result.Data;
        var sb = new StringBuilder();

        sb.AppendLine("=====================================");
        sb.AppendLine("         ACCOUNT STATEMENT");
        sb.AppendLine("=====================================");
        sb.AppendLine();
        sb.AppendLine($"Client: {statement.AccountName}");
        sb.AppendLine($"Client ID: {statement.AccountCode}");
        sb.AppendLine($"Currency: {statement.Currency}");
        sb.AppendLine($"Period: {statement.PeriodStart:yyyy-MM-dd} to {statement.PeriodEnd:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine($"Opening Balance: {statement.Currency} {statement.OpeningBalance:N2}");
        sb.AppendLine();
        sb.AppendLine("TRANSACTIONS");
        sb.AppendLine("-------------------------------------");
        sb.AppendLine("Date       | Type   | Amount      | Balance     | Description");
        sb.AppendLine("-------------------------------------");

        foreach (var txn in statement.Transactions)
        {
            var typeStr = txn.Type == "Credit" ? "CR" : "DR";
            sb.AppendLine($"{txn.Date:MM/dd/yyyy} | {typeStr,-6} | {txn.Amount,11:N2} | {txn.BalanceAfter,11:N2} | {txn.Description}");
        }

        sb.AppendLine("-------------------------------------");
        sb.AppendLine();
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"Total Credits: {statement.Currency} {statement.TotalCredits:N2}");
        sb.AppendLine($"Total Debits:  {statement.Currency} {statement.TotalDebits:N2}");
        sb.AppendLine($"Net Movement:  {statement.Currency} {statement.NetMovement:N2}");
        sb.AppendLine();
        sb.AppendLine($"Closing Balance: {statement.Currency} {statement.ClosingBalance:N2}");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine("=====================================");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public async Task<byte[]> ExportTransactionsCsvAsync(
        Guid companyId, Guid clientId, DateTime? startDate, DateTime? endDate, Currency? currency)
    {
        // Ensure dates are UTC for PostgreSQL
        if (startDate.HasValue)
            startDate = DateTime.SpecifyKind(startDate.Value, DateTimeKind.Utc);
        if (endDate.HasValue)
            endDate = DateTime.SpecifyKind(endDate.Value, DateTimeKind.Utc);

        var query = GetClientTransactionsQuery(companyId, clientId);

        if (startDate.HasValue)
            query = query.Where(t => t.TransactionDate >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(t => t.TransactionDate <= endDate.Value.AddDays(1));

        if (currency.HasValue)
            query = query.Where(t => t.Currency == currency.Value);  // FIX #12: Only match primary currency

        var transactions = await query
            .OrderByDescending(t => t.TransactionDate)
            .ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("Code,Date,Time,Type,Description,Amount,Currency,Balance Before,Balance After,Reference,Status");

        foreach (var txn in transactions)
        {
            var dto = MapToClientTransactionDto(txn, clientId);
            sb.AppendLine($"\"{dto.Code}\",\"{dto.Date:yyyy-MM-dd}\",\"{dto.Time}\",\"{dto.Type}\",\"{dto.Description}\",{dto.Amount},{dto.Currency},{dto.BalanceBefore},{dto.BalanceAfter},\"{dto.Reference}\",\"{dto.Status}\"");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    #endregion

    #region Alerts

    public async Task<ApiResponse<PagedResult<ClientAlertDto>>> GetAlertsAsync(
        Guid companyId, Guid clientId, int page, int pageSize, bool unreadOnly)
    {
        var query = _context.Set<ClientAlert>()
            .Where(a => a.ClientId == clientId && a.CompanyId == companyId && !a.IsDeleted);

        if (unreadOnly)
            query = query.Where(a => !a.IsRead);

        var totalCount = await query.CountAsync();

        var alerts = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new ClientAlertDto
            {
                Id = a.Id,
                Type = a.Type,
                Title = a.Title,
                Message = a.Message,
                IsRead = a.IsRead,
                CreatedAt = a.CreatedAt,
                RelatedTransactionId = a.RelatedTransactionId
            })
            .ToListAsync();

        return new ApiResponse<PagedResult<ClientAlertDto>>(true, "Success",
            new PagedResult<ClientAlertDto>(alerts, totalCount, page, pageSize));
    }

    public async Task<ApiResponse<bool>> MarkAlertAsReadAsync(Guid companyId, Guid clientId, Guid alertId)
    {
        var alert = await _context.Set<ClientAlert>()
            .FirstOrDefaultAsync(a => a.Id == alertId && a.ClientId == clientId && 
                                      a.CompanyId == companyId && !a.IsDeleted);

        if (alert == null)
            return new ApiResponse<bool>(false, "Alert not found", false);

        alert.IsRead = true;
        alert.ReadAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return new ApiResponse<bool>(true, "Alert marked as read", true);
    }

    public async Task<ApiResponse<bool>> MarkAllAlertsAsReadAsync(Guid companyId, Guid clientId)
    {
        var alerts = await _context.Set<ClientAlert>()
            .Where(a => a.ClientId == clientId && a.CompanyId == companyId && !a.IsDeleted && !a.IsRead)
            .ToListAsync();

        foreach (var alert in alerts)
        {
            alert.IsRead = true;
            alert.ReadAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        return new ApiResponse<bool>(true, $"{alerts.Count} alerts marked as read", true);
    }

    public async Task<ApiResponse<int>> GetUnreadCountAsync(Guid companyId, Guid clientId)
    {
        var count = await _context.Set<ClientAlert>()
            .CountAsync(a => a.ClientId == clientId && a.CompanyId == companyId && !a.IsDeleted && !a.IsRead);

        return new ApiResponse<int>(true, "Success", count);
    }

    #endregion

    #region Analytics

    public async Task<ApiResponse<ClientAnalyticsDto>> GetAnalyticsAsync(Guid companyId, Guid clientId, int months)
    {
        // Ensure UTC for PostgreSQL
        var now = DateTime.UtcNow;
        var startDate = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-months);
        
        var transactions = await GetClientTransactionsQuery(companyId, clientId)
            .Where(t => t.TransactionDate >= startDate)
            .ToListAsync();

        var client = await _context.Users.FindAsync(clientId);

        // Monthly data
        var monthlyData = new List<MonthlyDataDto>();
        for (int i = months - 1; i >= 0; i--)
        {
            var monthStart = DateTime.UtcNow.AddMonths(-i);
            var monthName = monthStart.ToString("MMM", CultureInfo.InvariantCulture);

            var monthTxns = transactions.Where(t =>
                t.TransactionDate.Year == monthStart.Year &&
                t.TransactionDate.Month == monthStart.Month).ToList();

            var (inKES, outKES, inUSD, outUSD) = CalculateInOut(monthTxns, clientId);

            monthlyData.Add(new MonthlyDataDto
            {
                Month = monthName,
                IncomeKES = inKES,
                ExpensesKES = outKES,
                IncomeUSD = inUSD,
                ExpensesUSD = outUSD,
                BalanceKES = client?.BalanceKES ?? 0,
                BalanceUSD = client?.BalanceUSD ?? 0
            });
        }

        // Category breakdown by TransactionType
        var categoryGroups = transactions
            .GroupBy(t => t.TransactionType)
            .Select(g => new CategoryBreakdownDto
            {
                Category = g.Key.ToString(),
                Count = g.Count(),
                TotalKES = g.Where(t => t.Currency == Currency.KES).Sum(t => t.Amount),
                TotalUSD = g.Where(t => t.Currency == Currency.USD).Sum(t => t.Amount),
                Percentage = transactions.Count > 0 ? (decimal)g.Count() / transactions.Count * 100 : 0
            })
            .OrderByDescending(c => c.Count)
            .ToList();

        // Weekly activity (last 7 days)
        var weeklyActivity = new List<WeeklyActivityDto>();
        var dayNames = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        var todayUtc = DateTime.UtcNow.Date;
        for (int i = 6; i >= 0; i--)
        {
            var date = todayUtc.AddDays(-i);
            weeklyActivity.Add(new WeeklyActivityDto
            {
                Day = dayNames[(int)date.DayOfWeek],
                Transactions = transactions.Count(t => t.TransactionDate.Date == date)
            });
        }

        // Totals
        var (totalInKES, totalOutKES, totalInUSD, totalOutUSD) = CalculateInOut(transactions, clientId);

        // Growth calculation
        var thisMonth = DateTime.UtcNow;
        var lastMonth = thisMonth.AddMonths(-1);

        var thisMonthTxns = transactions.Where(t => 
            t.TransactionDate.Year == thisMonth.Year && t.TransactionDate.Month == thisMonth.Month);
        var lastMonthTxns = transactions.Where(t => 
            t.TransactionDate.Year == lastMonth.Year && t.TransactionDate.Month == lastMonth.Month);

        var thisMonthTotal = thisMonthTxns.Sum(t => t.Amount);
        var lastMonthTotal = lastMonthTxns.Sum(t => t.Amount);

        var growthPercentage = lastMonthTotal > 0
            ? ((thisMonthTotal - lastMonthTotal) / lastMonthTotal) * 100
            : 0;

        var kesTxns = transactions.Where(t => t.Currency == Currency.KES).ToList();
        var usdTxns = transactions.Where(t => t.Currency == Currency.USD).ToList();

        return new ApiResponse<ClientAnalyticsDto>(true, "Success", new ClientAnalyticsDto
        {
            MonthlyData = monthlyData,
            CategoryBreakdown = categoryGroups,
            WeeklyActivity = weeklyActivity,
            Totals = new AnalyticsTotalsDto
            {
                TotalTransactions = transactions.Count,
                AvgTransactionKES = kesTxns.Any() ? kesTxns.Average(t => t.Amount) : 0,
                AvgTransactionUSD = usdTxns.Any() ? usdTxns.Average(t => t.Amount) : 0,
                NetIncomeKES = totalInKES - totalOutKES,
                NetIncomeUSD = totalInUSD - totalOutUSD,
                GrowthPercentage = growthPercentage
            }
        });
    }

    #endregion

    #region Security

    public async Task<ApiResponse<bool>> ChangePasswordAsync(Guid companyId, Guid clientId, ChangePasswordDto dto)
    {
        if (dto.NewPassword != dto.ConfirmPassword)
            return new ApiResponse<bool>(false, "New passwords do not match", false);

        if (dto.NewPassword.Length < 6)
            return new ApiResponse<bool>(false, "Password must be at least 6 characters", false);

        var client = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == clientId && u.CompanyId == companyId && 
                                      u.Role == UserRole.Client && !u.IsDeleted);

        if (client == null)
            return new ApiResponse<bool>(false, "Client not found", false);

        if (client.ClientType != ClientType.Permanent)
            return new ApiResponse<bool>(false, "Only permanent clients can change password", false);

        if (string.IsNullOrEmpty(client.PasswordHash) ||
            !BCrypt.Net.BCrypt.Verify(dto.CurrentPassword, client.PasswordHash))
        {
            return new ApiResponse<bool>(false, "Current password is incorrect", false);
        }

        client.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
        await _context.SaveChangesAsync();

        // Create alert
        var alert = new ClientAlert
        {
            ClientId = clientId,
            CompanyId = companyId,
            Type = "success",
            Title = "Password Changed",
            Message = "Your password has been successfully changed."
        };
        _context.Set<ClientAlert>().Add(alert);
        await _context.SaveChangesAsync();

        return new ApiResponse<bool>(true, "Password changed successfully", true);
    }

    #endregion

    #region Private Helpers

    private IQueryable<Transaction> GetClientTransactionsQuery(Guid companyId, Guid clientId)
    {
        // AccountType.Client = 3 in backend enums
        return _context.Transactions
            .Where(t => t.CompanyId == companyId && !t.IsDeleted &&
                ((t.SourceAccountType == AccountType.Client && t.SourceAccountId == clientId) ||
                 (t.DestAccountType == AccountType.Client && t.DestAccountId == clientId)));
    }

    private ClientTransactionDto MapToClientTransactionDto(Transaction txn, Guid clientId)
    {
        var isSource = txn.SourceAccountType == AccountType.Client && txn.SourceAccountId == clientId;

        // From client's perspective:
        // If client is SOURCE and TransactionType is Debit -> Client is debited (money out)
        // If client is SOURCE and TransactionType is Credit -> Client is credited (money in)
        // If client is DEST and TransactionType is Debit -> Client is credited (money in)
        // If client is DEST and TransactionType is Credit -> Client is debited (money out)
        
        string type;
        decimal amount, balanceBefore, balanceAfter;
        Currency currency;

        if (isSource)
        {
            // Client is the primary account
            type = txn.TransactionType == TransactionType.Debit ? "Debit" : "Credit";
            amount = txn.Amount;
            currency = txn.Currency;
            balanceBefore = txn.SourceBalanceBefore;
            balanceAfter = txn.SourceBalanceAfter;
        }
        else
        {
            // Client is the counter account
            type = txn.TransactionType == TransactionType.Debit ? "Credit" : "Debit";
            amount = txn.CounterAmount ?? txn.Amount;
            currency = txn.CounterCurrency ?? txn.Currency;
            balanceBefore = txn.DestBalanceBefore;
            balanceAfter = txn.DestBalanceAfter;
        }

        return new ClientTransactionDto
        {
            Id = txn.Id,
            Code = txn.Code,
            Date = txn.TransactionDate,
            Time = txn.TransactionDate.ToString("HH:mm"),
            Type = type,
            TransactionType = txn.TransactionType,
            Description = txn.Description,
            Amount = amount,
            Currency = currency,
            BalanceBefore = balanceBefore,
            BalanceAfter = balanceAfter,
            Reference = txn.Reference,
            Notes = txn.Notes,
            Status = txn.ReconciliationStatus,
            CreatedAt = txn.CreatedAt,
            CounterAccountType = isSource ? txn.DestAccountType : txn.SourceAccountType,
            ExchangeRate = txn.ExchangeRate,
            CounterAmount = isSource ? txn.CounterAmount : txn.Amount,
            CounterCurrency = isSource ? txn.CounterCurrency : txn.Currency,
            IsReversed = txn.DeletedAt.HasValue && !txn.IsDeleted,
            IsReversal = txn.Reference.StartsWith("REV-")
        };
    }

    private async Task<ClientProfileDto> BuildProfileAsync(Guid companyId, User client)
    {
        var transactions = await GetClientTransactionsQuery(companyId, client.Id).ToListAsync();
        var (inKES, outKES, inUSD, outUSD) = CalculateInOut(transactions, client.Id);

        return new ClientProfileDto
        {
            Id = client.Id,
            Code = client.Code,
            FullName = client.FullName,
            Email = client.Email,
            WhatsAppNumber = client.WhatsAppNumber,
            IdPassport = client.IdPassport,
            ClientType = client.ClientType ?? ClientType.Temporary,
            BalanceKES = client.BalanceKES,
            BalanceUSD = client.BalanceUSD,
            OpeningBalanceKES = client.OpeningBalanceKES,
            OpeningBalanceUSD = client.OpeningBalanceUSD,
            TotalInKES = inKES,
            TotalOutKES = outKES,
            TotalInUSD = inUSD,
            TotalOutUSD = outUSD,
            IsActive = client.IsActive,
            CreatedAt = client.CreatedAt,
            LastLoginAt = client.LastLoginAt
        };
    }

    private (decimal inKES, decimal outKES, decimal inUSD, decimal outUSD) CalculateInOut(
        List<Transaction> transactions, Guid clientId)
    {
        decimal inKES = 0, outKES = 0, inUSD = 0, outUSD = 0;

        foreach (var txn in transactions)
        {
            var isSource = txn.SourceAccountType == AccountType.Client && txn.SourceAccountId == clientId;

            decimal amount;
            Currency currency;
            bool isCredit;

            if (isSource)
            {
                amount = txn.Amount;
                currency = txn.Currency;
                isCredit = txn.TransactionType == TransactionType.Credit;
            }
            else
            {
                amount = txn.CounterAmount ?? txn.Amount;
                currency = txn.CounterCurrency ?? txn.Currency;
                isCredit = txn.TransactionType == TransactionType.Debit;
            }

            if (currency == Currency.KES)
            {
                if (isCredit) inKES += amount;
                else outKES += amount;
            }
            else
            {
                if (isCredit) inUSD += amount;
                else outUSD += amount;
            }
        }

        return (inKES, outKES, inUSD, outUSD);
    }

    #endregion
}