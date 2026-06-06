using Microsoft.EntityFrameworkCore;
using SARIFF.Core.DTOs;
using SARIFF.Core.Entities;
using SARIFF.Core.Enums;
using SARIFF.Core.Interfaces;
using SARIFF.Infrastructure.Data;

namespace SARIFF.Infrastructure.Services;

// =====================================================
// BANK ACCOUNT SERVICE - FIXED
// Change: NetMovement = OpeningBalance + Credits - Debits
// =====================================================
public class BankAccountService : IBankAccountService
{
    private readonly AppDbContext _context;
    private readonly StatementHelper _statementHelper;

    public BankAccountService(AppDbContext context)
    {
        _context = context;
        _statementHelper = new StatementHelper(context);
    }

    public async Task<ApiResponse<BankAccountResponseDto>> CreateAsync(Guid companyId, CreateBankAccountDto dto)
    {
        // Input validation
        var validationError = ValidationHelper.FirstError(
            ValidationHelper.ValidateName(dto.BankName, "Bank name"),
            ValidationHelper.ValidateAccountNumber(dto.AccountNumber, "Account number"),
            ValidationHelper.ValidateName(dto.AccountName, "Account name"),
            ValidationHelper.ValidateAmount(dto.OpeningBalance, "Opening balance", allowZero: true)
        );
        if (validationError != null)
            return new ApiResponse<BankAccountResponseDto>(false, validationError, null);

        if (await _context.BankAccounts.AnyAsync(b => b.CompanyId == companyId && b.AccountNumber == dto.AccountNumber && !b.IsDeleted))
            return new ApiResponse<BankAccountResponseDto>(false, "Account number already exists", null);

        const int maxRetries = 3;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            var code = await CodeGenerator.GenerateBankAccountCodeAsync(_context, companyId);
            var account = new BankAccount
            {
                CompanyId = companyId,
                Code = code,
                BankName = dto.BankName,
                AccountNumber = dto.AccountNumber,
                AccountName = dto.AccountName,
                BranchCode = dto.BranchCode,
                Currency = dto.Currency,
                Balance = dto.OpeningBalance,
                OpeningBalance = dto.OpeningBalance,
                IsActive = true
            };

            try
            {
                _context.BankAccounts.Add(account);
                await _context.SaveChangesAsync();
                return new ApiResponse<BankAccountResponseDto>(true, "Bank account created", await MapToResponseAsync(companyId, account));
            }
            catch (DbUpdateException) when (attempt < maxRetries - 1)
            {
                _context.Entry(account).State = EntityState.Detached;
                await Task.Delay(50 * (attempt + 1));
            }
        }
        return new ApiResponse<BankAccountResponseDto>(false, "Failed to generate unique code. Please try again.", null);
    }

    public async Task<ApiResponse<BankAccountResponseDto>> GetByIdAsync(Guid companyId, Guid id)
    {
        var account = await _context.BankAccounts.FirstOrDefaultAsync(b => b.Id == id && b.CompanyId == companyId && !b.IsDeleted);
        if (account == null)
            return new ApiResponse<BankAccountResponseDto>(false, "Bank account not found", null);
        return new ApiResponse<BankAccountResponseDto>(true, "Success", await MapToResponseAsync(companyId, account));
    }

    public async Task<ApiResponse<List<BankAccountResponseDto>>> GetAllAsync(Guid companyId)
    {
        var accounts = await _context.BankAccounts.Where(b => b.CompanyId == companyId && !b.IsDeleted).ToListAsync();
        var result = new List<BankAccountResponseDto>();
        foreach (var account in accounts)
        {
            result.Add(await MapToResponseAsync(companyId, account));
        }
        return new ApiResponse<List<BankAccountResponseDto>>(true, "Success", result);
    }

    public async Task<ApiResponse<BankAccountResponseDto>> UpdateAsync(Guid companyId, Guid id, UpdateBankAccountDto dto)
    {
        var account = await _context.BankAccounts.FirstOrDefaultAsync(b => b.Id == id && b.CompanyId == companyId && !b.IsDeleted);
        if (account == null)
            return new ApiResponse<BankAccountResponseDto>(false, "Bank account not found", null);

        if (dto.BankName != null)
        {
            var check = ValidationHelper.ValidateName(dto.BankName, "Bank name");
            if (!check.IsValid) return new ApiResponse<BankAccountResponseDto>(false, check.Error!, null);
            account.BankName = dto.BankName;
        }
        if (dto.AccountName != null)
        {
            var check = ValidationHelper.ValidateName(dto.AccountName, "Account name");
            if (!check.IsValid) return new ApiResponse<BankAccountResponseDto>(false, check.Error!, null);
            account.AccountName = dto.AccountName;
        }
        if (dto.BranchCode != null) account.BranchCode = dto.BranchCode;
        if (dto.IsActive.HasValue) account.IsActive = dto.IsActive.Value;
        account.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return new ApiResponse<BankAccountResponseDto>(true, "Bank account updated", await MapToResponseAsync(companyId, account));
    }

    public async Task<ApiResponse<bool>> DeleteAsync(Guid companyId, Guid id)
    {
        var account = await _context.BankAccounts.FirstOrDefaultAsync(b => b.Id == id && b.CompanyId == companyId && !b.IsDeleted);
        if (account == null)
            return new ApiResponse<bool>(false, "Bank account not found", false);

        // Check if there are any transactions using this account
        var hasTransactions = await _context.Transactions
            .AnyAsync(t => t.CompanyId == companyId && !t.IsDeleted &&
                ((t.SourceAccountType == AccountType.Bank && t.SourceAccountId == id) ||
                 (t.DestAccountType == AccountType.Bank && t.DestAccountId == id)));

        if (hasTransactions)
            return new ApiResponse<bool>(false,
                "Cannot delete bank account with existing transactions. Please delete or reverse all transactions first.", false);

        account.IsDeleted = true;
        account.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return new ApiResponse<bool>(true, "Bank account deleted", true);
    }

    public async Task<ApiResponse<BankAccountStatsDto>> GetStatsAsync(Guid companyId)
    {
        var accounts = await _context.BankAccounts.Where(b => b.CompanyId == companyId && !b.IsDeleted).ToListAsync();
        var accountIds = accounts.Select(a => a.Id).ToList();
        
        var transactions = await _context.Transactions
            .Where(t => t.CompanyId == companyId && !t.IsDeleted &&
                       ((t.SourceAccountType == AccountType.Bank && accountIds.Contains(t.SourceAccountId)) ||
                        (t.DestAccountType == AccountType.Bank && accountIds.Contains(t.DestAccountId))))
            .ToListAsync();

        decimal totalDebitKES = 0, totalCreditKES = 0, openingBalanceKES = 0;
        decimal totalDebitUSD = 0, totalCreditUSD = 0, openingBalanceUSD = 0;

        foreach (var account in accounts)
        {
            var accountTxns = transactions.Where(t =>
                (t.SourceAccountType == AccountType.Bank && t.SourceAccountId == account.Id) ||
                (t.DestAccountType == AccountType.Bank && t.DestAccountId == account.Id)).ToList();

            var (debit, credit) = _statementHelper.CalculateTransactionTotals(accountTxns, AccountType.Bank, account.Id);

            if (account.Currency == Currency.KES)
            {
                totalDebitKES += debit;
                totalCreditKES += credit;
                openingBalanceKES += account.OpeningBalance;
            }
            else
            {
                totalDebitUSD += debit;
                totalCreditUSD += credit;
                openingBalanceUSD += account.OpeningBalance;
            }
        }

        // ✅ FIXED: NetMovement = OpeningBalance + Credits - Debits
        var stats = new BankAccountStatsDto(
            TotalAccounts: accounts.Count,
            TotalBalanceKES: accounts.Where(a => a.Currency == Currency.KES).Sum(a => a.Balance),
            TotalBalanceUSD: accounts.Where(a => a.Currency == Currency.USD).Sum(a => a.Balance),
            TotalDebitKES: totalDebitKES,
            TotalCreditKES: totalCreditKES,
            NetMovementKES: openingBalanceKES + totalDebitKES - totalCreditKES,
            TotalDebitUSD: totalDebitUSD,
            TotalCreditUSD: totalCreditUSD,
            NetMovementUSD: openingBalanceUSD + totalDebitUSD - totalCreditUSD
        );

        return new ApiResponse<BankAccountStatsDto>(true, "Success", stats);
    }

    public async Task<ApiResponse<StatementDto>> GetStatementAsync(Guid companyId, Guid id, StatementFilterDto filter)
    {
        var account = await _context.BankAccounts.FirstOrDefaultAsync(b => b.Id == id && b.CompanyId == companyId && !b.IsDeleted);
        if (account == null)
            return new ApiResponse<StatementDto>(false, "Bank account not found", null);

        var query = _context.Transactions.Where(t => t.CompanyId == companyId && !t.IsDeleted &&
            ((t.SourceAccountType == AccountType.Bank && t.SourceAccountId == id) ||
             (t.DestAccountType == AccountType.Bank && t.DestAccountId == id)));

        if (filter.StartDate.HasValue) query = query.Where(t => t.TransactionDate >= filter.StartDate.Value);
        if (filter.EndDate.HasValue) query = query.Where(t => t.TransactionDate <= filter.EndDate.Value);
        if (filter.TransactionType.HasValue) query = query.Where(t => t.TransactionType == filter.TransactionType.Value);

        // FIX #1: Sort chronologically for running balance computation
        var transactions = await query.OrderBy(t => t.TransactionDate).ThenBy(t => t.CreatedAt).ToListAsync();

        // FIX #1: Use dynamic running balance instead of frozen snapshots
        var lines = await _statementHelper.BuildStatementLinesWithRunningBalanceAsync(
            transactions, AccountType.Bank, id, account.OpeningBalance, account.Currency);

        // Reverse for display (newest first)
        lines.Reverse();

        decimal totalDebit = lines.Sum(l => l.Debit ?? 0);
        decimal totalCredit = lines.Sum(l => l.Credit ?? 0);

        return new ApiResponse<StatementDto>(true, "Success", new StatementDto(
            AccountName: $"{account.BankName} - {account.AccountNumber}",
            AccountCode: account.Code,
            AccountType: AccountType.Bank,
            Currency: account.Currency,
            PeriodStart: filter.StartDate,
            PeriodEnd: filter.EndDate,
            OpeningBalance: account.OpeningBalance,
            ClosingBalance: account.Balance,
            TotalDebit: totalDebit,
            TotalCredit: totalCredit,
            NetMovement: account.Balance - account.OpeningBalance,
            Transactions: lines
        ));
    }

    private async Task<BankAccountResponseDto> MapToResponseAsync(Guid companyId, BankAccount account)
    {
        var transactions = await _context.Transactions
            .Where(t => t.CompanyId == companyId && !t.IsDeleted &&
                       ((t.SourceAccountType == AccountType.Bank && t.SourceAccountId == account.Id) ||
                        (t.DestAccountType == AccountType.Bank && t.DestAccountId == account.Id)))
            .ToListAsync();

        var (totalDebit, totalCredit) = _statementHelper.CalculateTransactionTotals(transactions, AccountType.Bank, account.Id);

        // ✅ FIXED: NetMovement = OpeningBalance + Credits - Debits
        return new BankAccountResponseDto(
            account.Id, account.Code, account.BankName, account.AccountNumber, account.AccountName, account.BranchCode,
            account.Currency, account.Balance, account.OpeningBalance,
            totalDebit, totalCredit, account.OpeningBalance + totalDebit-totalCredit,
            account.IsActive, account.CreatedAt
        );
    }
}

// =====================================================
// M-PESA AGENT SERVICE - FIXED
// =====================================================
public class MpesaAgentService : IMpesaAgentService
{
    private readonly AppDbContext _context;
    private readonly StatementHelper _statementHelper;

    public MpesaAgentService(AppDbContext context)
    {
        _context = context;
        _statementHelper = new StatementHelper(context);
    }

    public async Task<ApiResponse<MpesaAgentResponseDto>> CreateAsync(Guid companyId, CreateMpesaAgentDto dto)
    {
        // Input validation
        var validationError = ValidationHelper.FirstError(
            ValidationHelper.ValidateName(dto.AgentName, "Agent name"),
            ValidationHelper.ValidatePhone(dto.PhoneNumber, "Phone number"),
            ValidationHelper.ValidateText(dto.AgentNumber, "Agent number", 50, required: true),
            ValidationHelper.ValidateAmount(dto.OpeningBalance, "Opening balance", allowZero: true)
        );
        if (validationError != null)
            return new ApiResponse<MpesaAgentResponseDto>(false, validationError, null);

        if (await _context.MpesaAgents.AnyAsync(m => m.CompanyId == companyId && m.AgentNumber == dto.AgentNumber && !m.IsDeleted))
            return new ApiResponse<MpesaAgentResponseDto>(false, "Agent number already exists", null);

        const int maxRetries = 3;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            var code = await CodeGenerator.GenerateMpesaAgentCodeAsync(_context, companyId);
            var agent = new MpesaAgent
            {
                CompanyId = companyId,
                Code = code,
                AgentName = dto.AgentName,
                PhoneNumber = dto.PhoneNumber,
                AgentNumber = dto.AgentNumber,
                StoreNumber = dto.StoreNumber,
                AgentType = dto.AgentType,
                Balance = dto.OpeningBalance,
                OpeningBalance = dto.OpeningBalance,
                IsActive = true
            };

            try
            {
                _context.MpesaAgents.Add(agent);
                await _context.SaveChangesAsync();
                return new ApiResponse<MpesaAgentResponseDto>(true, "M-Pesa agent created", await MapToResponseAsync(companyId, agent));
            }
            catch (DbUpdateException) when (attempt < maxRetries - 1)
            {
                _context.Entry(agent).State = EntityState.Detached;
                await Task.Delay(50 * (attempt + 1));
            }
        }
        return new ApiResponse<MpesaAgentResponseDto>(false, "Failed to generate unique code. Please try again.", null);
    }

    public async Task<ApiResponse<MpesaAgentResponseDto>> GetByIdAsync(Guid companyId, Guid id)
    {
        var agent = await _context.MpesaAgents.FirstOrDefaultAsync(m => m.Id == id && m.CompanyId == companyId && !m.IsDeleted);
        if (agent == null) return new ApiResponse<MpesaAgentResponseDto>(false, "M-Pesa agent not found", null);
        return new ApiResponse<MpesaAgentResponseDto>(true, "Success", await MapToResponseAsync(companyId, agent));
    }

    public async Task<ApiResponse<List<MpesaAgentResponseDto>>> GetAllAsync(Guid companyId)
    {
        var agents = await _context.MpesaAgents.Where(m => m.CompanyId == companyId && !m.IsDeleted).ToListAsync();
        var result = new List<MpesaAgentResponseDto>();
        foreach (var agent in agents)
        {
            result.Add(await MapToResponseAsync(companyId, agent));
        }
        return new ApiResponse<List<MpesaAgentResponseDto>>(true, "Success", result);
    }

    public async Task<ApiResponse<MpesaAgentResponseDto>> UpdateAsync(Guid companyId, Guid id, UpdateMpesaAgentDto dto)
    {
        var agent = await _context.MpesaAgents.FirstOrDefaultAsync(m => m.Id == id && m.CompanyId == companyId && !m.IsDeleted);
        if (agent == null) return new ApiResponse<MpesaAgentResponseDto>(false, "M-Pesa agent not found", null);

        if (dto.AgentName != null)
        {
            var check = ValidationHelper.ValidateName(dto.AgentName, "Agent name");
            if (!check.IsValid) return new ApiResponse<MpesaAgentResponseDto>(false, check.Error!, null);
            agent.AgentName = dto.AgentName;
        }
        if (dto.StoreNumber != null) agent.StoreNumber = dto.StoreNumber;
        if (dto.AgentType.HasValue) agent.AgentType = dto.AgentType.Value;
        if (dto.IsActive.HasValue) agent.IsActive = dto.IsActive.Value;
        agent.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return new ApiResponse<MpesaAgentResponseDto>(true, "M-Pesa agent updated", await MapToResponseAsync(companyId, agent));
    }

    public async Task<ApiResponse<bool>> DeleteAsync(Guid companyId, Guid id)
    {
        var agent = await _context.MpesaAgents.FirstOrDefaultAsync(m => m.Id == id && m.CompanyId == companyId && !m.IsDeleted);
        if (agent == null) return new ApiResponse<bool>(false, "M-Pesa agent not found", false);

        // Check if there are any transactions using this agent
        var hasTransactions = await _context.Transactions
            .AnyAsync(t => t.CompanyId == companyId && !t.IsDeleted &&
                ((t.SourceAccountType == AccountType.Mpesa && t.SourceAccountId == id) ||
                 (t.DestAccountType == AccountType.Mpesa && t.DestAccountId == id)));

        if (hasTransactions)
            return new ApiResponse<bool>(false,
                "Cannot delete M-Pesa agent with existing transactions. Please delete or reverse all transactions first.", false);

        agent.IsDeleted = true;
        agent.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return new ApiResponse<bool>(true, "M-Pesa agent deleted", true);
    }

    public async Task<ApiResponse<MpesaAgentStatsDto>> GetStatsAsync(Guid companyId)
    {
        var agents = await _context.MpesaAgents.Where(m => m.CompanyId == companyId && !m.IsDeleted).ToListAsync();
        var agentIds = agents.Select(a => a.Id).ToList();
        
        var transactions = await _context.Transactions
            .Where(t => t.CompanyId == companyId && !t.IsDeleted &&
                       ((t.SourceAccountType == AccountType.Mpesa && agentIds.Contains(t.SourceAccountId)) ||
                        (t.DestAccountType == AccountType.Mpesa && agentIds.Contains(t.DestAccountId))))
            .ToListAsync();

        decimal totalDebit = 0, totalCredit = 0;
        decimal totalOpeningBalance = agents.Sum(a => a.OpeningBalance);

        foreach (var agent in agents)
        {
            var agentTxns = transactions.Where(t =>
                (t.SourceAccountType == AccountType.Mpesa && t.SourceAccountId == agent.Id) ||
                (t.DestAccountType == AccountType.Mpesa && t.DestAccountId == agent.Id)).ToList();

            var (debit, credit) = _statementHelper.CalculateTransactionTotals(agentTxns, AccountType.Mpesa, agent.Id);
            totalDebit += debit;
            totalCredit += credit;
        }

        // ✅ FIXED: NetMovement = OpeningBalance + Credits - Debits
        return new ApiResponse<MpesaAgentStatsDto>(true, "Success", new MpesaAgentStatsDto(
            agents.Count, 
            agents.Sum(a => a.Balance),
            totalDebit,
            totalCredit,
            totalOpeningBalance +  totalDebit-totalCredit
        ));
    }

    public async Task<ApiResponse<StatementDto>> GetStatementAsync(Guid companyId, Guid id, StatementFilterDto filter)
    {
        var agent = await _context.MpesaAgents.FirstOrDefaultAsync(m => m.Id == id && m.CompanyId == companyId && !m.IsDeleted);
        if (agent == null) return new ApiResponse<StatementDto>(false, "M-Pesa agent not found", null);

        var query = _context.Transactions.Where(t => t.CompanyId == companyId && !t.IsDeleted &&
            ((t.SourceAccountType == AccountType.Mpesa && t.SourceAccountId == id) ||
             (t.DestAccountType == AccountType.Mpesa && t.DestAccountId == id)));

        if (filter.StartDate.HasValue) query = query.Where(t => t.TransactionDate >= filter.StartDate.Value);
        if (filter.EndDate.HasValue) query = query.Where(t => t.TransactionDate <= filter.EndDate.Value);
        if (filter.TransactionType.HasValue) query = query.Where(t => t.TransactionType == filter.TransactionType.Value);

        // FIX #1: Sort chronologically for running balance computation
        var transactions = await query.OrderBy(t => t.TransactionDate).ThenBy(t => t.CreatedAt).ToListAsync();

        // FIX #1: Use dynamic running balance instead of frozen snapshots
        var lines = await _statementHelper.BuildStatementLinesWithRunningBalanceAsync(
            transactions, AccountType.Mpesa, id, agent.OpeningBalance, Currency.KES);

        // Reverse for display (newest first)
        lines.Reverse();

        decimal totalDebit = lines.Sum(l => l.Debit ?? 0);
        decimal totalCredit = lines.Sum(l => l.Credit ?? 0);

        return new ApiResponse<StatementDto>(true, "Success", new StatementDto(
            AccountName: $"{agent.AgentName} - {agent.AgentNumber}",
            AccountCode: agent.Code,
            AccountType: AccountType.Mpesa,
            Currency: Currency.KES,
            PeriodStart: filter.StartDate,
            PeriodEnd: filter.EndDate,
            OpeningBalance: agent.OpeningBalance,
            ClosingBalance: agent.Balance,
            TotalDebit: totalDebit,
            TotalCredit: totalCredit,
            NetMovement: agent.Balance - agent.OpeningBalance,
            Transactions: lines
        ));
    }

    private async Task<MpesaAgentResponseDto> MapToResponseAsync(Guid companyId, MpesaAgent agent)
    {
        var transactions = await _context.Transactions
            .Where(t => t.CompanyId == companyId && !t.IsDeleted &&
                       ((t.SourceAccountType == AccountType.Mpesa && t.SourceAccountId == agent.Id) ||
                        (t.DestAccountType == AccountType.Mpesa && t.DestAccountId == agent.Id)))
            .ToListAsync();

        var (totalDebit, totalCredit) = _statementHelper.CalculateTransactionTotals(transactions, AccountType.Mpesa, agent.Id);

        // ✅ FIXED: NetMovement = OpeningBalance + Credits - Debits
        return new MpesaAgentResponseDto(
            agent.Id, agent.Code, agent.AgentName, agent.PhoneNumber, agent.AgentNumber, agent.StoreNumber,
            agent.AgentType, agent.Balance, agent.OpeningBalance,
            totalDebit, totalCredit, agent.OpeningBalance + totalDebit-totalCredit,
            agent.IsActive, agent.CreatedAt
        );
    }
}

public class CashAccountService : ICashAccountService
{
    private readonly AppDbContext _context;
    private readonly StatementHelper _statementHelper;

    public CashAccountService(AppDbContext context)
    {
        _context = context;
        _statementHelper = new StatementHelper(context);
    }

    /// <summary>
    /// NEW: Create a cash account with opening balance
    /// </summary>
    public async Task<ApiResponse<CashAccountResponseDto>> CreateAsync(Guid companyId, CreateCashAccountDto dto)
    {
        // Check if cash account for this currency already exists
        var existing = await _context.CashAccounts
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Currency == dto.Currency && !c.IsDeleted);

        if (existing != null)
        {
            return new ApiResponse<CashAccountResponseDto>(false, 
                $"Cash account for {dto.Currency} already exists. Use update to change opening balance.", null);
        }

        var account = new CashAccount
        {
            CompanyId = companyId,
            Currency = dto.Currency,
            Balance = dto.OpeningBalance,
            OpeningBalance = dto.OpeningBalance,
            Code = $"CASH-{dto.Currency}",  // FIX #5: Set Code to avoid NULL constraint
            IsActive = true                  // FIX #4: Now correctly typed as bool
        };

        _context.CashAccounts.Add(account);
        await _context.SaveChangesAsync();

        return new ApiResponse<CashAccountResponseDto>(true, "Cash account created", 
            await MapToResponseAsync(companyId, account));
    }

    /// <summary>
    /// NEW: Get a specific cash account by ID
    /// </summary>
    public async Task<ApiResponse<CashAccountResponseDto>> GetByIdAsync(Guid companyId, Guid id)
    {
        var account = await _context.CashAccounts
            .FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId && !c.IsDeleted);

        if (account == null)
            return new ApiResponse<CashAccountResponseDto>(false, "Cash account not found", null);

        return new ApiResponse<CashAccountResponseDto>(true, "Success", 
            await MapToResponseAsync(companyId, account));
    }

    /// <summary>
    /// NEW: Get cash account by currency
    /// </summary>
    public async Task<ApiResponse<CashAccountResponseDto>> GetByCurrencyAsync(Guid companyId, Currency currency)
    {
        var account = await _context.CashAccounts
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Currency == currency && !c.IsDeleted);

        if (account == null)
            return new ApiResponse<CashAccountResponseDto>(false, $"Cash account for {currency} not found", null);

        return new ApiResponse<CashAccountResponseDto>(true, "Success", 
            await MapToResponseAsync(companyId, account));
    }

    public async Task<ApiResponse<List<CashAccountResponseDto>>> GetAllAsync(Guid companyId)
    {
        var accounts = await _context.CashAccounts
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .ToListAsync();

        var result = new List<CashAccountResponseDto>();

        foreach (var account in accounts)
        {
            result.Add(await MapToResponseAsync(companyId, account));
        }

        return new ApiResponse<List<CashAccountResponseDto>>(true, "Success", result);
    }

    /// <summary>
    /// NEW: Update cash account opening balance
    /// 
    /// IMPORTANT: This recalculates the current balance based on:
    /// NewBalance = NewOpeningBalance + TotalDebits - TotalCredits
    /// </summary>
    public async Task<ApiResponse<CashAccountResponseDto>> UpdateAsync(Guid companyId, Guid id, UpdateCashAccountDto dto)
    {
        var account = await _context.CashAccounts
            .FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId && !c.IsDeleted);

        if (account == null)
            return new ApiResponse<CashAccountResponseDto>(false, "Cash account not found", null);

        if (dto.OpeningBalance.HasValue)
        {
            var oldOpeningBalance = account.OpeningBalance;
            var newOpeningBalance = dto.OpeningBalance.Value;
            var difference = newOpeningBalance - oldOpeningBalance;

            // Adjust current balance by the same difference
            account.OpeningBalance = newOpeningBalance;
            account.Balance += difference;
            account.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        return new ApiResponse<CashAccountResponseDto>(true, "Cash account updated", 
            await MapToResponseAsync(companyId, account));
    }

    /// <summary>
    /// NEW: Soft delete cash account
    /// </summary>
    public async Task<ApiResponse<bool>> DeleteAsync(Guid companyId, Guid id)
    {
        var account = await _context.CashAccounts
            .FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId && !c.IsDeleted);

        if (account == null)
            return new ApiResponse<bool>(false, "Cash account not found", false);

        // Check if there are any transactions using this account
        var hasTransactions = await _context.Transactions
            .AnyAsync(t => t.CompanyId == companyId && !t.IsDeleted &&
                ((t.SourceAccountType == AccountType.Cash && t.Currency == account.Currency) ||
                 (t.DestAccountType == AccountType.Cash && (t.CounterCurrency ?? t.Currency) == account.Currency)));

        if (hasTransactions)
        {
            return new ApiResponse<bool>(false, 
                "Cannot delete cash account with existing transactions. Please delete or reverse all transactions first.", false);
        }

        account.IsDeleted = true;
        account.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return new ApiResponse<bool>(true, "Cash account deleted", true);
    }

    public async Task<ApiResponse<CashStatsDto>> GetStatsAsync(Guid companyId)
    {
        var accounts = await _context.CashAccounts
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .ToListAsync();

        var kesAccount = accounts.FirstOrDefault(a => a.Currency == Currency.KES);
        var usdAccount = accounts.FirstOrDefault(a => a.Currency == Currency.USD);

        var (debitKES, creditKES) = await CalculateCashTotalsAsync(companyId, Currency.KES);
        var (debitUSD, creditUSD) = await CalculateCashTotalsAsync(companyId, Currency.USD);

        var openingKES = kesAccount?.OpeningBalance ?? 0;
        var openingUSD = usdAccount?.OpeningBalance ?? 0;

        // FIXED: Consistent NetMovement calculation
        // For ASSET accounts: NetMovement = OpeningBalance + Debits - Credits
        return new ApiResponse<CashStatsDto>(true, "Success", new CashStatsDto(
            BalanceKES: kesAccount?.Balance ?? 0,
            OpeningBalanceKES: openingKES,
            TotalDebitKES: debitKES,
            TotalCreditKES: creditKES,
            NetMovementKES: openingKES + debitKES - creditKES,
            BalanceUSD: usdAccount?.Balance ?? 0,
            OpeningBalanceUSD: openingUSD,
            TotalDebitUSD: debitUSD,
            TotalCreditUSD: creditUSD,
            NetMovementUSD: openingUSD + debitUSD - creditUSD
        ));
    }

    public async Task<ApiResponse<StatementDto>> GetStatementAsync(Guid companyId, Currency currency, StatementFilterDto filter)
    {
        var account = await _context.CashAccounts
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Currency == currency && !c.IsDeleted);

        if (account == null)
            return new ApiResponse<StatementDto>(false, "Cash account not found", null);

        var query = _context.Transactions.Where(t => t.CompanyId == companyId && !t.IsDeleted &&
            ((t.SourceAccountType == AccountType.Cash && t.Currency == currency) ||
             (t.DestAccountType == AccountType.Cash && (t.CounterCurrency ?? t.Currency) == currency)));

        if (filter.StartDate.HasValue) query = query.Where(t => t.TransactionDate >= filter.StartDate.Value);
        if (filter.EndDate.HasValue) query = query.Where(t => t.TransactionDate <= filter.EndDate.Value);
        if (filter.TransactionType.HasValue) query = query.Where(t => t.TransactionType == filter.TransactionType.Value);

        // FIX #1: Sort chronologically for running balance computation
        var transactions = await query
            .OrderBy(t => t.TransactionDate)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync();

        // FIX #1: Use dynamic running balance instead of frozen snapshots
        var lines = await _statementHelper.BuildStatementLinesWithRunningBalanceAsync(
            transactions, AccountType.Cash, account.Id, account.OpeningBalance, currency);

        // Reverse for display (newest first)
        lines.Reverse();

        decimal totalDebit = lines.Sum(l => l.Debit ?? 0);
        decimal totalCredit = lines.Sum(l => l.Credit ?? 0);

        return new ApiResponse<StatementDto>(true, "Success", new StatementDto(
            AccountName: $"Cash {currency}",
            AccountCode: $"CASH-{currency}",
            AccountType: AccountType.Cash,
            Currency: currency,
            PeriodStart: filter.StartDate,
            PeriodEnd: filter.EndDate,
            OpeningBalance: account.OpeningBalance,
            ClosingBalance: account.Balance,
            TotalDebit: totalDebit,
            TotalCredit: totalCredit,
            NetMovement: account.Balance - account.OpeningBalance,
            Transactions: lines
        ));
    }

    private async Task<(decimal debit, decimal credit)> CalculateCashTotalsAsync(Guid companyId, Currency currency)
    {
        var transactions = await _context.Transactions
            .Where(t => t.CompanyId == companyId && !t.IsDeleted &&
                       ((t.SourceAccountType == AccountType.Cash && t.Currency == currency) ||
                        (t.DestAccountType == AccountType.Cash && (t.CounterCurrency ?? t.Currency) == currency)))
            .ToListAsync();

        return _statementHelper.CalculateTransactionTotals(transactions, AccountType.Cash, Guid.Empty, currency);
    }

    private async Task<CashAccountResponseDto> MapToResponseAsync(Guid companyId, CashAccount account)
    {
        var (totalDebit, totalCredit) = await CalculateCashTotalsAsync(companyId, account.Currency);

        return new CashAccountResponseDto(
            account.Id,
            account.Currency,
            account.Balance,
            account.OpeningBalance,
            totalDebit,
            totalCredit,
            account.OpeningBalance + totalDebit - totalCredit,
            account.CreatedAt
        );
    }
}

public class ExpenseService : IExpenseService
{
    private readonly AppDbContext _context;
    private readonly ISystemLogService _systemLog;

    public ExpenseService(AppDbContext context, ISystemLogService systemLog)
    {
        _context = context;
        _systemLog = systemLog;
    }

    public async Task<ApiResponse<ExpenseCategoryResponseDto>> CreateCategoryAsync(Guid companyId, CreateExpenseCategoryDto dto)
    {
        var nameCheck = ValidationHelper.ValidateName(dto.Name, "Category name");
        if (!nameCheck.IsValid)
            return new ApiResponse<ExpenseCategoryResponseDto>(false, nameCheck.Error!, null);

        var descCheck = ValidationHelper.ValidateText(dto.Description, "Description", 500);
        if (!descCheck.IsValid)
            return new ApiResponse<ExpenseCategoryResponseDto>(false, descCheck.Error!, null);

        if (await _context.ExpenseCategories.AnyAsync(c => c.CompanyId == companyId && c.Name == dto.Name && !c.IsDeleted))
            return new ApiResponse<ExpenseCategoryResponseDto>(false, "Category already exists", null);

        var category = new ExpenseCategory
        {
            CompanyId = companyId,
            Name = dto.Name,
            Description = dto.Description,
            IsActive = true
        };
        
        _context.ExpenseCategories.Add(category);
        await _context.SaveChangesAsync();

        return new ApiResponse<ExpenseCategoryResponseDto>(true, "Category created",
            new ExpenseCategoryResponseDto(category.Id, category.Name, category.Description, category.IsActive, 0, 0, 0));
    }

    public async Task<ApiResponse<List<ExpenseCategoryResponseDto>>> GetCategoriesAsync(Guid companyId)
    {
        var categories = await _context.ExpenseCategories
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .ToListAsync();
            
        var expenses = await _context.Expenses
            .Where(e => e.CompanyId == companyId && !e.IsDeleted)
            .ToListAsync();

        var result = categories.Select(c => new ExpenseCategoryResponseDto(
            c.Id, c.Name, c.Description, c.IsActive,
            expenses.Where(e => e.CategoryId == c.Id && e.Currency == Currency.KES).Sum(e => e.Amount),
            expenses.Where(e => e.CategoryId == c.Id && e.Currency == Currency.USD).Sum(e => e.Amount),
            expenses.Count(e => e.CategoryId == c.Id))).ToList();

        return new ApiResponse<List<ExpenseCategoryResponseDto>>(true, "Success", result);
    }

    public async Task<ApiResponse<ExpenseCategoryResponseDto>> UpdateCategoryAsync(Guid companyId, Guid id, UpdateExpenseCategoryDto dto)
    {
        var category = await _context.ExpenseCategories
            .FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId && !c.IsDeleted);
            
        if (category == null) 
            return new ApiResponse<ExpenseCategoryResponseDto>(false, "Category not found", null);

        if (dto.Name != null) category.Name = dto.Name;
        if (dto.Description != null) category.Description = dto.Description;
        if (dto.IsActive.HasValue) category.IsActive = dto.IsActive.Value;
        category.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        
        return new ApiResponse<ExpenseCategoryResponseDto>(true, "Category updated",
            new ExpenseCategoryResponseDto(category.Id, category.Name, category.Description, category.IsActive, 0, 0, 0));
    }

    public async Task<ApiResponse<bool>> DeleteCategoryAsync(Guid companyId, Guid id)
    {
        var category = await _context.ExpenseCategories
            .FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId && !c.IsDeleted);
            
        if (category == null) 
            return new ApiResponse<bool>(false, "Category not found", false);

        // Check for existing expenses
        var hasExpenses = await _context.Expenses
            .AnyAsync(e => e.CategoryId == id && e.CompanyId == companyId && !e.IsDeleted);
            
        if (hasExpenses)
            return new ApiResponse<bool>(false, "Cannot delete category with existing expenses", false);

        category.IsDeleted = true;
        category.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        
        return new ApiResponse<bool>(true, "Category deleted", true);
    }

    /// <summary>
    /// FIXED: Create expense with proper transaction tracking
    /// </summary>
    public async Task<ApiResponse<ExpenseResponseDto>> CreateAsync(Guid companyId, Guid userId, CreateExpenseDto dto)
    {
        // Input validation
        var validationError = ValidationHelper.FirstError(
            ValidationHelper.ValidateAmount(dto.Amount, "Expense amount"),
            ValidationHelper.ValidateText(dto.Description, "Description", 500, required: true),
            ValidationHelper.ValidateText(dto.VendorPayee, "Vendor/Payee", 255),
            ValidationHelper.ValidateText(dto.Reference, "Reference", 50)
        );
        if (validationError != null)
            return new ApiResponse<ExpenseResponseDto>(false, validationError, null);

        using var dbTransaction = await _context.Database.BeginTransactionAsync();
        
        try
        {
            var code = await CodeGenerator.GenerateExpenseCodeAsync(_context, companyId);
            var expenseDate = DateTime.SpecifyKind(dto.ExpenseDate, DateTimeKind.Utc);

            // Get category name
            var category = await _context.ExpenseCategories.FindAsync(dto.CategoryId);
            if (category == null || category.CompanyId != companyId)
                return new ApiResponse<ExpenseResponseDto>(false, "Invalid category", null);

            var categoryName = category.Name;

            // Get payment account and validate
            decimal balanceBefore = 0;
            Guid paymentAccountId = dto.PaymentAccountId;

            switch (dto.PaymentAccountType)
            {
                case AccountType.Cash:
                    var cash = await _context.CashAccounts.FirstOrDefaultAsync(c =>
                        c.CompanyId == companyId && c.Currency == dto.Currency && !c.IsDeleted);
                    if (cash == null)
                        return new ApiResponse<ExpenseResponseDto>(false, $"No cash account found for {dto.Currency}", null);
                    balanceBefore = cash.Balance;
                    paymentAccountId = cash.Id;
                    
                    // Check sufficient balance
                    if (cash.Balance < dto.Amount)
                        return new ApiResponse<ExpenseResponseDto>(false, 
                            $"Insufficient balance. Available: {dto.Currency} {cash.Balance:N2}", null);
                    
                    cash.Balance -= dto.Amount;
                    break;

                case AccountType.Bank:
                    var bank = await _context.BankAccounts.FirstOrDefaultAsync(b =>
                        b.Id == dto.PaymentAccountId && b.CompanyId == companyId && !b.IsDeleted);
                    if (bank == null)
                        return new ApiResponse<ExpenseResponseDto>(false, "Bank account not found", null);
                    if (bank.Currency != dto.Currency)
                        return new ApiResponse<ExpenseResponseDto>(false, "Currency mismatch with bank account", null);
                    balanceBefore = bank.Balance;
                    
                    if (bank.Balance < dto.Amount)
                        return new ApiResponse<ExpenseResponseDto>(false, 
                            $"Insufficient balance. Available: {dto.Currency} {bank.Balance:N2}", null);
                    
                    bank.Balance -= dto.Amount;
                    break;

                case AccountType.Mpesa:
                    var mpesa = await _context.MpesaAgents.FirstOrDefaultAsync(m =>
                        m.Id == dto.PaymentAccountId && m.CompanyId == companyId && !m.IsDeleted);
                    if (mpesa == null)
                        return new ApiResponse<ExpenseResponseDto>(false, "M-Pesa agent not found", null);
                    if (dto.Currency != Currency.KES)
                        return new ApiResponse<ExpenseResponseDto>(false, "M-Pesa only supports KES", null);
                    balanceBefore = mpesa.Balance;
                    
                    if (mpesa.Balance < dto.Amount)
                        return new ApiResponse<ExpenseResponseDto>(false, 
                            $"Insufficient balance. Available: KES {mpesa.Balance:N2}", null);
                    
                    mpesa.Balance -= dto.Amount;
                    break;

                default:
                    return new ApiResponse<ExpenseResponseDto>(false, "Invalid payment account type", null);
            }

            decimal balanceAfter = balanceBefore - dto.Amount;

            // Create transaction code
            var txnCode = await CodeGenerator.GenerateTransactionCodeAsync(_context, companyId);

            // Create transaction record (Credit = money going OUT of payment account)
            var transaction = new Transaction
            {
                CompanyId = companyId,
                Code = txnCode,
                Reference = $"EXP-{code}",
                TransactionType = TransactionType.Credit, // Credit = money out
                TransactionDate = expenseDate,
                Description = $"Expense: {categoryName} - {dto.Description}",
                Notes = $"Vendor: {dto.VendorPayee ?? "N/A"} | Expense Code: {code}",
                Currency = dto.Currency,
                Amount = dto.Amount,
                // Source = Payment Account (where money comes FROM)
                SourceAccountType = dto.PaymentAccountType,
                SourceAccountId = paymentAccountId,
                SourceBalanceBefore = balanceBefore,
                SourceBalanceAfter = balanceAfter,
                // FIX #2: Dest = Expense category (proper double-entry, NOT same as source)
                DestAccountType = AccountType.Expense,
                DestAccountId = dto.CategoryId, // Expense category is the logical destination
                DestBalanceBefore = 0,
                DestBalanceAfter = dto.Amount,
                CreatedByUserId = userId,
                ReconciliationStatus = ReconciliationStatus.Pending
            };

            _context.Transactions.Add(transaction);

            // Create expense record
            var expense = new Expense
            {
                CompanyId = companyId,
                Code = code,
                CategoryId = dto.CategoryId,
                Description = dto.Description,
                VendorPayee = dto.VendorPayee,
                Amount = dto.Amount,
                Currency = dto.Currency,
                PaymentMethod = dto.PaymentMethod,
                PaymentAccountType = dto.PaymentAccountType,
                PaymentAccountId = paymentAccountId,
                Reference = dto.Reference,
                ExpenseDate = expenseDate,
                CreatedByUserId = userId,
                TransactionId = transaction.Id // Link to transaction
            };

            _context.Expenses.Add(expense);
            await _context.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            return new ApiResponse<ExpenseResponseDto>(true, "Expense recorded successfully", new ExpenseResponseDto(
                expense.Id, expense.Code, expense.CategoryId, categoryName, expense.Description, expense.VendorPayee,
                expense.Amount, expense.Currency, expense.PaymentMethod, dto.PaymentAccountType.ToString(),
                expense.Reference, expense.ExpenseDate, expense.CreatedAt));
        }
        catch (Exception ex)
        {
            await dbTransaction.RollbackAsync();
            await _systemLog.LogExpenseErrorAsync(companyId, $"Failed to create expense: {ex.Message}", userId);
            return new ApiResponse<ExpenseResponseDto>(false, "Failed to create expense. Please try again.", null);
        }
    }

    public async Task<ApiResponse<ExpenseResponseDto>> GetByIdAsync(Guid companyId, Guid id)
    {
        var expense = await _context.Expenses
            .FirstOrDefaultAsync(e => e.Id == id && e.CompanyId == companyId && !e.IsDeleted);
            
        if (expense == null) 
            return new ApiResponse<ExpenseResponseDto>(false, "Expense not found", null);

        var category = await _context.ExpenseCategories.FindAsync(expense.CategoryId);
        
        return new ApiResponse<ExpenseResponseDto>(true, "Success", new ExpenseResponseDto(
            expense.Id, expense.Code, expense.CategoryId, category?.Name ?? "Unknown", expense.Description,
            expense.VendorPayee, expense.Amount, expense.Currency, expense.PaymentMethod, 
            expense.PaymentAccountType.ToString(), expense.Reference, expense.ExpenseDate, expense.CreatedAt));
    }

    public async Task<ApiResponse<PagedResult<ExpenseResponseDto>>> GetAllAsync(Guid companyId, int page, int pageSize, ReportFilterDto? filter = null)
    {
        var query = _context.Expenses.Where(e => e.CompanyId == companyId && !e.IsDeleted);
        
        if (filter?.StartDate.HasValue == true) query = query.Where(e => e.ExpenseDate >= filter.StartDate.Value);
        if (filter?.EndDate.HasValue == true) query = query.Where(e => e.ExpenseDate <= filter.EndDate.Value);
        if (filter?.Currency.HasValue == true) query = query.Where(e => e.Currency == filter.Currency.Value);

        var totalCount = await query.CountAsync();
        var expenses = await query
            .OrderByDescending(e => e.ExpenseDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
            
        var categories = await _context.ExpenseCategories
            .Where(c => c.CompanyId == companyId)
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        var items = expenses.Select(e => new ExpenseResponseDto(
            e.Id, e.Code, e.CategoryId, categories.GetValueOrDefault(e.CategoryId, "Unknown"), e.Description,
            e.VendorPayee, e.Amount, e.Currency, e.PaymentMethod, e.PaymentAccountType.ToString(), 
            e.Reference, e.ExpenseDate, e.CreatedAt)).ToList();

        return new ApiResponse<PagedResult<ExpenseResponseDto>>(true, "Success",
            new PagedResult<ExpenseResponseDto>(items, totalCount, page, pageSize));
    }

    public async Task<ApiResponse<ExpenseStatsDto>> GetStatsAsync(Guid companyId)
    {
        var expenses = await _context.Expenses
            .Where(e => e.CompanyId == companyId && !e.IsDeleted)
            .ToListAsync();
            
        var categories = await _context.ExpenseCategories
            .CountAsync(c => c.CompanyId == companyId && c.IsActive && !c.IsDeleted);
            
        var thisMonth = DateTime.UtcNow.Month;
        var thisYear = DateTime.UtcNow.Year;

        return new ApiResponse<ExpenseStatsDto>(true, "Success", new ExpenseStatsDto(
            expenses.Where(e => e.Currency == Currency.KES).Sum(e => e.Amount),
            expenses.Where(e => e.Currency == Currency.USD).Sum(e => e.Amount),
            expenses.Where(e => e.Currency == Currency.KES && e.ExpenseDate.Month == thisMonth && e.ExpenseDate.Year == thisYear).Sum(e => e.Amount),
            expenses.Where(e => e.Currency == Currency.USD && e.ExpenseDate.Month == thisMonth && e.ExpenseDate.Year == thisYear).Sum(e => e.Amount),
            categories));
    }

    /// <summary>
    /// FIXED: Update expense with proper transaction handling
    /// Note: Only description, vendor, and reference can be updated
    /// Amount changes require delete + recreate for proper audit trail
    /// </summary>
    public async Task<ApiResponse<ExpenseResponseDto>> UpdateAsync(Guid companyId, Guid id, UpdateExpenseDto dto)
    {
        var expense = await _context.Expenses
            .FirstOrDefaultAsync(e => e.Id == id && e.CompanyId == companyId && !e.IsDeleted);

        if (expense == null)
            return new ApiResponse<ExpenseResponseDto>(false, "Expense not found", null);

        // SECURITY: Do not allow amount changes through update
        // Amount changes would corrupt the transaction chain
        // User must delete and recreate expense for amount changes
        if (dto.Amount.HasValue && dto.Amount.Value != expense.Amount)
        {
            return new ApiResponse<ExpenseResponseDto>(false, 
                "Amount cannot be changed. Please delete and recreate the expense for amount changes.", null);
        }

        // Update allowed fields only
        if (dto.Description != null) expense.Description = dto.Description;
        if (dto.VendorPayee != null) expense.VendorPayee = dto.VendorPayee;
        if (dto.Reference != null) expense.Reference = dto.Reference;
        expense.UpdatedAt = DateTime.UtcNow;

        // Update linked transaction description
        if (expense.TransactionId.HasValue)
        {
            var transaction = await _context.Transactions.FindAsync(expense.TransactionId.Value);
            if (transaction != null)
            {
                var category = await _context.ExpenseCategories.FindAsync(expense.CategoryId);
                transaction.Description = $"Expense: {category?.Name ?? "Unknown"} - {expense.Description}";
                transaction.Notes = $"Vendor: {expense.VendorPayee ?? "N/A"} | Expense Code: {expense.Code}";
                transaction.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _context.SaveChangesAsync();

        var cat = await _context.ExpenseCategories.FindAsync(expense.CategoryId);
        return new ApiResponse<ExpenseResponseDto>(true, "Expense updated", new ExpenseResponseDto(
            expense.Id, expense.Code, expense.CategoryId, cat?.Name ?? "Unknown", expense.Description,
            expense.VendorPayee, expense.Amount, expense.Currency, expense.PaymentMethod, 
            expense.PaymentAccountType.ToString(), expense.Reference, expense.ExpenseDate, expense.CreatedAt));
    }

    /// <summary>
    /// FIXED: Delete expense using proper accounting reversal
    /// Creates a reversal transaction instead of just restoring balance
    /// </summary>
    public async Task<ApiResponse<bool>> DeleteAsync(Guid companyId, Guid id, Guid userId)
    {
        using var dbTransaction = await _context.Database.BeginTransactionAsync();
        
        try
        {
            var expense = await _context.Expenses
                .FirstOrDefaultAsync(e => e.Id == id && e.CompanyId == companyId && !e.IsDeleted);

            if (expense == null)
                return new ApiResponse<bool>(false, "Expense not found", false);

            // Get current balance of payment account
            decimal currentBalance = 0;
            Guid paymentAccountId = expense.PaymentAccountId;

            switch (expense.PaymentAccountType)
            {
                case AccountType.Cash:
                    var cash = await _context.CashAccounts.FirstOrDefaultAsync(c =>
                        c.CompanyId == companyId && c.Currency == expense.Currency && !c.IsDeleted);
                    if (cash != null)
                    {
                        currentBalance = cash.Balance;
                        paymentAccountId = cash.Id;
                        cash.Balance += expense.Amount; // Reverse: add back
                    }
                    break;

                case AccountType.Bank:
                    var bank = await _context.BankAccounts.FirstOrDefaultAsync(b =>
                        b.Id == expense.PaymentAccountId && b.CompanyId == companyId && !b.IsDeleted);
                    if (bank != null)
                    {
                        currentBalance = bank.Balance;
                        bank.Balance += expense.Amount; // Reverse: add back
                    }
                    break;

                case AccountType.Mpesa:
                    var mpesa = await _context.MpesaAgents.FirstOrDefaultAsync(m =>
                        m.Id == expense.PaymentAccountId && m.CompanyId == companyId && !m.IsDeleted);
                    if (mpesa != null)
                    {
                        currentBalance = mpesa.Balance;
                        mpesa.Balance += expense.Amount; // Reverse: add back
                    }
                    break;
            }

            decimal newBalance = currentBalance + expense.Amount;

            // Create reversal transaction
            var reversalCode = await CodeGenerator.GenerateTransactionCodeAsync(_context, companyId);
            var category = await _context.ExpenseCategories.FindAsync(expense.CategoryId);

            var reversalTransaction = new Transaction
            {
                CompanyId = companyId,
                Code = reversalCode,
                Reference = $"REV-EXP-{expense.Code}",
                // FIXED: Reversal is the mirror of original.
                // Original: Source=Bank, Type=Credit (money OUT)
                // Reversal: Source=Bank, Type=Debit (money BACK IN)
                TransactionType = TransactionType.Debit,
                TransactionDate = DateTime.UtcNow,
                Description = $"REVERSAL: Expense {category?.Name ?? "Unknown"} - {expense.Description}",
                Notes = $"Reversal of expense {expense.Code}",
                Currency = expense.Currency,
                Amount = expense.Amount,
                SourceAccountType = expense.PaymentAccountType,
                SourceAccountId = paymentAccountId,
                SourceBalanceBefore = currentBalance,
                SourceBalanceAfter = newBalance,
                DestAccountType = AccountType.Expense,
                DestAccountId = expense.CategoryId,
                DestBalanceBefore = 0,
                DestBalanceAfter = 0,
                CreatedByUserId = userId,
                ReconciliationStatus = ReconciliationStatus.Matched
            };

            _context.Transactions.Add(reversalTransaction);

            // Soft delete the expense record (this is fine — expenses have their own list)
            expense.IsDeleted = true;
            expense.UpdatedAt = DateTime.UtcNow;

            // Mark original transaction as REVERSED — but keep it visible in statements
            // (same approach as TransactionService.DeleteAsync — Fix T)
            if (expense.TransactionId.HasValue)
            {
                var originalTransaction = await _context.Transactions.FindAsync(expense.TransactionId.Value);
                if (originalTransaction != null)
                {
                    // Do NOT set IsDeleted = true — that hides it from statements
                    originalTransaction.DeletedByUserId = userId;
                    originalTransaction.DeletedAt = DateTime.UtcNow;
                    originalTransaction.DeleteReason = "Expense deleted";
                    originalTransaction.Description = $"[REVERSED] {originalTransaction.Description}";
                    originalTransaction.Notes = (originalTransaction.Notes ?? "") + $" [Reversed by: {reversalCode}]";
                    originalTransaction.UpdatedAt = DateTime.UtcNow;
                }
            }

            await _context.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            await _systemLog.LogInfoAsync("ExpenseService", 
                $"Expense {expense.Code} deleted and reversed. Reversal: {reversalCode}", companyId, userId);

            return new ApiResponse<bool>(true, $"Expense deleted and reversed. Reversal code: {reversalCode}", true);
        }
        catch (Exception ex)
        {
            await dbTransaction.RollbackAsync();
            await _systemLog.LogExpenseErrorAsync(companyId, $"Failed to delete expense: {ex.Message}", userId);
            return new ApiResponse<bool>(false, "Failed to delete expense. Please try again.", false);
        }
    }
}

public class ExchangeRateService : IExchangeRateService
{
    private readonly AppDbContext _context;
    private readonly ITransactionService _transactionService;
    private readonly ISystemLogService _systemLog;

    public ExchangeRateService(
        AppDbContext context, 
        ITransactionService transactionService,
        ISystemLogService systemLog)
    {
        _context = context;
        _transactionService = transactionService;
        _systemLog = systemLog;
    }

    public async Task<ApiResponse<ExchangeRateResponseDto>> SetRateAsync(Guid companyId, Guid userId, SetExchangeRateDto dto)
    {
        // Validate rates
        if (dto.BuyRate <= 0 || dto.SellRate <= 0)
            return new ApiResponse<ExchangeRateResponseDto>(false, "Exchange rates must be positive", null);

        if (dto.BuyRate >= dto.SellRate)
            return new ApiResponse<ExchangeRateResponseDto>(false, "Buy rate must be less than sell rate", null);

        // Deactivate current rate
        var currentRate = await _context.ExchangeRates
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.IsActive);
            
        if (currentRate != null)
        {
            currentRate.IsActive = false;
            currentRate.EffectiveTo = DateTime.UtcNow;
        }

        var newRate = new ExchangeRate
        {
            CompanyId = companyId,
            BuyRate = dto.BuyRate,
            SellRate = dto.SellRate,
            EffectiveFrom = DateTime.UtcNow,
            IsActive = true,
            CreatedByUserId = userId
        };

        _context.ExchangeRates.Add(newRate);
        await _context.SaveChangesAsync();

        return new ApiResponse<ExchangeRateResponseDto>(true, "Exchange rate set", MapToResponse(newRate));
    }

    public async Task<ApiResponse<ExchangeRateResponseDto>> GetCurrentRateAsync(Guid companyId)
    {
        var rate = await _context.ExchangeRates
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.IsActive);
            
        if (rate == null) 
            return new ApiResponse<ExchangeRateResponseDto>(false, "No active exchange rate", null);
            
        return new ApiResponse<ExchangeRateResponseDto>(true, "Success", MapToResponse(rate));
    }

    public async Task<ApiResponse<List<ExchangeRateResponseDto>>> GetHistoryAsync(Guid companyId)
    {
        var rates = await _context.ExchangeRates
            .Where(r => r.CompanyId == companyId)
            .OrderByDescending(r => r.EffectiveFrom)
            .ToListAsync();
            
        return new ApiResponse<List<ExchangeRateResponseDto>>(true, "Success", rates.Select(MapToResponse).ToList());
    }

    public async Task<ApiResponse<CurrencyConvertResultDto>> ConvertAsync(Guid companyId, CurrencyConvertDto dto)
    {
        var rate = await _context.ExchangeRates
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.IsActive);
            
        if (rate == null) 
            return new ApiResponse<CurrencyConvertResultDto>(false, "No active exchange rate", null);

        if (rate.BuyRate <= 0 || rate.SellRate <= 0)
            return new ApiResponse<CurrencyConvertResultDto>(false, "Exchange rate is invalid (zero or negative)", null);

        decimal converted;
        decimal usedRate;

        if (dto.From == Currency.KES && dto.To == Currency.USD)
        {
            usedRate = rate.SellRate;  // FIX: Bureau SELLS USD at SellRate
            converted = dto.Amount / usedRate;
        }
        else
        {
            usedRate = rate.BuyRate;  // FIX: Bureau BUYS USD at BuyRate
            converted = dto.Amount * usedRate;
        }

        return new ApiResponse<CurrencyConvertResultDto>(true, "Success", 
            new CurrencyConvertResultDto(dto.Amount, dto.From, converted, dto.To, usedRate));
    }

    /// <summary>
    /// FIXED: Create exchange transaction with proper rollback
    /// 
    /// Exchange creates 2 transactions:
    /// 1. Receive FROM client (client gives currency A)
    /// 2. Give TO client (client receives currency B)
    /// 
    /// If transaction 2 fails, transaction 1 is now rolled back properly.
    /// </summary>
    public async Task<ApiResponse<TransactionResponseDto>> CreateExchangeTransactionAsync(
        Guid companyId, Guid userId, ExchangeTransactionDto dto)
    {
        // Use database transaction for atomicity
        using var dbTransaction = await _context.Database.BeginTransactionAsync();
        
        try
        {
            // Validate client exists
            var client = await _context.Users
                .FirstOrDefaultAsync(u => u.Id == dto.ClientId && u.CompanyId == companyId && !u.IsDeleted);
                
            if (client == null)
                return new ApiResponse<TransactionResponseDto>(false, "Client not found", null);

            // Get cash accounts
            var cashKES = await _context.CashAccounts
                .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Currency == Currency.KES && !c.IsDeleted);
            var cashUSD = await _context.CashAccounts
                .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Currency == Currency.USD && !c.IsDeleted);

            if (cashKES == null || cashUSD == null)
                return new ApiResponse<TransactionResponseDto>(false, 
                    "Cash accounts not found. Please create both KES and USD cash accounts.", null);

            // Validate exchange rate
            if (dto.ExchangeRate <= 0)
                return new ApiResponse<TransactionResponseDto>(false, "Invalid exchange rate", null);

            // Calculate the amount to give to client
            decimal amountTo;
            if (dto.CurrencyFrom == Currency.KES)
            {
                // Client gives KES, receives USD
                amountTo = dto.AmountFrom / dto.ExchangeRate;
                
                // Check if we have enough USD
                if (cashUSD.Balance < amountTo)
                    return new ApiResponse<TransactionResponseDto>(false, 
                        $"Insufficient USD balance. Available: ${cashUSD.Balance:N2}, Required: ${amountTo:N2}", null);
            }
            else
            {
                // Client gives USD, receives KES
                amountTo = dto.AmountFrom * dto.ExchangeRate;
                
                // Check if we have enough KES
                if (cashKES.Balance < amountTo)
                    return new ApiResponse<TransactionResponseDto>(false, 
                        $"Insufficient KES balance. Available: KES {cashKES.Balance:N2}, Required: KES {amountTo:N2}", null);
            }

            // Determine currency to give
            var currencyTo = dto.CurrencyFrom == Currency.KES ? Currency.USD : Currency.KES;

            // ==================== TRANSACTION 1 ====================
            // Receive FROM client - Client's account is DEBITED (money out from client)
            var txn1 = new CreateTransactionDto(
                TransactionType: TransactionType.Debit,
                SourceAccountType: AccountType.Client,
                SourceAccountId: dto.ClientId,
                DestAccountType: AccountType.Cash,
                DestAccountId: dto.CurrencyFrom == Currency.KES ? cashKES.Id : cashUSD.Id,
                Amount: dto.AmountFrom,
                Currency: dto.CurrencyFrom,
                CounterAmount: null,
                CounterCurrency: null,
                Description: $"Exchange: Client gives {dto.CurrencyFrom} {dto.AmountFrom:N2}",
                Notes: $"Exchange rate: {dto.ExchangeRate:N4}",
                ExchangeRate: dto.ExchangeRate,
                PaymentMethod: PaymentMethod.Cash
            );

            var result1 = await _transactionService.CreateAsync(companyId, userId, txn1);
            if (!result1.Success)
            {
                await dbTransaction.RollbackAsync();
                return new ApiResponse<TransactionResponseDto>(false, 
                    $"Failed to create receive transaction: {result1.Message}", null);
            }

            // ==================== TRANSACTION 2 ====================
            // Give TO client - Client's account is CREDITED (money in to client)
            var txn2 = new CreateTransactionDto(
                TransactionType: TransactionType.Credit,
                SourceAccountType: AccountType.Client,
                SourceAccountId: dto.ClientId,
                DestAccountType: AccountType.Cash,
                DestAccountId: currencyTo == Currency.KES ? cashKES.Id : cashUSD.Id,
                Amount: amountTo,
                Currency: currencyTo,
                CounterAmount: null,
                CounterCurrency: null,
                Description: $"Exchange: Client receives {currencyTo} {amountTo:N2}",
                Notes: $"Exchange rate: {dto.ExchangeRate:N4} | Original: {dto.CurrencyFrom} {dto.AmountFrom:N2}",
                ExchangeRate: dto.ExchangeRate,
                PaymentMethod: PaymentMethod.Cash
            );

            var result2 = await _transactionService.CreateAsync(companyId, userId, txn2);
            if (!result2.Success)
            {
                // FIXED: Rollback the entire database transaction
                // This will undo transaction 1 as well
                await dbTransaction.RollbackAsync();
                
                await _systemLog.LogErrorAsync("ExchangeRateService", 
                    $"Exchange failed on second transaction. Rolled back first transaction. Error: {result2.Message}",
                    null, companyId, userId);
                
                return new ApiResponse<TransactionResponseDto>(false, 
                    $"Exchange failed. All changes rolled back. Error: {result2.Message}", null);
            }

            // Both transactions successful - commit
            await dbTransaction.CommitAsync();
            
            await _systemLog.LogInfoAsync("ExchangeRateService", 
                $"Exchange completed: {dto.CurrencyFrom} {dto.AmountFrom:N2} -> {currencyTo} {amountTo:N2}", 
                companyId, userId);

            return result2;
        }
        catch (Exception ex)
        {
            await dbTransaction.RollbackAsync();
            
            await _systemLog.LogErrorAsync("ExchangeRateService", 
                $"Exchange transaction failed: {ex.Message}", ex.StackTrace, companyId, userId);
            
            return new ApiResponse<TransactionResponseDto>(false, 
                $"Exchange failed: {ex.Message}. All changes rolled back.", null);
        }
    }

    private static ExchangeRateResponseDto MapToResponse(ExchangeRate r) => new(
        r.Id, r.BuyRate, r.SellRate, r.EffectiveFrom, r.EffectiveTo, r.IsActive);
}