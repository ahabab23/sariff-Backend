
using Microsoft.EntityFrameworkCore;
using SARIFF.Core.DTOs;
using SARIFF.Core.Entities;
using SARIFF.Core.Enums;
using SARIFF.Infrastructure.Data;

namespace SARIFF.Infrastructure.Services;

/// <summary>
/// Helper class for generating statement lines with TRADITIONAL ACCOUNTING rules
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
/// │ (Client)        │ (client owes less) │ (client owes more) │
/// └─────────────────┴────────────────────┴────────────────────┘
/// 
/// STATEMENT COLUMNS (for Asset accounts like Cash/Bank/M-Pesa):
/// - Debit column = Money IN (balance increased)
/// - Credit column = Money OUT (balance decreased)
/// 
/// STATEMENT COLUMNS (for Liability accounts like Client):
/// - Debit column = Money OUT (balance decreased)
/// - Credit column = Money IN (balance increased)
/// </summary>
public class StatementHelper
{
    private readonly AppDbContext _context;

    public StatementHelper(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Creates a statement line with proper transaction action types for both accounts
    /// </summary>
    public async Task<StatementLineDto> CreateStatementLineAsync(
        Transaction txn, 
        AccountType viewerAccountType, 
        Guid viewerAccountId,
        Currency? viewerCurrency = null)
    {
        // Determine if viewer is source or dest
        bool isSource;
        
        if (viewerAccountType == AccountType.Cash && viewerCurrency.HasValue)
        {
            isSource = txn.SourceAccountType == AccountType.Cash && txn.Currency == viewerCurrency.Value;
        }
        else
        {
            isSource = txn.SourceAccountType == viewerAccountType && txn.SourceAccountId == viewerAccountId;
        }
        
        // ==================== THIS ACCOUNT'S DATA ====================
        decimal amount, balanceBefore, balanceAfter;
        Currency currency;
        
        if (isSource)
        {
            amount = txn.Amount;
            currency = txn.Currency;
            balanceBefore = txn.SourceBalanceBefore;
            balanceAfter = txn.SourceBalanceAfter;
        }
        else
        {
            amount = txn.CounterAmount ?? txn.Amount;
            currency = txn.CounterCurrency ?? txn.Currency;
            balanceBefore = txn.DestBalanceBefore;
            balanceAfter = txn.DestBalanceAfter;
        }

        // ==================== DETERMINE ACTIONS ====================
        // TRADITIONAL ACCOUNTING:
        // - TransactionType refers to SOURCE account action
        // - DEST account gets the OPPOSITE action
        //
        // If viewer is SOURCE:
        //   - Use the transaction type directly
        //
        // If viewer is DEST:
        //   - Use the OPPOSITE of transaction type

        string thisAccountAction;
        string relatedAccountAction;
        decimal? debit = null;
        decimal? credit = null;
        
        if (isSource)
        {
            // Viewer is source - use transaction type directly
            thisAccountAction = txn.TransactionType == TransactionType.Debit ? "Debit" : "Credit";
            relatedAccountAction = txn.TransactionType == TransactionType.Debit ? "Credit" : "Debit";
            
            if (txn.TransactionType == TransactionType.Debit)
                debit = amount;
            else
                credit = amount;
        }
        else
        {
            // Viewer is dest - use OPPOSITE of transaction type
            thisAccountAction = txn.TransactionType == TransactionType.Debit ? "Credit" : "Debit";
            relatedAccountAction = txn.TransactionType == TransactionType.Debit ? "Debit" : "Credit";
            
            if (txn.TransactionType == TransactionType.Debit)
                credit = amount;  // Dest gets credit when source is debited
            else
                debit = amount;   // Dest gets debit when source is credited
        }

        // ==================== RELATED ACCOUNT ====================
        var relatedAccount = await GetRelatedAccountAsync(txn, isSource, relatedAccountAction);

        return new StatementLineDto(
            TransactionId: txn.Id,
            TransactionCode: txn.Code,
            Date: txn.TransactionDate,
            Reference: txn.Reference,
            Description: txn.Description,
            TransactionType: txn.TransactionType,
            
            // This account
            ThisAccountAction: thisAccountAction,
            Debit: debit,
            Credit: credit,
            Amount: amount,
            Currency: currency,
            BalanceBefore: balanceBefore,
            BalanceAfter: balanceAfter,
            
            // Related account
            RelatedAccount: relatedAccount,
            
            // Forex
            ExchangeRate: txn.ExchangeRate,
            CounterAmount: isSource ? txn.CounterAmount : txn.Amount,
            CounterCurrency: isSource ? txn.CounterCurrency : txn.Currency,
            
            // Meta
            Notes: txn.Notes,
            ReconciliationStatus: txn.ReconciliationStatus,
            
            // Reversal status
            IsReversed: txn.DeletedAt.HasValue && !txn.IsDeleted,
            IsReversal: txn.Reference.StartsWith("REV-")
        );
    }

    /// <summary>
    /// Gets related account details with action type
    /// </summary>
    private async Task<RelatedAccountDto> GetRelatedAccountAsync(
        Transaction txn, 
        bool viewerIsSource, 
        string relatedAction)
    {
        AccountType relatedType;
        Guid relatedId;
        decimal relatedAmount, relatedBalanceBefore, relatedBalanceAfter;
        Currency relatedCurrency;

        if (viewerIsSource)
        {
            relatedType = txn.DestAccountType;
            relatedId = txn.DestAccountId;
            relatedAmount = txn.CounterAmount ?? txn.Amount;
            relatedCurrency = txn.CounterCurrency ?? txn.Currency;
            relatedBalanceBefore = txn.DestBalanceBefore;
            relatedBalanceAfter = txn.DestBalanceAfter;
        }
        else
        {
            relatedType = txn.SourceAccountType;
            relatedId = txn.SourceAccountId;
            relatedAmount = txn.Amount;
            relatedCurrency = txn.Currency;
            relatedBalanceBefore = txn.SourceBalanceBefore;
            relatedBalanceAfter = txn.SourceBalanceAfter;
        }

        // Get related account details
        string accountName = "Unknown";
        string? accountCode = null;
        string? clientCode = null;
        string? clientPhone = null;

        switch (relatedType)
        {
            case AccountType.Client:
                var client = await _context.Users.FirstOrDefaultAsync(u => u.Id == relatedId);
                if (client != null)
                {
                    accountName = client.FullName;
                    accountCode = client.Code;
                    clientCode = client.Code;
                    clientPhone = client.WhatsAppNumber;
                }
                break;
            case AccountType.Bank:
                var bank = await _context.BankAccounts.FirstOrDefaultAsync(b => b.Id == relatedId);
                if (bank != null)
                {
                    accountName = $"{bank.BankName} - {bank.AccountNumber}";
                    accountCode = bank.Code;
                }
                break;
            case AccountType.Mpesa:
                var mpesa = await _context.MpesaAgents.FirstOrDefaultAsync(m => m.Id == relatedId);
                if (mpesa != null)
                {
                    accountName = $"{mpesa.AgentName} - {mpesa.AgentNumber}";
                    accountCode = mpesa.Code;
                }
                break;
            case AccountType.Cash:
                accountName = $"Cash {relatedCurrency}";
                accountCode = "CASH";
                break;
        }

        return new RelatedAccountDto(
            AccountId: relatedId,
            AccountType: relatedType,
            AccountName: accountName,
            AccountCode: accountCode,
            Currency: relatedCurrency,
            Action: relatedAction,
            Amount: relatedAmount,
            BalanceBefore: relatedBalanceBefore,
            BalanceAfter: relatedBalanceAfter,
            ClientCode: clientCode,
            ClientPhone: clientPhone
        );
    }

    /// <summary>
    /// Calculates debit and credit totals for an account from transactions
    /// 
    /// TRADITIONAL ACCOUNTING:
    /// - For ASSET accounts: Debit = money IN, Credit = money OUT
    /// - For LIABILITY accounts: Debit = money OUT, Credit = money IN
    /// </summary>
    public (decimal totalDebit, decimal totalCredit) CalculateTransactionTotals(
        List<Transaction> transactions,
        AccountType accountType,
        Guid accountId,
        Currency? currency = null)
    {
        decimal totalDebit = 0, totalCredit = 0;

        foreach (var txn in transactions)
        {
            bool isSource;
            decimal amount;

            if (accountType == AccountType.Cash && currency.HasValue)
            {
                isSource = txn.SourceAccountType == AccountType.Cash && txn.Currency == currency.Value;
                amount = isSource ? txn.Amount : (txn.CounterAmount ?? txn.Amount);
            }
            else
            {
                isSource = txn.SourceAccountType == accountType && txn.SourceAccountId == accountId;
                amount = isSource ? txn.Amount : (txn.CounterAmount ?? txn.Amount);
            }

            // Determine the action for THIS account
            // Source gets the transaction type directly
            // Dest gets the opposite
            bool isDebit;
            if (isSource)
            {
                isDebit = txn.TransactionType == TransactionType.Debit;
            }
            else
            {
                // Dest gets opposite: if source is Debit, dest is Credit and vice versa
                isDebit = txn.TransactionType == TransactionType.Credit;
            }

            if (isDebit)
                totalDebit += amount;
            else
                totalCredit += amount;
        }

        return (totalDebit, totalCredit);
    }

    /// <summary>
    /// Calculates debit and credit totals per currency for multi-currency accounts (like clients)
    /// </summary>
    public (decimal debitKES, decimal creditKES, decimal debitUSD, decimal creditUSD) CalculateTransactionTotalsByCurrency(
        List<Transaction> transactions,
        AccountType accountType,
        Guid accountId)
    {
        decimal debitKES = 0, creditKES = 0, debitUSD = 0, creditUSD = 0;

        foreach (var txn in transactions)
        {
            var isSource = txn.SourceAccountType == accountType && txn.SourceAccountId == accountId;
            
            decimal amount;
            Currency currency;
            
            if (isSource)
            {
                amount = txn.Amount;
                currency = txn.Currency;
            }
            else
            {
                amount = txn.CounterAmount ?? txn.Amount;
                currency = txn.CounterCurrency ?? txn.Currency;
            }

            // Source gets transaction type, Dest gets opposite
            bool isDebit = isSource 
                ? txn.TransactionType == TransactionType.Debit 
                : txn.TransactionType == TransactionType.Credit;

            if (currency == Currency.KES)
            {
                if (isDebit) debitKES += amount;
                else creditKES += amount;
            }
            else
            {
                if (isDebit) debitUSD += amount;
                else creditUSD += amount;
            }
        }

        return (debitKES, creditKES, debitUSD, creditUSD);
    }

    /// <summary>
    /// Helper to determine if an account type is an asset
    /// </summary>
    public static bool IsAssetAccount(AccountType accountType)
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

    /// <summary>
    /// FIX #1 (CRITICAL): Build statement lines with DYNAMICALLY COMPUTED running balances.
    /// 
    /// The original code stored SourceBalanceAfter/DestBalanceAfter as frozen point-in-time snapshots.
    /// When a transaction is reversed/deleted, subsequent transactions still show stale balance-after values.
    /// 
    /// This method computes the running balance from the opening balance forward, so statements
    /// are always accurate regardless of reversals.
    /// 
    /// Transactions MUST be ordered chronologically (oldest first) before calling this method.
    /// </summary>
    public async Task<List<StatementLineDto>> BuildStatementLinesWithRunningBalanceAsync(
        List<Transaction> transactions,
        AccountType viewerAccountType,
        Guid viewerAccountId,
        decimal openingBalance,
        Currency? viewerCurrency = null,
        decimal? openingBalanceSecondary = null,
        Currency? secondaryCurrency = null)
    {
        var lines = new List<StatementLineDto>();

        // Track running balance PER CURRENCY.
        // Client accounts have KES + USD. Other accounts have one currency.
        var runningBalances = new Dictionary<Currency, decimal>();

        if (viewerCurrency.HasValue)
            runningBalances[viewerCurrency.Value] = openingBalance;

        if (secondaryCurrency.HasValue && openingBalanceSecondary.HasValue)
            runningBalances[secondaryCurrency.Value] = openingBalanceSecondary.Value;

        // Fallback: if no currency specified, use a single tracker
        decimal fallbackBalance = openingBalance;

        foreach (var txn in transactions)
        {
            var line = await CreateStatementLineAsync(txn, viewerAccountType, viewerAccountId, viewerCurrency);

            var lineCurrency = line.Currency;
            decimal balanceBefore, computedBalanceAfter;

            if (runningBalances.ContainsKey(lineCurrency))
            {
                // Currency-specific running balance
                balanceBefore = runningBalances[lineCurrency];

                if (IsAssetAccount(viewerAccountType))
                    computedBalanceAfter = balanceBefore + (line.Debit ?? 0) - (line.Credit ?? 0);
                else
                    computedBalanceAfter = balanceBefore - (line.Debit ?? 0) + (line.Credit ?? 0);

                runningBalances[lineCurrency] = computedBalanceAfter;
            }
            else if (!viewerCurrency.HasValue)
            {
                // No currency filter — single fallback balance
                balanceBefore = fallbackBalance;

                if (IsAssetAccount(viewerAccountType))
                    computedBalanceAfter = balanceBefore + (line.Debit ?? 0) - (line.Credit ?? 0);
                else
                    computedBalanceAfter = balanceBefore - (line.Debit ?? 0) + (line.Credit ?? 0);

                fallbackBalance = computedBalanceAfter;
            }
            else
            {
                // Transaction currency doesn't match any tracked currency — 
                // show it but don't compute balance (no opening balance for this currency)
                balanceBefore = 0;
                computedBalanceAfter = 0;
            }

            var correctedLine = line with
            {
                BalanceBefore = balanceBefore,
                BalanceAfter = computedBalanceAfter
            };

            lines.Add(correctedLine);
        }

        return lines;
    }
}