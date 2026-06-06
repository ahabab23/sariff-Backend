      

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SARIFF.Core.DTOs;
using SARIFF.Core.Entities;
using SARIFF.Core.Enums;
using SARIFF.Core.Interfaces;
using SARIFF.Infrastructure.Data;

namespace SARIFF.Infrastructure.Services;


public class ClientService : IClientService
{
    private readonly AppDbContext _context;
    private readonly StatementHelper _statementHelper;
    private readonly INotificationService _notificationService;
    private readonly ITransactionService _transactionService;
    private readonly ISmsService _smsService;
    private readonly IConfiguration _config;

    public ClientService(AppDbContext context, INotificationService notificationService, ITransactionService transactionService, ISmsService smsService, IConfiguration config)
    {
        _context = context;
        _statementHelper = new StatementHelper(context);
        _notificationService = notificationService;
        _transactionService = transactionService;
        _smsService = smsService;
        _config = config;
    }

    public async Task<ApiResponse<ClientResponseDto>> CreateAsync(Guid companyId, CreateClientDto dto)
    {
        // Input validation
        var validationError = ValidationHelper.FirstError(
            ValidationHelper.ValidateName(dto.FullName, "Full name"),
            ValidationHelper.ValidatePhone(dto.WhatsAppNumber, "WhatsApp number"),
            ValidationHelper.ValidateEmail(dto.Email),
            ValidationHelper.ValidateText(dto.IdPassport, "ID/Passport", 50),
            ValidationHelper.ValidateAmount(dto.OpeningBalanceKES, "Opening balance KES", allowZero: true),
            ValidationHelper.ValidateAmount(dto.OpeningBalanceUSD, "Opening balance USD", allowZero: true)
        );
        if (validationError != null)
            return new ApiResponse<ClientResponseDto>(false, validationError, null);

        if (dto.ClientType == ClientType.Permanent)
        {
            var pwdCheck = ValidationHelper.ValidatePassword(dto.Password);
            if (!pwdCheck.IsValid)
                return new ApiResponse<ClientResponseDto>(false, pwdCheck.Error!, null);
        }

        if (await _context.Users.AnyAsync(u => u.CompanyId == companyId && u.WhatsAppNumber == dto.WhatsAppNumber && !u.IsDeleted))
            return new ApiResponse<ClientResponseDto>(false, "Client with this WhatsApp number already exists", null);

        // Retry loop handles race condition: if two requests generate the same code,
        // the unique constraint on {CompanyId, Code} rejects the second one, and we retry.
        const int maxRetries = 3;
        
        // Get company prefix for client codes (e.g., "FB" → "FB-CL-2026-0001")
        var company = await _context.Companies.FindAsync(companyId);
        var companyPrefix = company?.CodePrefix;
        
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            var code = await CodeGenerator.GenerateClientCodeAsync(_context, companyId, companyPrefix);

            var client = new User
            {
                CompanyId = companyId,
                Code = code,
                FullName = dto.FullName,
                WhatsAppNumber = dto.WhatsAppNumber,
                Email = dto.Email,
                IdPassport = dto.IdPassport,
                Role = UserRole.Client,
                ClientType = dto.ClientType,
                PasswordHash = dto.ClientType == ClientType.Permanent ? BCrypt.Net.BCrypt.HashPassword(dto.Password) : null,
                BalanceKES = dto.OpeningBalanceKES,
                BalanceUSD = dto.OpeningBalanceUSD,
                OpeningBalanceKES = dto.OpeningBalanceKES,
                OpeningBalanceUSD = dto.OpeningBalanceUSD,
                IsActive = true
            };

            try
            {
                _context.Users.Add(client);
                await _context.SaveChangesAsync();

                // Send credentials via WhatsApp for permanent clients
                if (client.ClientType == ClientType.Permanent && !string.IsNullOrEmpty(dto.Password))
                {
                    var companyName = company?.Name ?? "SARIFF";
                    var websiteUrl = _config["Waha:WebsiteUrl"] ?? "https://app.sariff.com";
                    await _notificationService.SendClientCredentialsAsync(
                        companyId, client.Id, client.WhatsAppNumber, client.FullName,
                        code, dto.Password, companyName, websiteUrl);
                    
                    // Auto-send credentials via SMS
                    try
                    {
                        var smsMsg = $"Welcome to {companyName}! Your account: Code: {code}, Password: {dto.Password}. Download the SARIFF app to manage your account. Change your password after first login.";
                        await _smsService.SendSmsAsync(client.WhatsAppNumber, smsMsg);
                    }
                    catch { /* SMS failure should not block client creation */ }
                }

                return new ApiResponse<ClientResponseDto>(true, "Client created successfully", await MapToResponseAsync(companyId, client));
            }
            catch (DbUpdateException) when (attempt < maxRetries - 1)
            {
                // Duplicate code — detach the failed entity and retry with a new code
                _context.Entry(client).State = EntityState.Detached;
                await Task.Delay(50 * (attempt + 1)); // Small backoff
            }
        }

        return new ApiResponse<ClientResponseDto>(false, "Failed to generate unique client code. Please try again.", null);
    }

    public async Task<ApiResponse<ClientResponseDto>> GetByIdAsync(Guid companyId, Guid id)
    {
        var client = await _context.Users.FirstOrDefaultAsync(u => 
            u.Id == id && u.CompanyId == companyId && u.Role == UserRole.Client && !u.IsDeleted);
        
        if (client == null)
            return new ApiResponse<ClientResponseDto>(false, "Client not found", null);

        return new ApiResponse<ClientResponseDto>(true, "Success", await MapToResponseAsync(companyId, client));
    }

    public async Task<ApiResponse<PagedResult<ClientResponseDto>>> GetAllAsync(Guid companyId, int page, int pageSize, string? search = null, string? filter = null)
    {
        var query = _context.Users.Where(u => u.CompanyId == companyId && u.Role == UserRole.Client && !u.IsDeleted);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(u => 
                u.FullName.ToLower().Contains(searchLower) || 
                u.WhatsAppNumber.Contains(search) || 
                u.Code.ToLower().Contains(searchLower));
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            query = filter.ToLower() switch
            {
                "debit" => query.Where(u => u.BalanceKES > 0 || u.BalanceUSD > 0),
                "credit" => query.Where(u => u.BalanceKES < 0 || u.BalanceUSD < 0),
                "permanent" => query.Where(u => u.ClientType == ClientType.Permanent),
                "temporary" => query.Where(u => u.ClientType == ClientType.Temporary),
                "inactive" => query.Where(u => !u.IsActive),
                _ => query
            };
        }

        var totalCount = await query.CountAsync();
        var clients = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        // PERF FIX: Batch load ALL transactions for this page of clients in ONE query
        // instead of N+1 (one query per client)
        var clientIds = clients.Select(c => c.Id).ToList();
        var allTransactions = await _context.Transactions
            .Where(t => t.CompanyId == companyId && !t.IsDeleted &&
                       ((t.SourceAccountType == AccountType.Client && clientIds.Contains(t.SourceAccountId)) ||
                        (t.DestAccountType == AccountType.Client && clientIds.Contains(t.DestAccountId))))
            .ToListAsync();

        var items = clients.Select(client =>
        {
            var clientTxns = allTransactions.Where(t =>
                (t.SourceAccountType == AccountType.Client && t.SourceAccountId == client.Id) ||
                (t.DestAccountType == AccountType.Client && t.DestAccountId == client.Id)).ToList();

            var (debitKES, creditKES, debitUSD, creditUSD) = _statementHelper.CalculateTransactionTotalsByCurrency(
                clientTxns, AccountType.Client, client.Id);

            return new ClientResponseDto(
                client.Id, client.Code, client.FullName, client.WhatsAppNumber,
                client.Email, client.IdPassport, client.ClientType ?? ClientType.Temporary,
                client.BalanceKES, client.BalanceUSD, client.OpeningBalanceKES, client.OpeningBalanceUSD,
                debitKES, creditKES, creditKES - debitKES,
                debitUSD, creditUSD, creditUSD - debitUSD,
                client.IsActive, client.CreatedAt);
        }).ToList();

        return new ApiResponse<PagedResult<ClientResponseDto>>(true, "Success",
            new PagedResult<ClientResponseDto>(items, totalCount, page, pageSize));
    }

    public async Task<ApiResponse<ClientResponseDto>> UpdateAsync(Guid companyId, Guid id, UpdateClientDto dto)
    {
        var client = await _context.Users.FirstOrDefaultAsync(u => 
            u.Id == id && u.CompanyId == companyId && u.Role == UserRole.Client && !u.IsDeleted);
        
        if (client == null)
            return new ApiResponse<ClientResponseDto>(false, "Client not found", null);

        // Validate provided fields
        if (dto.FullName != null)
        {
            var nameCheck = ValidationHelper.ValidateName(dto.FullName, "Full name");
            if (!nameCheck.IsValid)
                return new ApiResponse<ClientResponseDto>(false, nameCheck.Error!, null);
            client.FullName = dto.FullName;
        }
        if (dto.Email != null)
        {
            var emailCheck = ValidationHelper.ValidateEmail(dto.Email);
            if (!emailCheck.IsValid)
                return new ApiResponse<ClientResponseDto>(false, emailCheck.Error!, null);
            client.Email = dto.Email;
        }
        if (dto.IdPassport != null) client.IdPassport = dto.IdPassport;
        if (dto.IsActive.HasValue) client.IsActive = dto.IsActive.Value;
        client.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return new ApiResponse<ClientResponseDto>(true, "Client updated successfully", await MapToResponseAsync(companyId, client));
    }

    public async Task<ApiResponse<bool>> ConvertToPermamentAsync(Guid companyId, Guid id, ConvertClientDto dto)
    {
        var client = await _context.Users.FirstOrDefaultAsync(u => 
            u.Id == id && u.CompanyId == companyId && u.Role == UserRole.Client && !u.IsDeleted);
        
        if (client == null)
            return new ApiResponse<bool>(false, "Client not found", false);

        if (client.ClientType == ClientType.Permanent)
            return new ApiResponse<bool>(false, "Client is already permanent", false);

        if (string.IsNullOrEmpty(dto.Password) || dto.Password.Length < 6)
            return new ApiResponse<bool>(false, "Password must be at least 6 characters", false);

        client.ClientType = ClientType.Permanent;
        client.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password);
        client.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // FIX: Send credentials via WhatsApp on conversion to permanent
        var company = await _context.Companies.FindAsync(companyId);
        var companyName = company?.Name ?? "SARIFF";
        var websiteUrl = _config["Waha:WebsiteUrl"] ?? "https://app.sariff.com";
        await _notificationService.SendClientCredentialsAsync(
            companyId, client.Id, client.WhatsAppNumber, client.FullName,
            client.Code, dto.Password, companyName, websiteUrl);
        
        // Auto-send credentials via SMS
        try
        {
            var smsMsg = $"Welcome to {companyName}! Your account: Code: {client.Code}, Password: {dto.Password}. Download the SARIFF app to manage your account.";
            await _smsService.SendSmsAsync(client.WhatsAppNumber, smsMsg);
        }
        catch { /* SMS failure should not block conversion */ }

        return new ApiResponse<bool>(true, "Client converted to permanent", true);
    }

    public async Task<ApiResponse<bool>> ResetPasswordAsync(Guid companyId, Guid id, ResetClientPasswordDto dto)
    {
        var client = await _context.Users.FirstOrDefaultAsync(u => 
            u.Id == id && u.CompanyId == companyId && u.Role == UserRole.Client && !u.IsDeleted);
        
        if (client == null)
            return new ApiResponse<bool>(false, "Client not found", false);

        if (client.ClientType != ClientType.Permanent)
            return new ApiResponse<bool>(false, "Cannot reset password for temporary client", false);

        if (string.IsNullOrEmpty(dto.NewPassword) || dto.NewPassword.Length < 6)
            return new ApiResponse<bool>(false, "Password must be at least 6 characters", false);

        client.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
        client.FailedLoginAttempts = 0;
        client.LockedUntil = null;
        client.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Auto-send new password via SMS
        try
        {
            var smsMsg = $"SARIFF: Your password has been reset. Code: {client.Code}, New Password: {dto.NewPassword}. Please change your password after login.";
            await _smsService.SendSmsAsync(client.WhatsAppNumber, smsMsg);
        }
        catch { /* SMS failure should not block reset */ }

        return new ApiResponse<bool>(true, "Password reset successfully", true);
    }

    /// <summary>
    /// FIX: Delete client with proper validation
    /// - Checks for outstanding balance
    /// - Checks for recent transactions
    /// - Checks for exchange history
    /// </summary>
    public async Task<ApiResponse<bool>> DeleteAsync(Guid companyId, Guid id)
    {
        var client = await _context.Users.FirstOrDefaultAsync(u => 
            u.Id == id && u.CompanyId == companyId && u.Role == UserRole.Client && !u.IsDeleted);
        
        if (client == null)
            return new ApiResponse<bool>(false, "Client not found", false);

        // FIX: Check for outstanding balance
        if (client.BalanceKES != 0 || client.BalanceUSD != 0)
        {
            var balanceDetails = new List<string>();
            if (client.BalanceKES != 0)
                balanceDetails.Add($"KES {client.BalanceKES:N2}");
            if (client.BalanceUSD != 0)
                balanceDetails.Add($"USD {client.BalanceUSD:N2}");
            
            return new ApiResponse<bool>(false, 
                $"Cannot delete client with outstanding balance: {string.Join(", ", balanceDetails)}. " +
                "Please settle all balances first, or use Archive instead.", false);
        }

        // FIX: Check for transactions in the last 30 days
        var hasRecentTransactions = await _context.Transactions
            .AnyAsync(t => t.CompanyId == companyId && !t.IsDeleted &&
                ((t.SourceAccountType == AccountType.Client && t.SourceAccountId == id) ||
                 (t.DestAccountType == AccountType.Client && t.DestAccountId == id)) &&
                t.TransactionDate >= DateTime.UtcNow.AddDays(-30));

        if (hasRecentTransactions)
        {
            return new ApiResponse<bool>(false, 
                "Cannot delete client with transactions in the last 30 days. " +
                "Please wait or use Archive instead.", false);
        }

        // FIX: Check for exchange history (needed for audit)
        var hasExchangeHistory = await _context.ExchangeTransactions
            .AnyAsync(e => e.CompanyId == companyId && e.ClientId == id && !e.IsDeleted);

        if (hasExchangeHistory)
        {
            return new ApiResponse<bool>(false, 
                "Cannot delete client with exchange history. " +
                "This data is required for audit and compliance purposes. Use Archive instead.", false);
        }

        // Safe to delete
        client.IsDeleted = true;
        client.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return new ApiResponse<bool>(true, "Client deleted successfully", true);
    }

    /// <summary>
    /// NEW: Archive client instead of delete
    /// Keeps all data but marks client as inactive
    /// </summary>
    public async Task<ApiResponse<bool>> ArchiveAsync(Guid companyId, Guid id)
    {
        var client = await _context.Users.FirstOrDefaultAsync(u => 
            u.Id == id && u.CompanyId == companyId && u.Role == UserRole.Client && !u.IsDeleted);
        
        if (client == null)
            return new ApiResponse<bool>(false, "Client not found", false);

        client.IsActive = false;
        client.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var message = "Client archived successfully.";
        if (client.BalanceKES != 0 || client.BalanceUSD != 0)
        {
            message += $" Note: Client still has outstanding balance (KES {client.BalanceKES:N2}, USD {client.BalanceUSD:N2}).";
        }

        return new ApiResponse<bool>(true, message, true);
    }

    /// <summary>
    /// NEW: Reactivate archived client
    /// </summary>
    public async Task<ApiResponse<bool>> ReactivateAsync(Guid companyId, Guid id)
    {
        var client = await _context.Users.FirstOrDefaultAsync(u => 
            u.Id == id && u.CompanyId == companyId && u.Role == UserRole.Client && !u.IsDeleted);
        
        if (client == null)
            return new ApiResponse<bool>(false, "Client not found", false);

        if (client.IsActive)
            return new ApiResponse<bool>(false, "Client is already active", false);

        client.IsActive = true;
        client.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return new ApiResponse<bool>(true, "Client reactivated successfully", true);
    }

    public async Task<ApiResponse<ClientStatsDto>> GetStatsAsync(Guid companyId)
    {
        // PERF: Use DB-level aggregation for client stats
        var clientQuery = _context.Users.Where(u => u.CompanyId == companyId && u.Role == UserRole.Client && !u.IsDeleted);
        
        var totalClients = await clientQuery.CountAsync();
        var permanentClients = await clientQuery.CountAsync(c => c.ClientType == ClientType.Permanent);
        var temporaryClients = await clientQuery.CountAsync(c => c.ClientType == ClientType.Temporary);
        var clientsWithDebit = await clientQuery.CountAsync(c => c.BalanceKES > 0 || c.BalanceUSD > 0);
        var clientsWithCredit = await clientQuery.CountAsync(c => c.BalanceKES < 0 || c.BalanceUSD < 0);
        var totalBalanceKES = await clientQuery.SumAsync(c => c.BalanceKES);
        var totalBalanceUSD = await clientQuery.SumAsync(c => c.BalanceUSD);

        // PERF: Aggregate debit/credit from source-side transactions (DB-level)
        var sourceAgg = await _context.Transactions
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.SourceAccountType == AccountType.Client)
            .GroupBy(t => new { t.Currency, t.TransactionType })
            .Select(g => new { g.Key.Currency, g.Key.TransactionType, Total = g.Sum(t => t.Amount) })
            .ToListAsync();

        // Dest-side: use CounterAmount/CounterCurrency where available
        var destAgg = await _context.Transactions
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.DestAccountType == AccountType.Client)
            .GroupBy(t => new { Currency = t.CounterCurrency ?? t.Currency, t.TransactionType })
            .Select(g => new { g.Key.Currency, g.Key.TransactionType, Total = g.Sum(t => t.CounterAmount ?? t.Amount) })
            .ToListAsync();

        decimal totalDebitKES = 0, totalCreditKES = 0, totalDebitUSD = 0, totalCreditUSD = 0;

        // Source: Debit type = debit for client
        foreach (var s in sourceAgg)
        {
            var isDebit = s.TransactionType == TransactionType.Debit;
            if (s.Currency == Currency.KES) { if (isDebit) totalDebitKES += s.Total; else totalCreditKES += s.Total; }
            else { if (isDebit) totalDebitUSD += s.Total; else totalCreditUSD += s.Total; }
        }
        // Dest: Credit type = debit for client (reverse)
        foreach (var d in destAgg)
        {
            var isDebit = d.TransactionType == TransactionType.Credit;
            if (d.Currency == Currency.KES) { if (isDebit) totalDebitKES += d.Total; else totalCreditKES += d.Total; }
            else { if (isDebit) totalDebitUSD += d.Total; else totalCreditUSD += d.Total; }
        }

        var stats = new ClientStatsDto(
            TotalClients: totalClients,
            PermanentClients: permanentClients,
            TemporaryClients: temporaryClients,
            ClientsWithDebit: clientsWithDebit,
            ClientsWithCredit: clientsWithCredit,
            TotalBalanceKES: totalBalanceKES,
            TotalBalanceUSD: totalBalanceUSD,
            TotalDebitKES: totalDebitKES,
            TotalCreditKES: totalCreditKES,
            TotalDebitUSD: totalDebitUSD,
            TotalCreditUSD: totalCreditUSD
        );

        return new ApiResponse<ClientStatsDto>(true, "Success", stats);
    }

    public async Task<ApiResponse<StatementDto>> GetStatementAsync(Guid companyId, Guid id, StatementFilterDto filter)
    {
        var client = await _context.Users.FirstOrDefaultAsync(u => 
            u.Id == id && u.CompanyId == companyId && u.Role == UserRole.Client && !u.IsDeleted);
        
        if (client == null)
            return new ApiResponse<StatementDto>(false, "Client not found", null);

        var query = _context.Transactions
            .Where(t => t.CompanyId == companyId && !t.IsDeleted &&
                       ((t.SourceAccountType == AccountType.Client && t.SourceAccountId == id) ||
                        (t.DestAccountType == AccountType.Client && t.DestAccountId == id)));

        if (filter.StartDate.HasValue)
            query = query.Where(t => t.TransactionDate >= filter.StartDate.Value);
        if (filter.EndDate.HasValue)
            query = query.Where(t => t.TransactionDate <= filter.EndDate.Value);
        if (filter.Currency.HasValue)
            query = query.Where(t => t.Currency == filter.Currency.Value);
        if (filter.TransactionType.HasValue)
            query = query.Where(t => t.TransactionType == filter.TransactionType.Value);

        var transactions = await query
            .OrderBy(t => t.TransactionDate)        // FIX #1: chronological order for running balance
            .ThenBy(t => t.CreatedAt)
            .ToListAsync();

        var statementCurrency = filter.Currency ?? Currency.KES;
        var openingBalanceKES = client.OpeningBalanceKES;
        var openingBalanceUSD = client.OpeningBalanceUSD;
        var closingBalance = statementCurrency == Currency.KES ? client.BalanceKES : client.BalanceUSD;

        // Client accounts have dual currencies — pass both opening balances
        var lines = await _statementHelper.BuildStatementLinesWithRunningBalanceAsync(
            transactions, AccountType.Client, id, openingBalanceKES, Currency.KES,
            openingBalanceUSD, Currency.USD);

        // Reverse for display (newest first)
        lines.Reverse();

        decimal totalDebit = lines.Sum(l => l.Debit ?? 0);
        decimal totalCredit = lines.Sum(l => l.Credit ?? 0);

        var openingBalance = statementCurrency == Currency.KES ? openingBalanceKES : openingBalanceUSD;
        var netMovement = closingBalance - openingBalance;

        var statement = new StatementDto(
            AccountName: client.FullName,
            AccountCode: client.Code,
            AccountType: AccountType.Client,
            Currency: statementCurrency,
            PeriodStart: filter.StartDate,
            PeriodEnd: filter.EndDate,
            OpeningBalance: openingBalance,
            ClosingBalance: closingBalance,
            TotalDebit: totalDebit,
            TotalCredit: totalCredit,
            NetMovement: netMovement,
            Transactions: lines
        );

        return new ApiResponse<StatementDto>(true, "Success", statement);
    }

    /// <summary>
    /// Reverse a transaction from the client statement context.
    /// Delegates to TransactionService.DeleteAsync with clientContext set,
    /// which skips the "client only from client statement" guard but validates
    /// the transaction belongs to this client.
    /// </summary>
    public async Task<ApiResponse<bool>> ReverseTransactionAsync(Guid companyId, Guid clientId, Guid transactionId, Guid userId, string? reason)
    {
        // Validate client exists
        var client = await _context.Users.FirstOrDefaultAsync(u =>
            u.Id == clientId && u.CompanyId == companyId && u.Role == Core.Enums.UserRole.Client && !u.IsDeleted);

        if (client == null)
            return new ApiResponse<bool>(false, "Client not found", false);

        // Delegate to TransactionService with clientContext — this tells DeleteAsync
        // "I'm calling from the client statement, allow client transactions but validate ownership"
        return await _transactionService.DeleteAsync(
            companyId, transactionId, userId,
            new DeleteTransactionDto(reason),
            clientContext: clientId);
    }

    /// <summary>
    /// PERF: Lightweight client list for dropdowns — no balance/transaction calculations.
    /// Returns only id, code, name, phone, isActive. Single DB query, no N+1.
    /// </summary>
    public async Task<ApiResponse<List<ClientLookupDto>>> GetLookupAsync(Guid companyId, string? search = null)
    {
        var query = _context.Users
            .Where(u => u.CompanyId == companyId && u.Role == UserRole.Client && !u.IsDeleted && u.IsActive);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLower();
            query = query.Where(u => 
                u.FullName.ToLower().Contains(s) || 
                u.WhatsAppNumber.Contains(search) || 
                u.Code.ToLower().Contains(s));
        }

        var clients = await query
            .OrderBy(u => u.FullName)
            .Take(200) // Cap at 200 for dropdown performance
            .Select(u => new ClientLookupDto(u.Id, u.Code, u.FullName, u.WhatsAppNumber, u.IdPassport, u.IsActive))
            .ToListAsync();

        return new ApiResponse<List<ClientLookupDto>>(true, "Success", clients);
    }

    private async Task<ClientResponseDto> MapToResponseAsync(Guid companyId, User client)
    {
        var transactions = await _context.Transactions
            .Where(t => t.CompanyId == companyId && !t.IsDeleted &&
                       ((t.SourceAccountType == AccountType.Client && t.SourceAccountId == client.Id) ||
                        (t.DestAccountType == AccountType.Client && t.DestAccountId == client.Id)))
            .ToListAsync();

        var (debitKES, creditKES, debitUSD, creditUSD) = _statementHelper.CalculateTransactionTotalsByCurrency(
            transactions, AccountType.Client, client.Id);

        return new ClientResponseDto(
            client.Id,
            client.Code,
            client.FullName,
            client.WhatsAppNumber,
            client.Email,
            client.IdPassport,
            client.ClientType ?? ClientType.Temporary,
            client.BalanceKES,
            client.BalanceUSD,
            client.OpeningBalanceKES,
            client.OpeningBalanceUSD,
            debitKES,
            creditKES,
            creditKES - debitKES,
            debitUSD,
            creditUSD,
            creditUSD - debitUSD,
            client.IsActive,
            client.CreatedAt
        );
    }
}