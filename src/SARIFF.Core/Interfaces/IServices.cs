using SARIFF.Core.DTOs;
using SARIFF.Core.Enums;

namespace SARIFF.Core.Interfaces;

public interface IAuthService
{
    // UNIFIED LOGIN - One endpoint for ALL users
    Task<ApiResponse<object>> UnifiedLoginAsync(UnifiedLoginDto request, string ipAddress, string? userAgent);
    
    // OTP verification (for SuperAdmin/OfficeUser after login)
    Task<ApiResponse<TokenResponseDto>> VerifyOtpAsync(OtpVerifyWithCodeDto request, string ipAddress, string? userAgent);
    
    // Token management
    Task<ApiResponse<TokenResponseDto>> RefreshTokenAsync(RefreshTokenDto request, string ipAddress);
    Task<ApiResponse<bool>> LogoutAsync(Guid userId);
}

public interface ICompanyService
{
    Task<ApiResponse<CompanyResponseDto>> CreateAsync(CreateCompanyDto dto);
    Task<ApiResponse<CompanyResponseDto>> GetByIdAsync(Guid id);
    Task<ApiResponse<PagedResult<CompanyResponseDto>>> GetAllAsync(int page, int pageSize, string? search = null);
    Task<ApiResponse<CompanyResponseDto>> UpdateAsync(Guid id, UpdateCompanyDto dto);
    Task<ApiResponse<bool>> ActivateAsync(Guid id);
    Task<ApiResponse<bool>> DeactivateAsync(Guid id);
    Task<ApiResponse<bool>> ResetPasswordAsync(Guid id, AdminResetPasswordDto dto);
    Task<ApiResponse<CompanySummaryDto>> GetSummaryAsync(Guid id);
    Task<ApiResponse<List<CompanySummaryDto>>> GetAllSummariesAsync();
}

public interface IClientService
{
    Task<ApiResponse<ClientResponseDto>> CreateAsync(Guid companyId, CreateClientDto dto);
    Task<ApiResponse<ClientResponseDto>> GetByIdAsync(Guid companyId, Guid id);
    Task<ApiResponse<PagedResult<ClientResponseDto>>> GetAllAsync(Guid companyId, int page, int pageSize, string? search = null, string? filter = null);
    Task<ApiResponse<ClientResponseDto>> UpdateAsync(Guid companyId, Guid id, UpdateClientDto dto);
    Task<ApiResponse<bool>> ConvertToPermamentAsync(Guid companyId, Guid id, ConvertClientDto dto);
    Task<ApiResponse<bool>> ResetPasswordAsync(Guid companyId, Guid id, ResetClientPasswordDto dto);
    Task<ApiResponse<bool>> DeleteAsync(Guid companyId, Guid id);
    Task<ApiResponse<ClientStatsDto>> GetStatsAsync(Guid companyId);
    Task<ApiResponse<StatementDto>> GetStatementAsync(Guid companyId, Guid id, StatementFilterDto filter);
    Task<ApiResponse<bool>> ReverseTransactionAsync(Guid companyId, Guid clientId, Guid transactionId, Guid userId, string? reason);
    Task<ApiResponse<List<ClientLookupDto>>> GetLookupAsync(Guid companyId, string? search = null);
}

public interface IBankAccountService
{
    Task<ApiResponse<BankAccountResponseDto>> CreateAsync(Guid companyId, CreateBankAccountDto dto);
    Task<ApiResponse<BankAccountResponseDto>> GetByIdAsync(Guid companyId, Guid id);
    Task<ApiResponse<List<BankAccountResponseDto>>> GetAllAsync(Guid companyId);
    Task<ApiResponse<BankAccountResponseDto>> UpdateAsync(Guid companyId, Guid id, UpdateBankAccountDto dto);
    Task<ApiResponse<bool>> DeleteAsync(Guid companyId, Guid id);
    Task<ApiResponse<BankAccountStatsDto>> GetStatsAsync(Guid companyId);
    Task<ApiResponse<StatementDto>> GetStatementAsync(Guid companyId, Guid id, StatementFilterDto filter);
}

public interface IMpesaAgentService
{
    Task<ApiResponse<MpesaAgentResponseDto>> CreateAsync(Guid companyId, CreateMpesaAgentDto dto);
    Task<ApiResponse<MpesaAgentResponseDto>> GetByIdAsync(Guid companyId, Guid id);
    Task<ApiResponse<List<MpesaAgentResponseDto>>> GetAllAsync(Guid companyId);
    Task<ApiResponse<MpesaAgentResponseDto>> UpdateAsync(Guid companyId, Guid id, UpdateMpesaAgentDto dto);
    Task<ApiResponse<bool>> DeleteAsync(Guid companyId, Guid id);
    Task<ApiResponse<MpesaAgentStatsDto>> GetStatsAsync(Guid companyId);
    Task<ApiResponse<StatementDto>> GetStatementAsync(Guid companyId, Guid id, StatementFilterDto filter);
}

public interface ICashAccountService
{
    // NEW: Create cash account with opening balance
    Task<ApiResponse<CashAccountResponseDto>> CreateAsync(Guid companyId, CreateCashAccountDto dto);
    
    // NEW: Get by ID
    Task<ApiResponse<CashAccountResponseDto>> GetByIdAsync(Guid companyId, Guid id);
    
    // NEW: Get by currency
    Task<ApiResponse<CashAccountResponseDto>> GetByCurrencyAsync(Guid companyId, Currency currency);
    
    // Existing
    Task<ApiResponse<List<CashAccountResponseDto>>> GetAllAsync(Guid companyId);
    
    // NEW: Update opening balance
    Task<ApiResponse<CashAccountResponseDto>> UpdateAsync(Guid companyId, Guid id, UpdateCashAccountDto dto);
    
    // NEW: Delete
    Task<ApiResponse<bool>> DeleteAsync(Guid companyId, Guid id);
    
    // Existing
    Task<ApiResponse<CashStatsDto>> GetStatsAsync(Guid companyId);
    Task<ApiResponse<StatementDto>> GetStatementAsync(Guid companyId, Currency currency, StatementFilterDto filter);
}
public interface ITransactionService
{
    Task<ApiResponse<TransactionResponseDto>> CreateAsync(Guid companyId, Guid userId, CreateTransactionDto dto);
    Task<ApiResponse<TransactionResponseDto>> GetByIdAsync(Guid companyId, Guid id);
    Task<ApiResponse<PagedResult<TransactionResponseDto>>> GetAllAsync(Guid companyId, int page, int pageSize, ReportFilterDto? filter = null);
    Task<ApiResponse<TransactionResponseDto>> UpdateAsync(Guid companyId, Guid id, UpdateTransactionDto dto);
    Task<ApiResponse<bool>> DeleteAsync(Guid companyId, Guid id, Guid userId, DeleteTransactionDto dto, Guid? clientContext = null);
    Task<ApiResponse<TransactionSummaryDto>> GetTodaySummaryAsync(Guid companyId);
    Task<ApiResponse<List<TransactionResponseDto>>> GetRecentAsync(Guid companyId, int count = 10);
}

/// <summary>
/// FIXED Expense Service Interface
/// 
/// Changes:
/// 1. DeleteAsync now requires userId for audit trail
/// </summary>
public interface IExpenseService
{
    // Categories
    Task<ApiResponse<ExpenseCategoryResponseDto>> CreateCategoryAsync(Guid companyId, CreateExpenseCategoryDto dto);
    Task<ApiResponse<List<ExpenseCategoryResponseDto>>> GetCategoriesAsync(Guid companyId);
    Task<ApiResponse<ExpenseCategoryResponseDto>> UpdateCategoryAsync(Guid companyId, Guid id, UpdateExpenseCategoryDto dto);
    Task<ApiResponse<bool>> DeleteCategoryAsync(Guid companyId, Guid id);
    
    // Expenses
    Task<ApiResponse<ExpenseResponseDto>> CreateAsync(Guid companyId, Guid userId, CreateExpenseDto dto);
    Task<ApiResponse<ExpenseResponseDto>> GetByIdAsync(Guid companyId, Guid id);
    Task<ApiResponse<PagedResult<ExpenseResponseDto>>> GetAllAsync(Guid companyId, int page, int pageSize, ReportFilterDto? filter = null);
    Task<ApiResponse<ExpenseStatsDto>> GetStatsAsync(Guid companyId);
    Task<ApiResponse<ExpenseResponseDto>> UpdateAsync(Guid companyId, Guid id, UpdateExpenseDto dto);
    
    // FIXED: DeleteAsync now requires userId for proper audit trail
    Task<ApiResponse<bool>> DeleteAsync(Guid companyId, Guid id, Guid userId);
}

public interface IExchangeRateService
{
    Task<ApiResponse<ExchangeRateResponseDto>> SetRateAsync(Guid companyId, Guid userId, SetExchangeRateDto dto);
    Task<ApiResponse<ExchangeRateResponseDto>> GetCurrentRateAsync(Guid companyId);
    Task<ApiResponse<List<ExchangeRateResponseDto>>> GetHistoryAsync(Guid companyId);
    Task<ApiResponse<CurrencyConvertResultDto>> ConvertAsync(Guid companyId, CurrencyConvertDto dto);
    Task<ApiResponse<TransactionResponseDto>> CreateExchangeTransactionAsync(Guid companyId, Guid userId, ExchangeTransactionDto dto);
}
/// <summary>
/// Enhanced Exchange Service for Forex Bureau Operations
/// Handles float management, exchange transactions, daily operations, and reporting
/// </summary>
public interface IExchangeService
{
    // Rate Management
    Task<ApiResponse<ExchangeRateResponseDto>> SetRateAsync(Guid companyId, Guid userId, SetExchangeRateDto dto);
    Task<ApiResponse<ExchangeRateResponseDto>> GetCurrentRateAsync(Guid companyId);
    Task<ApiResponse<List<ExchangeRateResponseDto>>> GetRateHistoryAsync(Guid companyId, int days = 30);
    
    // Float Management
    Task<ApiResponse<ExchangeFloatDto>> GetFloatAsync(Guid companyId);
    Task<ApiResponse<ExchangeFloatDto>> FundFloatAsync(Guid companyId, Guid userId, FundFloatDto dto);
    Task<ApiResponse<ExchangeFloatDto>> WithdrawFloatAsync(Guid companyId, Guid userId, WithdrawFloatDto dto);
    Task<ApiResponse<ExchangeFloatDto>> SettleProfitAsync(Guid companyId, Guid userId, SettleProfitDto dto);
    Task<ApiResponse<List<FloatMovementDto>>> GetFloatMovementsAsync(Guid companyId, DateTime? from, DateTime? to);
    
    // Exchange Transactions
    Task<ApiResponse<ExchangeResponseDto>> CreateExchangeAsync(Guid companyId, Guid userId, CreateExchangeDto dto);
    Task<ApiResponse<PagedResult<ExchangeResponseDto>>> GetExchangesAsync(Guid companyId, int page, int pageSize, string? search, ExchangeType? type, DateTime? from, DateTime? to);
    Task<ApiResponse<ExchangeResponseDto>> GetExchangeByIdAsync(Guid companyId, Guid exchangeId);
    Task<ApiResponse<bool>> VoidExchangeAsync(Guid companyId, Guid userId, Guid exchangeId, string reason);
    
    // Daily Operations
    Task<ApiResponse<DailySummaryDto>> GetTodaySummaryAsync(Guid companyId);
    Task<ApiResponse<DailySummaryDto>> RecordOpeningFloatAsync(Guid companyId, Guid userId, OpeningFloatDto dto);
    Task<ApiResponse<DailySummaryDto>> RecordClosingFloatAsync(Guid companyId, Guid userId, ClosingFloatDto dto);
    Task<ApiResponse<List<DailySummaryDto>>> GetDailySummariesAsync(Guid companyId, DateTime from, DateTime to);
    
    // Reports
    Task<ApiResponse<ProfitReportDto>> GetProfitReportAsync(Guid companyId, DateTime from, DateTime to);
    Task<ApiResponse<List<LargeTransactionReportDto>>> GetLargeTransactionsAsync(Guid companyId, DateTime from, DateTime to, decimal threshold);
    Task<ApiResponse<ClientExchangeHistoryDto>> GetClientExchangeHistoryAsync(Guid companyId, Guid clientId);
    Task<ApiResponse<UsdPositionDto>> GetUsdPositionAsync(Guid companyId);
    
    // Alerts
    Task<ApiResponse<List<FloatAlertDto>>> GetAlertsAsync(Guid companyId);
    Task<ApiResponse<bool>> UpdateAlertThresholdsAsync(Guid companyId, decimal lowKes, decimal lowUsd, decimal largeTransaction);
}

public interface IInvoiceService
{
    Task<ApiResponse<InvoiceResponseDto>> CreateAsync(Guid companyId, CreateInvoiceDto dto);
    Task<ApiResponse<InvoiceResponseDto>> GetByIdAsync(Guid companyId, Guid id);
    Task<ApiResponse<PagedResult<InvoiceResponseDto>>> GetAllAsync(Guid companyId, int page, int pageSize);
    Task<ApiResponse<InvoiceResponseDto>> UpdateStatusAsync(Guid companyId, Guid id, UpdateInvoiceStatusDto dto);
    Task<ApiResponse<bool>> DeleteAsync(Guid companyId, Guid id);
    Task<ApiResponse<byte[]>> GeneratePdfAsync(Guid companyId, Guid id);
}

public interface IReconciliationService
{
    // EXISTING
    Task<ApiResponse<ReconciliationResponseDto>> CreateAsync(Guid companyId, CreateReconciliationDto dto);
    Task<ApiResponse<ReconciliationResponseDto>> GetByIdAsync(Guid companyId, Guid id);
    Task<ApiResponse<List<ReconciliationResponseDto>>> GetAllAsync(Guid companyId, AccountType? accountType = null);
    Task<ApiResponse<ReconciliationResponseDto>> CompleteAsync(Guid companyId, Guid id, Guid userId, CompleteReconciliationDto dto);
    Task<ApiResponse<decimal>> GetExpectedBalanceAsync(Guid companyId, AccountType accountType, Guid accountId);
    
    // NEW: Transaction-level reconciliation
    Task<ApiResponse<List<AccountReconciliationSummaryDto>>> GetAccountsWithStatsAsync(Guid companyId);
    Task<ApiResponse<PagedResult<TransactionReconciliationDto>>> GetAccountTransactionsAsync(
        Guid companyId, AccountType accountType, Guid accountId, ReconciliationFilterDto? filter, int page = 1, int pageSize = 50);
    Task<ApiResponse<AccountReconciliationBalanceDto>> GetAccountBalanceSummaryAsync(Guid companyId, AccountType accountType, Guid accountId);
    Task<ApiResponse<TransactionReconciliationDto>> ReconcileTransactionAsync(Guid companyId, Guid transactionId, Guid userId, ReconcileTransactionDto dto);
    Task<ApiResponse<int>> BulkReconcileAsync(Guid companyId, Guid userId, BulkReconcileDto dto);
}

public interface IReportService
{
    Task<ApiResponse<DailyReportDto>> GetDailyReportAsync(Guid companyId, DateTime date);
    Task<ApiResponse<PagedResult<TransactionResponseDto>>> GetTransactionReportAsync(Guid companyId, ReportFilterDto filter, int page, int pageSize);
    Task<ApiResponse<ClientBalanceReportDto>> GetClientBalanceReportAsync(Guid companyId, string? balanceType = null);
    Task<ApiResponse<AccountSummaryReportDto>> GetAccountSummaryReportAsync(Guid companyId);
    Task<ApiResponse<byte[]>> ExportToPdfAsync(Guid companyId, string reportType, ReportFilterDto filter);
    Task<ApiResponse<byte[]>> ExportToExcelAsync(Guid companyId, string reportType, ReportFilterDto filter);
}

public interface IDashboardService
{
    Task<ApiResponse<DashboardStatsDto>> GetOfficeUserDashboardAsync(Guid companyId);
    Task<ApiResponse<SuperAdminDashboardDto>> GetSuperAdminDashboardAsync();
}

public interface INotificationService
{
    Task<bool> SendTransactionNotificationAsync(Guid companyId, Guid clientId, TransactionResponseDto transaction);
    Task<bool> SendOtpAsync(string phoneNumber, string code);
    Task<bool> SendWelcomeMessageAsync(Guid companyId, string phoneNumber, string companyName);
    Task<bool> SendLoginAlertAsync(string phoneNumber, string userName, string ipAddress, DateTime loginTime);
    Task<bool> SendSystemErrorAlertAsync(string message, string details);
    
    // NEW: Send credentials to newly created office user (company)
    Task<bool> SendOfficeUserCredentialsAsync(Guid companyId, string phoneNumber, string companyName, string code, string password, string websiteUrl);
    
    // NEW: Send credentials to newly created permanent client
    Task<bool> SendClientCredentialsAsync(Guid companyId, Guid clientId, string phoneNumber, string fullName, string code, string password, string companyName, string websiteUrl);
    
    // NEW: Send transaction processed notification to permanent client
    Task<bool> SendClientTransactionProcessedAsync(Guid companyId, Guid clientId, string phoneNumber, string clientName, string companyName, string transactionCode, string transactionType, decimal amount, string currency, decimal balanceKES, decimal balanceUSD);
    
    // NEW: Send reversal notification to permanent client  
    Task<bool> SendReversalNotificationAsync(Guid companyId, Guid clientId, string phoneNumber, string clientName, string companyName, string originalCode, string reversalCode, decimal amount, string currency, string reason);
    
    // Balance alert notifications for office users
    Task NotifyCompanyAsync(Guid companyId, string title, string body, Dictionary<string, string>? data = null);
    Task CreateClientAlertAsync(Guid companyId, Guid clientId, string type, string title, string body, Guid? relatedTransactionId = null);
}

public interface IAuditService
{
    Task LogAsync(Guid? companyId, Guid? userId, UserRole? role, AuditAction action, string entityType, Guid? entityId, object? oldValues, object? newValues, string? ipAddress, string? userAgent);
    Task<ApiResponse<PagedResult<AdminAuditLogDto>>> GetAuditLogsAsync(int page, int pageSize, Guid? companyId = null);
    Task<ApiResponse<PagedResult<AdminLoginHistoryDto>>> GetLoginHistoryAsync(int page, int pageSize, Guid? companyId = null);
}

public interface ISystemLogService
{
    // Core logging methods
    Task LogErrorAsync(string source, string message, string? stackTrace = null, 
        Guid? companyId = null, Guid? userId = null, string? ipAddress = null, string? requestPath = null);
    Task LogCriticalAsync(string source, string message, string? stackTrace = null,
        Guid? companyId = null, Guid? userId = null);
    Task LogWarningAsync(string source, string message, Guid? companyId = null, Guid? userId = null);
    Task LogInfoAsync(string source, string message, Guid? companyId = null, Guid? userId = null);
    
    // Transaction-specific logging
    Task LogTransactionErrorAsync(Guid companyId, string transactionCode, string errorMessage, 
        string? details = null, Guid? userId = null);
    Task LogTransactionWarningAsync(Guid companyId, string transactionCode, string warningMessage, Guid? userId = null);
    Task LogTransactionSuccessAsync(Guid companyId, string transactionCode, decimal amount, 
        string currency, string transactionType, Guid? userId = null);
    
    // Auth logging
    Task LogLoginFailureAsync(string code, string reason, string ipAddress, Guid? companyId = null);
    Task LogLoginSuccessAsync(string code, string role, string ipAddress, Guid? companyId = null, Guid? userId = null);
    Task LogAccountLockedAsync(string code, int failedAttempts, string ipAddress, Guid? companyId = null);
    
    // Business operation logging
    Task LogReconciliationErrorAsync(Guid companyId, string errorMessage, string? details = null);
    Task LogClientErrorAsync(Guid companyId, string clientCode, string errorMessage, Guid? userId = null);
    Task LogAccountErrorAsync(Guid companyId, string accountType, string accountName, string errorMessage);
    Task LogInvoiceErrorAsync(Guid companyId, string invoiceNumber, string errorMessage);
    Task LogExpenseErrorAsync(Guid companyId, string errorMessage, Guid? userId = null);
    Task LogCompanyErrorAsync(Guid companyId, string errorMessage);
    
    // Security logging
    Task LogSecurityAlertAsync(string alertType, string message, string? ipAddress = null, 
        Guid? companyId = null, Guid? userId = null);
    Task LogSuspiciousActivityAsync(string activity, string ipAddress, Guid? companyId = null);
    
    // API logging
    Task LogApiErrorAsync(string endpoint, string method, string errorMessage, 
        string? stackTrace = null, string? ipAddress = null, Guid? companyId = null);
    
    // Query methods
    Task<ApiResponse<PagedResult<SystemLogResponseDto>>> GetLogsAsync(int page, int pageSize, string? level = null);
    Task<int> GetErrorCountLast24HoursAsync(Guid? companyId = null);
    Task<List<SystemLogResponseDto>> GetRecentErrorsAsync(int count = 10, Guid? companyId = null);
}
public interface ISuperAdminService
{
    // Dashboard
    Task<ApiResponse<SuperAdminDashboardExtendedDto>> GetDashboardAsync();
    
    // Companies
    Task<ApiResponse<PagedResult<CompanyStatsDto>>> GetAllCompaniesWithStatsAsync(
        int page, int pageSize, string? search = null, string? status = null);
    Task<ApiResponse<CompanyDetailDto>> GetCompanyDetailAsync(Guid companyId);
    Task<ApiResponse<bool>> UpdateSubscriptionAsync(Guid companyId, UpdateSubscriptionDto dto);
    Task<ApiResponse<bool>> SuspendCompanyAsync(Guid companyId, SuspendCompanyDto dto);
    Task<ApiResponse<bool>> ActivateCompanyAsync(Guid companyId);
    Task<ApiResponse<bool>> ResetOfficeUserPasswordAsync(Guid companyId, AdminResetPasswordDto dto);
    
    // System Health
    Task<ApiResponse<SystemHealthDto>> GetSystemHealthAsync();
    
    // Security
    Task<ApiResponse<SecurityOverviewDto>> GetSecurityOverviewAsync();
    Task<ApiResponse<PagedResult<SecurityAlertDto>>> GetSecurityAlertsAsync(int page, int pageSize, bool? resolved = null);
    Task<ApiResponse<bool>> ResolveSecurityAlertAsync(Guid alertId, ResolveAlertDto dto);
    Task<ApiResponse<bool>> BlockIPAsync(BlockIPDto dto);
    Task<ApiResponse<bool>> UnblockIPAsync(Guid id);
    Task<ApiResponse<List<IPWhitelistDto>>> GetIPWhitelistAsync();
    Task<ApiResponse<bool>> AddIPToWhitelistAsync(AddIPWhitelistDto dto, Guid addedByUserId);
    Task<ApiResponse<bool>> RemoveIPFromWhitelistAsync(Guid id);
    
    // Financial
    Task<ApiResponse<FinancialOverviewDto>> GetFinancialOverviewAsync();
    Task<ApiResponse<PagedResult<PaymentHistoryDto>>> GetPaymentHistoryAsync(int page, int pageSize, Guid? companyId = null);
    Task<ApiResponse<PaymentHistoryDto>> RecordPaymentAsync(RecordPaymentDto dto);
    
    // Analytics
    Task<ApiResponse<AnalyticsOverviewDto>> GetAnalyticsOverviewAsync(DateTime? startDate = null, DateTime? endDate = null);
    
    // Audit Logs (extended)
    Task<ApiResponse<PagedResult<AdminAuditLogDto>>> GetAuditLogsExtendedAsync(
        int page, int pageSize, AuditLogFilterDto? filter = null);
    Task<ApiResponse<byte[]>> ExportAuditLogsAsync(AuditLogFilterDto? filter = null);
}
/// <summary>
/// Push notifications via Expo. Implementations must be fail-safe (never throw).
/// </summary>
public interface IPushNotificationService
{
    Task SendToUserAsync(Guid userId, string title, string body, Dictionary<string, string>? data = null);
    Task SendToTokensAsync(IEnumerable<string> tokens, string title, string body, Dictionary<string, string>? data = null);
}