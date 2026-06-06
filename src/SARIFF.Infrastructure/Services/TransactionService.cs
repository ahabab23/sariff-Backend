
using Microsoft.EntityFrameworkCore;
using SARIFF.Core.DTOs;
using SARIFF.Core.Entities;
using SARIFF.Core.Enums;
using SARIFF.Core.Interfaces;
using SARIFF.Infrastructure.Data;

namespace SARIFF.Infrastructure.Services;

/// <summary>
/// Transaction Service - Implements TRADITIONAL DOUBLE-ENTRY ACCOUNTING
/// 
/// ACCOUNTING RULES:
/// ┌─────────────────┬────────────────────┬────────────────────┐
/// │ Account Type    │ DEBIT              │ CREDIT             │
/// ├─────────────────┼────────────────────┼────────────────────┤
/// │ ASSET           │ Balance INCREASES  │ Balance DECREASES  │
/// │ (Cash/Bank/     │ (money comes IN)   │ (money goes OUT)   │
/// │  M-Pesa)        │                    │                    │
/// ├─────────────────┼────────────────────┼────────────────────┤
/// │ LIABILITY       │ Balance DECREASES  │ Balance INCREASES  │
/// │ (Client)        │ (we owe less)      │ (we owe more)      │
/// └─────────────────┴────────────────────┴────────────────────┘
/// 
/// DOUBLE-ENTRY: Every transaction affects two accounts with equal and opposite effects.
/// 
/// REVERSAL METHOD: When deleting a transaction, we create a reversal entry
/// (opposite transaction) to maintain audit trail and running balance integrity.
/// </summary>
public class TransactionService : ITransactionService
{
    private readonly AppDbContext _context;
    private readonly INotificationService _notificationService;
    private readonly IClientAlertHelper _alertHelper;
    private readonly ISystemLogService _systemLog;
    private readonly IBalanceAlertService _balanceAlertService;

    public TransactionService(
        AppDbContext context, 
        INotificationService notificationService,
        ISystemLogService systemLog,
        IClientAlertHelper alertHelper,
        IBalanceAlertService balanceAlertService)
    {
        _context = context;
        _notificationService = notificationService;
        _systemLog = systemLog;
        _alertHelper = alertHelper;
        _balanceAlertService = balanceAlertService;
    }

    public async Task<ApiResponse<TransactionResponseDto>> CreateAsync(Guid companyId, Guid userId, CreateTransactionDto dto)
    {
        // Input validation
        var amountCheck = ValidationHelper.ValidateAmount(dto.Amount, "Transaction amount");
        if (!amountCheck.IsValid)
            return new ApiResponse<TransactionResponseDto>(false, amountCheck.Error!, null);
        
        if (dto.CounterAmount.HasValue)
        {
            var counterCheck = ValidationHelper.ValidateAmount(dto.CounterAmount.Value, "Counter amount");
            if (!counterCheck.IsValid)
                return new ApiResponse<TransactionResponseDto>(false, counterCheck.Error!, null);
        }

        if (dto.ExchangeRate.HasValue)
        {
            var rateCheck = ValidationHelper.ValidateRate(dto.ExchangeRate.Value, "Exchange rate");
            if (!rateCheck.IsValid)
                return new ApiResponse<TransactionResponseDto>(false, rateCheck.Error!, null);
        }

        var descCheck = ValidationHelper.ValidateText(dto.Description, "Description", 500);
        if (!descCheck.IsValid)
            return new ApiResponse<TransactionResponseDto>(false, descCheck.Error!, null);

        if (dto.SourceAccountId == dto.DestAccountId && dto.SourceAccountType == dto.DestAccountType)
            return new ApiResponse<TransactionResponseDto>(false, "Source and destination accounts cannot be the same", null);

        // FIX #3: If called from ExchangeRateService (which already opened a dbTransaction),
        // don't open a nested one. PostgreSQL doesn't support nested transactions.
        var existingTransaction = _context.Database.CurrentTransaction;
        var dbTransaction = existingTransaction == null 
            ? await _context.Database.BeginTransactionAsync() 
            : null;
        
        try
        {
            // ==================== FOREX HANDLING ====================
            var primaryAmount = dto.Amount;
            var primaryCurrency = dto.Currency;
            var counterAmount = dto.CounterAmount ?? dto.Amount;
            var counterCurrency = dto.CounterCurrency ?? dto.Currency;
            var isForex = dto.CounterCurrency.HasValue && dto.CounterCurrency != dto.Currency;
            // =========================================================

            // Validate accounts exist and belong to company
            var sourceBalance = await GetAccountBalanceAsync(companyId, dto.SourceAccountType, dto.SourceAccountId, primaryCurrency);
            var destBalance = await GetAccountBalanceAsync(companyId, dto.DestAccountType, dto.DestAccountId, counterCurrency);

            if (sourceBalance == null)
                return new ApiResponse<TransactionResponseDto>(false, "Source account not found or currency mismatch", null);
            if (destBalance == null)
                return new ApiResponse<TransactionResponseDto>(false, "Destination account not found or currency mismatch", null);

            // Generate reference and code
            var reference = await GenerateReferenceAsync(companyId);
            var code = await CodeGenerator.GenerateTransactionCodeAsync(_context, companyId);

            // ============================================================
            // TRADITIONAL ACCOUNTING BALANCE CALCULATION
            // ============================================================
            decimal newSourceBalance = CalculateNewBalance(
                dto.SourceAccountType, 
                sourceBalance.Value, 
                primaryAmount, 
                dto.TransactionType
            );

            // Destination gets the OPPOSITE transaction type effect
            var destTransactionType = dto.TransactionType == TransactionType.Debit 
                ? TransactionType.Credit 
                : TransactionType.Debit;

            decimal newDestBalance = CalculateNewBalance(
                dto.DestAccountType,
                destBalance.Value,
                counterAmount,
                destTransactionType
            );

            // ==================== NEGATIVE BALANCE GUARD ====================
            // Asset accounts (Cash, Bank, Mpesa) must NOT go negative.
            // Example: Client withdraws KES 10,000 but cash only has KES 9,000.
            if (IsAssetAccount(dto.SourceAccountType) && newSourceBalance < 0)
                return new ApiResponse<TransactionResponseDto>(false,
                    $"Insufficient {dto.SourceAccountType} balance. Available: {dto.Currency} {sourceBalance.Value:N2}, Required: {primaryAmount:N2}", null);

            if (IsAssetAccount(dto.DestAccountType) && newDestBalance < 0)
                return new ApiResponse<TransactionResponseDto>(false,
                    $"Insufficient {dto.DestAccountType} balance. Available: {(dto.CounterCurrency ?? dto.Currency)} {destBalance.Value:N2}, Required: {counterAmount:N2}", null);
            // ================================================================

            // ==================== RECONCILIATION STATUS ====================
            var needsReconciliation = 
                dto.SourceAccountType == AccountType.Cash ||
                dto.SourceAccountType == AccountType.Bank ||
                dto.SourceAccountType == AccountType.Mpesa ||
                dto.DestAccountType == AccountType.Cash ||
                dto.DestAccountType == AccountType.Bank ||
                dto.DestAccountType == AccountType.Mpesa;

            var reconciliationStatus = needsReconciliation 
                ? ReconciliationStatus.Pending 
                : ReconciliationStatus.Matched;
            // ================================================================

            // Create transaction record
            var transaction = new Transaction
            {
                CompanyId = companyId,
                Code = code,
                Reference = reference,
                TransactionDate = dto.TransactionDate ?? DateTime.UtcNow,
                TransactionType = dto.TransactionType,
                
                // Primary account amount
                Amount = primaryAmount,
                Currency = primaryCurrency,
                
                // Counter account amount (for forex)
                CounterAmount = isForex ? counterAmount : null,
                CounterCurrency = isForex ? counterCurrency : null,
                
                Description = dto.Description,
                Notes = dto.Notes,
                ExchangeRate = dto.ExchangeRate,
                
                // Source (Primary) account
                SourceAccountType = dto.SourceAccountType,
                SourceAccountId = dto.SourceAccountId,
                SourceBalanceBefore = sourceBalance.Value,
                SourceBalanceAfter = newSourceBalance,
                
                // Dest (Counter) account
                DestAccountType = dto.DestAccountType,
                DestAccountId = dto.DestAccountId,
                DestBalanceBefore = destBalance.Value,
                DestBalanceAfter = newDestBalance,
                
                // Reconciliation fields
                ReconciliationStatus = reconciliationStatus,
                ActualAmount = null,
                Variance = null,
                ReconciledAt = null,
                ReconciledByUserId = null,
                ReconciliationNotes = null,
                CreatedByUserId = userId
            };

            _context.Transactions.Add(transaction);

            // Update account balances
            await UpdateAccountBalanceAsync(companyId, dto.SourceAccountType, dto.SourceAccountId, primaryCurrency, newSourceBalance);
            await UpdateAccountBalanceAsync(companyId, dto.DestAccountType, dto.DestAccountId, counterCurrency, newDestBalance);

            await _context.SaveChangesAsync();
            
            // FIX #3: Only commit if we own the transaction (not nested from ExchangeRateService)
            if (dbTransaction != null)
                await dbTransaction.CommitAsync();

            // ============================================================
            // SEND NOTIFICATIONS AND CREATE ALERTS FOR CLIENTS
            // ============================================================
            if (dto.SourceAccountType == AccountType.Client)
            {
                var response = await MapToResponseAsync(transaction);
                await _notificationService.SendTransactionNotificationAsync(companyId, dto.SourceAccountId, response);
                
                await _alertHelper.CreateTransactionAlertAsync(
                    transaction, 
                    dto.SourceAccountId, 
                    companyId, 
                    isIncoming: false
                );
                
                await CheckAndCreateLowBalanceAlertAsync(companyId, dto.SourceAccountId, newSourceBalance, primaryCurrency);
            }
            
            if (dto.DestAccountType == AccountType.Client)
            {
                var response = await MapToResponseAsync(transaction);
                await _notificationService.SendTransactionNotificationAsync(companyId, dto.DestAccountId, response);
                
                await _alertHelper.CreateTransactionAlertAsync(
                    transaction, 
                    dto.DestAccountId, 
                    companyId, 
                    isIncoming: true
                );
            }
            
            await _systemLog.LogTransactionSuccessAsync(companyId, transaction.Code, 
                transaction.Amount, transaction.Currency.ToString(), transaction.TransactionType.ToString());
            
            // ============================================================
            // CHECK BALANCE ALERT RULES (notify office users)
            // ============================================================
            if (dto.SourceAccountType == AccountType.Client)
            {
                var client = await _context.Users.FindAsync(dto.SourceAccountId);
                if (client != null)
                    await _balanceAlertService.CheckAndNotifyAsync(companyId, client.Id, client.FullName, client.BalanceKES, client.BalanceUSD);
            }
            if (dto.DestAccountType == AccountType.Client)
            {
                var client = await _context.Users.FindAsync(dto.DestAccountId);
                if (client != null)
                    await _balanceAlertService.CheckAndNotifyAsync(companyId, client.Id, client.FullName, client.BalanceKES, client.BalanceUSD);
            }
            
            return new ApiResponse<TransactionResponseDto>(true, "Transaction created successfully", await MapToResponseAsync(transaction));
        }
        catch (DbUpdateConcurrencyException)
        {
            // FIX #18: Optimistic concurrency — another request modified the same balance
            if (dbTransaction != null)
                await dbTransaction.RollbackAsync();
            return new ApiResponse<TransactionResponseDto>(false,
                "Balance was modified by another operation. Please retry.", null);
        }
        catch (Exception ex)
        {
            // FIX #3: Only rollback if we own the transaction
            if (dbTransaction != null)
                await dbTransaction.RollbackAsync();
            await _systemLog.LogErrorAsync("TransactionService", $"Failed to create transaction: {ex.Message}", ex.StackTrace, companyId, userId);
            return new ApiResponse<TransactionResponseDto>(false, "Failed to create transaction. Please try again.", null);
        }
        finally
        {
            // FIX #3: Dispose our transaction if we created it
            if (dbTransaction != null)
                await dbTransaction.DisposeAsync();
        }
    }

    /// <summary>
    /// Checks if client balance is below threshold and creates a warning alert
    /// </summary>
    private async Task CheckAndCreateLowBalanceAlertAsync(Guid companyId, Guid clientId, decimal newBalance, Currency currency)
    {
        var threshold = currency == Currency.KES ? 10000m : 100m;
        
        if (newBalance < threshold && newBalance >= 0)
        {
            await _alertHelper.CreateLowBalanceAlertAsync(companyId, clientId, newBalance, currency);
        }
    }

    /// <summary>
    /// Calculates new balance based on traditional accounting rules
    /// FIX #17: Removed unused isSourceAccount parameter
    /// </summary>
    private decimal CalculateNewBalance(
        AccountType accountType, 
        decimal currentBalance, 
        decimal amount, 
        TransactionType transactionType)
    {
        bool isAssetAccount = IsAssetAccount(accountType);

        if (transactionType == TransactionType.Debit)
        {
            return isAssetAccount 
                ? currentBalance + amount
                : currentBalance - amount;
        }
        else
        {
            return isAssetAccount 
                ? currentBalance - amount
                : currentBalance + amount;
        }
    }

    /// <summary>
    /// Determines if an account type is an Asset account
    /// </summary>
    private bool IsAssetAccount(AccountType accountType)
    {
        return accountType switch
        {
            AccountType.Cash => true,
            AccountType.Bank => true,
            AccountType.Mpesa => true,
            AccountType.Client => false,
            AccountType.Expense => false, // Expense accounts are not asset accounts
            _ => true
        };
    }

    public async Task<ApiResponse<TransactionResponseDto>> GetByIdAsync(Guid companyId, Guid id)
    {
        var transaction = await _context.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.CompanyId == companyId && !t.IsDeleted);
        if (transaction == null)
            return new ApiResponse<TransactionResponseDto>(false, "Transaction not found", null);

        return new ApiResponse<TransactionResponseDto>(true, "Success", await MapToResponseAsync(transaction));
    }

    public async Task<ApiResponse<PagedResult<TransactionResponseDto>>> GetAllAsync(Guid companyId, int page, int pageSize, ReportFilterDto? filter = null)
    {
        var query = _context.Transactions.Where(t => t.CompanyId == companyId && !t.IsDeleted);

        if (filter != null)
        {
            if (filter.StartDate.HasValue)
                query = query.Where(t => t.TransactionDate >= filter.StartDate.Value);
            if (filter.EndDate.HasValue)
                query = query.Where(t => t.TransactionDate <= filter.EndDate.Value);
            if (filter.TransactionType.HasValue)
                query = query.Where(t => t.TransactionType == filter.TransactionType.Value);
            if (filter.Currency.HasValue)
                query = query.Where(t => t.Currency == filter.Currency.Value);
        }

        var totalCount = await query.CountAsync();
        var transactions = await query
            .OrderByDescending(t => t.TransactionDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = new List<TransactionResponseDto>();
        foreach (var t in transactions)
        {
            items.Add(await MapToResponseAsync(t));
        }

        return new ApiResponse<PagedResult<TransactionResponseDto>>(true, "Success",
            new PagedResult<TransactionResponseDto>(items, totalCount, page, pageSize));
    }

    public async Task<ApiResponse<TransactionResponseDto>> UpdateAsync(Guid companyId, Guid id, UpdateTransactionDto dto)
    {
        var transaction = await _context.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.CompanyId == companyId && !t.IsDeleted);
        if (transaction == null)
            return new ApiResponse<TransactionResponseDto>(false, "Transaction not found", null);

        // Cannot edit a reversed transaction or a reversal entry
        if (transaction.DeletedAt.HasValue)
            return new ApiResponse<TransactionResponseDto>(false, "Cannot edit a reversed transaction", null);
        if (transaction.Reference.StartsWith("REV-"))
            return new ApiResponse<TransactionResponseDto>(false, "Cannot edit a reversal transaction", null);
        if (transaction.Reference.StartsWith("EXC-"))
            return new ApiResponse<TransactionResponseDto>(false, "Cannot edit exchange transactions", null);
        if (transaction.Reference.StartsWith("EXP-"))
            return new ApiResponse<TransactionResponseDto>(false, "Cannot edit expense transactions", null);
        if (transaction.Reference == "FLOAT-TXN")
            return new ApiResponse<TransactionResponseDto>(false, "Cannot edit float transactions", null);

        // Only allow updating description and notes (not amounts - that would corrupt ledger)
        if (dto.Description != null) transaction.Description = dto.Description;
        if (dto.Notes != null) transaction.Notes = dto.Notes;
        transaction.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return new ApiResponse<TransactionResponseDto>(true, "Transaction updated successfully", await MapToResponseAsync(transaction));
    }

    /// <summary>
    /// Delete transaction using proper accounting reversal method.
    /// 
    /// RULES:
    /// 1. Cannot reverse a transaction that is already reversed (DeletedAt != null)
    /// 2. Cannot reverse a reversal transaction (Reference starts with "REV-")
    /// 3. Original transaction stays VISIBLE in statements (IsDeleted stays false)
    ///    so the running balance chain is never broken
    /// 4. Original is marked with [REVERSED] in description and DeletedAt is set
    ///    as a reversal flag (but IsDeleted remains false)
    /// 5. Reversal transaction appears as the next line in the statement
    /// </summary>
    public async Task<ApiResponse<bool>> DeleteAsync(Guid companyId, Guid id, Guid userId, DeleteTransactionDto dto, Guid? clientContext = null)
    {
        using var dbTransaction = await _context.Database.BeginTransactionAsync();
        
        try
        {
            var transaction = await _context.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.CompanyId == companyId && !t.IsDeleted);
            if (transaction == null)
                return new ApiResponse<bool>(false, "Transaction not found", false);

            // GUARD: Cannot reverse a reversal transaction
            if (transaction.Reference.StartsWith("REV-"))
                return new ApiResponse<bool>(false, "Cannot reverse a reversal transaction", false);

            // GUARD: Cannot reverse an already-reversed transaction
            if (transaction.DeletedAt.HasValue)
                return new ApiResponse<bool>(false, 
                    $"Transaction {transaction.Code} has already been reversed", false);

            // GUARD: Cannot reverse exchange-linked transactions — use Void Exchange instead
            if (transaction.Reference.StartsWith("EXC-"))
                return new ApiResponse<bool>(false, 
                    "Cannot reverse exchange transactions here. Use the Void Exchange endpoint instead.", false);

            // GUARD: Cannot reverse expense-linked transactions — use Delete Expense instead
            if (transaction.Reference.StartsWith("EXP-"))
                return new ApiResponse<bool>(false, 
                    "Cannot reverse expense transactions here. Delete the expense instead.", false);

            // GUARD: Cannot reverse float transactions — managed via Exchange module
            if (transaction.Reference == "FLOAT-TXN")
                return new ApiResponse<bool>(false, 
                    "Cannot reverse float transactions here. Use the Exchange module instead.", false);

            // GUARD: Client transactions — controlled access
            bool isClientTransaction = transaction.SourceAccountType == AccountType.Client || transaction.DestAccountType == AccountType.Client;
            if (isClientTransaction)
            {
                if (!clientContext.HasValue)
                {
                    // Called from general endpoint — block
                    return new ApiResponse<bool>(false, 
                        "Transactions involving client accounts can only be deleted from the client statement.", false);
                }
                
                // Called from client statement — validate the transaction belongs to this client
                bool belongsToClient = 
                    (transaction.SourceAccountType == AccountType.Client && transaction.SourceAccountId == clientContext.Value) ||
                    (transaction.DestAccountType == AccountType.Client && transaction.DestAccountId == clientContext.Value);
                    
                if (!belongsToClient)
                    return new ApiResponse<bool>(false, "Transaction does not belong to this client", false);
            }

            // ============================================================
            // METHOD: ACCOUNTING REVERSAL
            // Create an opposite transaction to reverse the original effect
            // Both original + reversal stay visible in statements
            // ============================================================

            var primaryCurrency = transaction.Currency;
            var counterCurrency = transaction.CounterCurrency ?? transaction.Currency;
            var counterAmount = transaction.CounterAmount ?? transaction.Amount;

            // Get current balances
            var currentSourceBalance = await GetAccountBalanceAsync(companyId, transaction.SourceAccountType, transaction.SourceAccountId, primaryCurrency);
            var currentDestBalance = await GetAccountBalanceAsync(companyId, transaction.DestAccountType, transaction.DestAccountId, counterCurrency);

            if (currentSourceBalance == null || currentDestBalance == null)
                return new ApiResponse<bool>(false, "One or more accounts not found", false);

            // Calculate reversal amounts
            // The reversal is the OPPOSITE of the original transaction
            var reversedSourceBalance = CalculateNewBalance(
                transaction.SourceAccountType,
                currentSourceBalance.Value,
                transaction.Amount,
                transaction.TransactionType == TransactionType.Debit ? TransactionType.Credit : TransactionType.Debit
            );

            var reversedDestBalance = CalculateNewBalance(
                transaction.DestAccountType,
                currentDestBalance.Value,
                counterAmount,
                transaction.TransactionType // Opposite of original dest
            );

            // Generate new code for reversal
            var reversalCode = await CodeGenerator.GenerateTransactionCodeAsync(_context, companyId);
            var reversalReference = $"REV-{transaction.Reference}";

            // Create reversal transaction
            var reversalTransaction = new Transaction
            {
                CompanyId = companyId,
                Code = reversalCode,
                Reference = reversalReference,
                TransactionDate = DateTime.UtcNow,
                TransactionType = transaction.TransactionType == TransactionType.Debit ? TransactionType.Credit : TransactionType.Debit,
                
                Amount = transaction.Amount,
                Currency = primaryCurrency,
                CounterAmount = transaction.CounterAmount,
                CounterCurrency = transaction.CounterCurrency,
                
                Description = $"REVERSAL: {transaction.Description}",
                Notes = $"Reversal of {transaction.Code}. Reason: {dto.Reason ?? "Not specified"}",
                ExchangeRate = transaction.ExchangeRate,
                
                SourceAccountType = transaction.SourceAccountType,
                SourceAccountId = transaction.SourceAccountId,
                SourceBalanceBefore = currentSourceBalance.Value,
                SourceBalanceAfter = reversedSourceBalance,
                
                DestAccountType = transaction.DestAccountType,
                DestAccountId = transaction.DestAccountId,
                DestBalanceBefore = currentDestBalance.Value,
                DestBalanceAfter = reversedDestBalance,
                
                ReconciliationStatus = ReconciliationStatus.Matched, // Reversals are auto-matched
                CreatedByUserId = userId
            };

            _context.Transactions.Add(reversalTransaction);

            // Update account balances to reversed values
            await UpdateAccountBalanceAsync(companyId, transaction.SourceAccountType, transaction.SourceAccountId, primaryCurrency, reversedSourceBalance);
            await UpdateAccountBalanceAsync(companyId, transaction.DestAccountType, transaction.DestAccountId, counterCurrency, reversedDestBalance);

            // ============================================================
            // MARK ORIGINAL AS REVERSED — but do NOT soft-delete it
            // IsDeleted stays false → transaction remains visible in statements
            // DeletedAt is set as a "reversed" flag
            // Description gets [REVERSED] prefix so it's clear in the statement
            // ============================================================
            transaction.DeletedByUserId = userId;
            transaction.DeletedAt = DateTime.UtcNow;
            transaction.DeleteReason = dto.Reason;
            transaction.UpdatedAt = DateTime.UtcNow;
            transaction.Description = $"[REVERSED] {transaction.Description}";
            transaction.Notes = (transaction.Notes ?? "") + $" [Reversed by: {reversalCode}]";

            await _context.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            // ============================================================
            // CREATE ALERTS FOR REVERSED TRANSACTION
            // ============================================================
            if (transaction.SourceAccountType == AccountType.Client)
            {
                await _alertHelper.CreateAlertAsync(
                    companyId,
                    transaction.SourceAccountId,
                    "info",
                    "Transaction Reversed",
                    $"Transaction {transaction.Code} for {transaction.Currency} {transaction.Amount:N2} has been reversed. Reason: {dto.Reason ?? "Not specified"}"
                );
            }
            if (transaction.DestAccountType == AccountType.Client)
            {
                await _alertHelper.CreateAlertAsync(
                    companyId,
                    transaction.DestAccountId,
                    "info",
                    "Transaction Reversed",
                    $"Transaction {transaction.Code} for {counterCurrency} {counterAmount:N2} has been reversed. Reason: {dto.Reason ?? "Not specified"}"
                );
            }

            await _systemLog.LogInfoAsync("TransactionService", 
                $"Transaction {transaction.Code} reversed by user {userId}. Reversal: {reversalCode}", companyId, userId);

            return new ApiResponse<bool>(true, $"Transaction reversed successfully. Reversal code: {reversalCode}", true);
        }
        catch (DbUpdateConcurrencyException)
        {
            await dbTransaction.RollbackAsync();
            return new ApiResponse<bool>(false, "Balance was modified by another operation. Please retry.", false);
        }
        catch (Exception ex)
        {
            await dbTransaction.RollbackAsync();
            await _systemLog.LogErrorAsync("TransactionService", $"Failed to reverse transaction: {ex.Message}", ex.StackTrace, companyId, userId);
            return new ApiResponse<bool>(false, "Failed to reverse transaction. Please try again.", false);
        }
    }

    public async Task<ApiResponse<TransactionSummaryDto>> GetTodaySummaryAsync(Guid companyId)
    {
        var today = DateTime.UtcNow.Date;
        var transactions = await _context.Transactions
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.TransactionDate.Date == today)
            .ToListAsync();

        // Business-perspective totals:
        // When a Client is credited (money flows TO client) → Business Debit (money OUT)
        // When a Client is debited (money flows FROM client) → Business Credit (money IN)
        // Non-client transactions: use TransactionType as-is
        decimal totalDebitKES = 0, totalDebitUSD = 0, totalCreditKES = 0, totalCreditUSD = 0;

        foreach (var t in transactions)
        {
            bool isClientTransaction =
                t.SourceAccountType == AccountType.Client ||
                t.DestAccountType == AccountType.Client;

            if (isClientTransaction)
            {
                // From business perspective: flip the type
                // Client Credit = business money OUT = TotalDebit
                // Client Debit = business money IN = TotalCredit
                if (t.TransactionType == TransactionType.Credit)
                {
                    if (t.Currency == Currency.KES) totalDebitKES += t.Amount;
                    else totalDebitUSD += t.Amount;
                }
                else
                {
                    if (t.Currency == Currency.KES) totalCreditKES += t.Amount;
                    else totalCreditUSD += t.Amount;
                }
            }
            else
            {
                // Non-client (internal transfers, expenses, etc.)
                if (t.TransactionType == TransactionType.Debit)
                {
                    if (t.Currency == Currency.KES) totalDebitKES += t.Amount;
                    else totalDebitUSD += t.Amount;
                }
                else
                {
                    if (t.Currency == Currency.KES) totalCreditKES += t.Amount;
                    else totalCreditUSD += t.Amount;
                }
            }
        }

        var summary = new TransactionSummaryDto(
            TotalCount: transactions.Count,
            TotalDebitKES: totalDebitKES,
            TotalDebitUSD: totalDebitUSD,
            TotalCreditKES: totalCreditKES,
            TotalCreditUSD: totalCreditUSD,
            NetFlowKES: totalDebitKES - totalCreditKES,    // Positive = net inflow
            NetFlowUSD: totalDebitUSD - totalCreditUSD
        );

        return new ApiResponse<TransactionSummaryDto>(true, "Success", summary);
    }

    public async Task<ApiResponse<List<TransactionResponseDto>>> GetRecentAsync(Guid companyId, int count = 10)
    {
        var transactions = await _context.Transactions
            .Where(t => t.CompanyId == companyId && !t.IsDeleted)
            .OrderByDescending(t => t.TransactionDate)
            .Take(count)
            .ToListAsync();

        var items = new List<TransactionResponseDto>();
        foreach (var t in transactions)
        {
            items.Add(await MapToResponseAsync(t));
        }

        return new ApiResponse<List<TransactionResponseDto>>(true, "Success", items);
    }

    /// <summary>
    /// FIXED: Now uses accountId for cash accounts where appropriate
    /// </summary>
    private async Task<decimal?> GetAccountBalanceAsync(Guid companyId, AccountType type, Guid accountId, Currency currency)
    {
        return type switch
        {
            AccountType.Cash => await _context.CashAccounts
                .Where(c => c.CompanyId == companyId && c.Id == accountId && !c.IsDeleted)
                .Select(c => (decimal?)c.Balance)
                .FirstOrDefaultAsync(),
            AccountType.Bank => await _context.BankAccounts
                .Where(b => b.CompanyId == companyId && b.Id == accountId && !b.IsDeleted)
                .Select(b => (decimal?)b.Balance)
                .FirstOrDefaultAsync(),
            AccountType.Mpesa => await _context.MpesaAgents
                .Where(m => m.CompanyId == companyId && m.Id == accountId && !m.IsDeleted)
                .Select(m => (decimal?)m.Balance)
                .FirstOrDefaultAsync(),
            AccountType.Client => currency == Currency.KES
                ? await _context.Users.Where(u => u.Id == accountId && u.CompanyId == companyId && !u.IsDeleted).Select(u => (decimal?)u.BalanceKES).FirstOrDefaultAsync()
                : await _context.Users.Where(u => u.Id == accountId && u.CompanyId == companyId && !u.IsDeleted).Select(u => (decimal?)u.BalanceUSD).FirstOrDefaultAsync(),
            AccountType.Expense => 0m, // Expense categories don't track running balance
            _ => null
        };
    }

    private async Task UpdateAccountBalanceAsync(Guid companyId, AccountType type, Guid accountId, Currency currency, decimal newBalance)
    {
        switch (type)
        {
            case AccountType.Cash:
                var cash = await _context.CashAccounts.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == accountId && !c.IsDeleted);
                if (cash != null) { cash.Balance = newBalance; cash.UpdatedAt = DateTime.UtcNow; }
                break;
            case AccountType.Bank:
                var bank = await _context.BankAccounts.FirstOrDefaultAsync(b => b.Id == accountId && b.CompanyId == companyId && !b.IsDeleted);
                if (bank != null) { bank.Balance = newBalance; bank.UpdatedAt = DateTime.UtcNow; }
                break;
            case AccountType.Mpesa:
                var mpesa = await _context.MpesaAgents.FirstOrDefaultAsync(m => m.Id == accountId && m.CompanyId == companyId && !m.IsDeleted);
                if (mpesa != null) { mpesa.Balance = newBalance; mpesa.UpdatedAt = DateTime.UtcNow; }
                break;
            case AccountType.Client:
                var client = await _context.Users.FirstOrDefaultAsync(u => u.Id == accountId && u.CompanyId == companyId && !u.IsDeleted);
                if (client != null)
                {
                    if (currency == Currency.KES) client.BalanceKES = newBalance;
                    else client.BalanceUSD = newBalance;
                    client.UpdatedAt = DateTime.UtcNow;
                }
                break;
            case AccountType.Expense:
                // Expense categories don't track running balance - no update needed
                break;
        }
    }

    private async Task<string> GenerateReferenceAsync(Guid companyId)
    {
        var today = DateTime.UtcNow;
        var count = await _context.Transactions
            .Where(t => t.CompanyId == companyId && t.TransactionDate.Date == today.Date)
            .CountAsync();
        return $"TXN-{today:yyyyMMdd}-{(count + 1):D4}";
    }

    private async Task<string> GetAccountNameAsync(AccountType type, Guid accountId)
    {
        return type switch
        {
            AccountType.Cash => "Cash",
            AccountType.Bank => await _context.BankAccounts.Where(b => b.Id == accountId).Select(b => b.BankName).FirstOrDefaultAsync() ?? "Bank",
            AccountType.Mpesa => await _context.MpesaAgents.Where(m => m.Id == accountId).Select(m => m.AgentName).FirstOrDefaultAsync() ?? "M-Pesa",
            AccountType.Client => await _context.Users.Where(u => u.Id == accountId).Select(u => u.FullName).FirstOrDefaultAsync() ?? "Client",
            _ => "Unknown"
        };
    }

    private async Task<TransactionResponseDto> MapToResponseAsync(Transaction t) => new(
        t.Id,
        t.Code,
        t.Reference,
        t.TransactionDate,
        t.TransactionType,
        t.Amount,
        t.Currency,
        t.Description,
        t.Notes,
        t.ExchangeRate,
        t.SourceAccountType,
        t.SourceAccountId,
        SourceAccountName: await GetAccountNameAsync(t.SourceAccountType, t.SourceAccountId),
        t.SourceBalanceBefore,
        t.SourceBalanceAfter,
        t.DestAccountType,
        t.DestAccountId,
        DestAccountName: await GetAccountNameAsync(t.DestAccountType, t.DestAccountId),
        t.DestBalanceBefore,
        t.DestBalanceAfter,
        t.CounterAmount,
        t.CounterCurrency,
        t.ReconciliationStatus,
        t.CreatedAt,
        IsReversed: t.DeletedAt.HasValue && !t.IsDeleted,   // Original that was reversed
        IsReversal: t.Reference.StartsWith("REV-")          // The reversal entry
    );
}