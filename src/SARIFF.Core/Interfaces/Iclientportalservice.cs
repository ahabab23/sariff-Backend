
using SARIFF.Core.DTOs;
using SARIFF.Core.Enums;

namespace SARIFF.Core.Interfaces;

public interface IClientPortalService
{
    // Dashboard
    Task<ApiResponse<ClientDashboardDto>> GetDashboardAsync(Guid companyId, Guid clientId);
    
    // Profile
    Task<ApiResponse<ClientProfileDto>> GetProfileAsync(Guid companyId, Guid clientId);
    Task<ApiResponse<ClientProfileDto>> UpdateProfileAsync(Guid companyId, Guid clientId, UpdateClientProfileDto dto);
    
    // Transactions
    Task<ApiResponse<PagedResult<ClientTransactionDto>>> GetTransactionsAsync(
        Guid companyId, Guid clientId, int page, int pageSize, TransactionFilters filters);
    Task<ApiResponse<ClientTransactionDto>> GetTransactionByIdAsync(Guid companyId, Guid clientId, Guid transactionId);
    Task<byte[]> GenerateTransactionReceiptAsync(Guid companyId, Guid clientId, Guid transactionId);
    
    // Statement
    Task<ApiResponse<ClientStatementDto>> GetStatementAsync(
        Guid companyId, Guid clientId, DateTime startDate, DateTime endDate, Currency? currency);
    Task<byte[]> GenerateStatementPdfAsync(
        Guid companyId, Guid clientId, DateTime startDate, DateTime endDate, Currency? currency);
    Task<byte[]> ExportTransactionsCsvAsync(
        Guid companyId, Guid clientId, DateTime? startDate, DateTime? endDate, Currency? currency);
    
    // Alerts
    Task<ApiResponse<PagedResult<ClientAlertDto>>> GetAlertsAsync(
        Guid companyId, Guid clientId, int page, int pageSize, bool unreadOnly);
    Task<ApiResponse<bool>> MarkAlertAsReadAsync(Guid companyId, Guid clientId, Guid alertId);
    Task<ApiResponse<bool>> MarkAllAlertsAsReadAsync(Guid companyId, Guid clientId);
    Task<ApiResponse<int>> GetUnreadCountAsync(Guid companyId, Guid clientId);
    
    // Analytics
    Task<ApiResponse<ClientAnalyticsDto>> GetAnalyticsAsync(Guid companyId, Guid clientId, int months);
    
    // Security
    Task<ApiResponse<bool>> ChangePasswordAsync(Guid companyId, Guid clientId, ChangePasswordDto dto);
}

// ============ CLIENT PORTAL DTOs ============

public class ClientDashboardDto
{
    public ClientProfileDto Profile { get; set; } = null!;
    public List<ClientTransactionDto> RecentTransactions { get; set; } = new();
    public List<ClientAlertDto> RecentAlerts { get; set; } = new();
    public QuickStatsDto QuickStats { get; set; } = null!;
}

public class QuickStatsDto
{
    public decimal ThisMonthInKES { get; set; }
    public decimal ThisMonthOutKES { get; set; }
    public decimal ThisMonthInUSD { get; set; }
    public decimal ThisMonthOutUSD { get; set; }
    public int TransactionCount { get; set; }
    public int UnreadAlerts { get; set; }
}

public class ClientProfileDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string WhatsAppNumber { get; set; } = string.Empty;
    public string? IdPassport { get; set; }
    public ClientType ClientType { get; set; }
    public decimal BalanceKES { get; set; }
    public decimal BalanceUSD { get; set; }
    public decimal OpeningBalanceKES { get; set; }
    public decimal OpeningBalanceUSD { get; set; }
    public decimal TotalInKES { get; set; }
    public decimal TotalOutKES { get; set; }
    public decimal TotalInUSD { get; set; }
    public decimal TotalOutUSD { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

public class UpdateClientProfileDto
{
    public string? Email { get; set; }
    public string? WhatsAppNumber { get; set; }
}

public class ClientTransactionDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string Time { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;  // "Credit" or "Debit" from client's perspective
    public TransactionType TransactionType { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public Currency Currency { get; set; }
    public decimal BalanceBefore { get; set; }
    public decimal BalanceAfter { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public ReconciliationStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    
    // Counter party info
    public string? CounterAccountName { get; set; }
    public AccountType? CounterAccountType { get; set; }
    
    // Forex info
    public decimal? ExchangeRate { get; set; }
    public decimal? CounterAmount { get; set; }
    public Currency? CounterCurrency { get; set; }
    
    // Reversal status
    public bool IsReversed { get; set; }   // Original that was reversed
    public bool IsReversal { get; set; }   // The reversal entry itself
}

public class ClientStatementDto
{
    public string AccountName { get; set; } = string.Empty;
    public string AccountCode { get; set; } = string.Empty;
    public Currency Currency { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal ClosingBalance { get; set; }
    public decimal TotalCredits { get; set; }
    public decimal TotalDebits { get; set; }
    public decimal NetMovement { get; set; }
    public List<ClientTransactionDto> Transactions { get; set; } = new();
}

public class ClientAlertDto
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "info";  // success, info, warning, error
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? RelatedTransactionId { get; set; }
}

public class ClientAnalyticsDto
{
    public List<MonthlyDataDto> MonthlyData { get; set; } = new();
    public List<CategoryBreakdownDto> CategoryBreakdown { get; set; } = new();
    public List<WeeklyActivityDto> WeeklyActivity { get; set; } = new();
    public AnalyticsTotalsDto Totals { get; set; } = null!;
}

public class MonthlyDataDto
{
    public string Month { get; set; } = string.Empty;
    public decimal IncomeKES { get; set; }
    public decimal ExpensesKES { get; set; }
    public decimal IncomeUSD { get; set; }
    public decimal ExpensesUSD { get; set; }
    public decimal BalanceKES { get; set; }
    public decimal BalanceUSD { get; set; }
}

public class CategoryBreakdownDto
{
    public string Category { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal TotalKES { get; set; }
    public decimal TotalUSD { get; set; }
    public decimal Percentage { get; set; }
}

public class WeeklyActivityDto
{
    public string Day { get; set; } = string.Empty;
    public int Transactions { get; set; }
}

public class AnalyticsTotalsDto
{
    public int TotalTransactions { get; set; }
    public decimal AvgTransactionKES { get; set; }
    public decimal AvgTransactionUSD { get; set; }
    public decimal NetIncomeKES { get; set; }
    public decimal NetIncomeUSD { get; set; }
    public decimal GrowthPercentage { get; set; }
}

public class ChangePasswordDto
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class TransactionFilters
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public Currency? Currency { get; set; }
    public TransactionType? Type { get; set; }
    public string? Search { get; set; }
}