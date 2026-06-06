using Microsoft.EntityFrameworkCore;
using SARIFF.Core.DTOs;
using SARIFF.Core.Entities;
using SARIFF.Core.Enums;
using SARIFF.Core.Interfaces;
using SARIFF.Infrastructure.Data;

namespace SARIFF.Infrastructure.Services;

/// <summary>
/// FIXED Exchange Service
/// 
/// Changes:
/// 1. Added database transactions to FundFloatAsync, WithdrawFloatAsync, CreateExchangeAsync
/// 2. Fixed BalanceBefore calculation in float movements
/// 3. Added Transaction records for client balance changes in FromAccount exchanges
/// 4. Fixed CreateFloatTransactionAsync to include balance fields
/// </summary>
public class ExchangeService : IExchangeService
{
    private readonly AppDbContext _context;
    private readonly ISystemLogService _systemLog;

    public ExchangeService(AppDbContext context, ISystemLogService systemLog)
    {
        _context = context;
        _systemLog = systemLog;
    }

    // ==================== RATE MANAGEMENT ====================

    public async Task<ApiResponse<ExchangeRateResponseDto>> SetRateAsync(Guid companyId, Guid userId, SetExchangeRateDto dto)
    {
        // Validate
        var buyCheck = ValidationHelper.ValidateRate(dto.BuyRate, "Buy rate");
        if (!buyCheck.IsValid)
            return new ApiResponse<ExchangeRateResponseDto>(false, buyCheck.Error!, null);
        var sellCheck = ValidationHelper.ValidateRate(dto.SellRate, "Sell rate");
        if (!sellCheck.IsValid)
            return new ApiResponse<ExchangeRateResponseDto>(false, sellCheck.Error!, null);
        if (dto.BuyRate >= dto.SellRate)
            return new ApiResponse<ExchangeRateResponseDto>(false, "Buy rate must be less than sell rate", null);

        // Deactivate current rate
        var currentRate = await _context.ExchangeRates.FirstOrDefaultAsync(r => r.CompanyId == companyId && r.IsActive);
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

        return new ApiResponse<ExchangeRateResponseDto>(true, "Exchange rate set", MapRateToResponse(newRate));
    }

    public async Task<ApiResponse<ExchangeRateResponseDto>> GetCurrentRateAsync(Guid companyId)
    {
        var rate = await _context.ExchangeRates.FirstOrDefaultAsync(r => r.CompanyId == companyId && r.IsActive);
        if (rate == null) return new ApiResponse<ExchangeRateResponseDto>(false, "No active exchange rate", null);
        return new ApiResponse<ExchangeRateResponseDto>(true, "Success", MapRateToResponse(rate));
    }

    public async Task<ApiResponse<List<ExchangeRateResponseDto>>> GetRateHistoryAsync(Guid companyId, int days = 30)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var rates = await _context.ExchangeRates
            .Where(r => r.CompanyId == companyId && r.EffectiveFrom >= cutoff)
            .OrderByDescending(r => r.EffectiveFrom)
            .ToListAsync();
        return new ApiResponse<List<ExchangeRateResponseDto>>(true, "Success", rates.Select(MapRateToResponse).ToList());
    }

    // ==================== FLOAT MANAGEMENT ====================

    public async Task<ApiResponse<ExchangeFloatDto>> GetFloatAsync(Guid companyId)
    {
        var float_ = await GetOrCreateFloatAsync(companyId);
        return new ApiResponse<ExchangeFloatDto>(true, "Success", MapToFloatDto(float_));
    }

    /// <summary>
    /// FIXED: Fund float with database transaction
    /// </summary>
    public async Task<ApiResponse<ExchangeFloatDto>> FundFloatAsync(Guid companyId, Guid userId, FundFloatDto dto)
    {
        var amountCheck = ValidationHelper.ValidateAmount(dto.Amount, "Fund amount");
        if (!amountCheck.IsValid)
            return new ApiResponse<ExchangeFloatDto>(false, amountCheck.Error!, null);

        if (dto.PurchaseRate.HasValue)
        {
            var rateCheck = ValidationHelper.ValidateRate(dto.PurchaseRate.Value, "Purchase rate");
            if (!rateCheck.IsValid)
                return new ApiResponse<ExchangeFloatDto>(false, rateCheck.Error!, null);
        }

        using var dbTransaction = await _context.Database.BeginTransactionAsync();

        try
        {
            var float_ = await GetOrCreateFloatAsync(companyId);

            // Get source account balance
            var sourceBalance = await GetAccountBalanceAsync(dto.SourceType, dto.SourceAccountId);
            var deductAmount = dto.Currency == Currency.USD && dto.PurchaseRate.HasValue
                ? dto.Amount * dto.PurchaseRate.Value
                : dto.Amount;

            if (sourceBalance < deductAmount)
            {
                await dbTransaction.RollbackAsync();
                return new ApiResponse<ExchangeFloatDto>(false, 
                    $"Insufficient balance. Available: {sourceBalance:N2}, Required: {deductAmount:N2}", null);
            }

            // FIXED: Capture balance BEFORE update
            decimal balanceBefore = dto.Currency == Currency.KES ? float_.KesBalance : float_.UsdBalance;

            // Deduct from source account
            await UpdateAccountBalanceAsync(dto.SourceType, dto.SourceAccountId, -deductAmount);

            // Update float
            if (dto.Currency == Currency.KES)
            {
                float_.KesBalance += dto.Amount;
            }
            else
            {
                float_.UsdBalance += dto.Amount;
                float_.UsdTotalCost += dto.Amount * (dto.PurchaseRate ?? 0);
                float_.UsdAverageCost = float_.UsdBalance > 0
                    ? float_.UsdTotalCost / float_.UsdBalance
                    : 0;
            }
            float_.UpdatedAt = DateTime.UtcNow;

            // Record movement with CORRECT balance before
            var movement = new FloatMovement
            {
                CompanyId = companyId,
                ExchangeFloatId = float_.Id,
                MovementDate = DateTime.UtcNow,
                MovementType = FloatMovementType.Fund,
                Currency = dto.Currency,
                Amount = dto.Amount,
                BalanceBefore = balanceBefore,  // FIXED: Captured before update
                BalanceAfter = dto.Currency == Currency.KES ? float_.KesBalance : float_.UsdBalance,
                RelatedAccountType = dto.SourceType,
                RelatedAccountId = dto.SourceAccountId,
                Notes = dto.Notes ?? $"Float funding from {dto.SourceType}",
                CreatedByUserId = userId
            };
            _context.FloatMovements.Add(movement);

            // Create transaction record for source account
            await CreateFloatTransactionAsync(companyId, userId, dto.SourceType, dto.SourceAccountId,
                TransactionType.Credit, deductAmount, dto.Currency == Currency.USD ? Currency.KES : dto.Currency,
                sourceBalance, sourceBalance - deductAmount,
                $"Transfer to Exchange Float - {dto.Currency} {dto.Amount:N2}", dto.Notes);

            await _context.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            return new ApiResponse<ExchangeFloatDto>(true, "Float funded successfully", MapToFloatDto(float_));
        }
        catch (DbUpdateConcurrencyException)
        {
            await dbTransaction.RollbackAsync();
            return new ApiResponse<ExchangeFloatDto>(false,
                "Balance was modified by another operation. Please retry.", null);
        }
        catch (Exception ex)
        {
            await dbTransaction.RollbackAsync();
            await _systemLog.LogErrorAsync("ExchangeService", $"Failed to fund float: {ex.Message}", 
                ex.StackTrace, companyId, userId);
            return new ApiResponse<ExchangeFloatDto>(false, "Failed to fund float. Please try again.", null);
        }
    }

    /// <summary>
    /// FIXED: Withdraw float with database transaction
    /// </summary>
    public async Task<ApiResponse<ExchangeFloatDto>> WithdrawFloatAsync(Guid companyId, Guid userId, WithdrawFloatDto dto)
    {
        var amountCheck = ValidationHelper.ValidateAmount(dto.Amount, "Withdrawal amount");
        if (!amountCheck.IsValid)
            return new ApiResponse<ExchangeFloatDto>(false, amountCheck.Error!, null);

        using var dbTransaction = await _context.Database.BeginTransactionAsync();

        try
        {
            var float_ = await GetOrCreateFloatAsync(companyId);

            var availableBalance = dto.Currency == Currency.KES ? float_.KesBalance : float_.UsdBalance;
            if (dto.Amount > availableBalance)
            {
                await dbTransaction.RollbackAsync();
                return new ApiResponse<ExchangeFloatDto>(false,
                    $"Insufficient float. Available: {dto.Currency} {availableBalance:N2}", null);
            }

            // FIXED: Capture balance BEFORE update
            var balanceBefore = availableBalance;

            // Update float
            if (dto.Currency == Currency.KES)
            {
                float_.KesBalance -= dto.Amount;
            }
            else
            {
                var costReduction = float_.UsdBalance > 0
                    ? (dto.Amount / float_.UsdBalance) * float_.UsdTotalCost
                    : 0;
                float_.UsdBalance -= dto.Amount;
                float_.UsdTotalCost -= costReduction;
                float_.UsdAverageCost = float_.UsdBalance > 0 ? float_.UsdTotalCost / float_.UsdBalance : 0;
            }
            float_.UpdatedAt = DateTime.UtcNow;

            // Get destination balance before credit
            var destBalanceBefore = await GetAccountBalanceAsync(dto.DestinationType, dto.DestinationAccountId);

            // Credit destination account
            await UpdateAccountBalanceAsync(dto.DestinationType, dto.DestinationAccountId, dto.Amount);

            // Record movement with CORRECT balance
            var movement = new FloatMovement
            {
                CompanyId = companyId,
                ExchangeFloatId = float_.Id,
                MovementDate = DateTime.UtcNow,
                MovementType = FloatMovementType.Withdraw,
                Currency = dto.Currency,
                Amount = -dto.Amount,
                BalanceBefore = balanceBefore,  // FIXED
                BalanceAfter = dto.Currency == Currency.KES ? float_.KesBalance : float_.UsdBalance,
                RelatedAccountType = dto.DestinationType,
                RelatedAccountId = dto.DestinationAccountId,
                Notes = dto.Notes ?? $"Float withdrawal to {dto.DestinationType}",
                CreatedByUserId = userId
            };
            _context.FloatMovements.Add(movement);

            // Create transaction for destination account
            await CreateFloatTransactionAsync(companyId, userId, dto.DestinationType, dto.DestinationAccountId,
                TransactionType.Debit, dto.Amount, dto.Currency,
                destBalanceBefore, destBalanceBefore + dto.Amount,
                $"Transfer from Exchange Float - {dto.Currency} {dto.Amount:N2}", dto.Notes);

            await _context.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            return new ApiResponse<ExchangeFloatDto>(true, "Float withdrawn successfully", MapToFloatDto(float_));
        }
        catch (DbUpdateConcurrencyException)
        {
            await dbTransaction.RollbackAsync();
            return new ApiResponse<ExchangeFloatDto>(false,
                "Balance was modified by another operation. Please retry.", null);
        }
        catch (Exception ex)
        {
            await dbTransaction.RollbackAsync();
            await _systemLog.LogErrorAsync("ExchangeService", $"Failed to withdraw float: {ex.Message}",
                ex.StackTrace, companyId, userId);
            return new ApiResponse<ExchangeFloatDto>(false, "Failed to withdraw float. Please try again.", null);
        }
    }

    /// <summary>
    /// Settle accumulated profit — transfer profit from float to a bank/cash/mpesa account.
    /// This is the EVENING operation: move earned profit out of the float.
    /// </summary>
    public async Task<ApiResponse<ExchangeFloatDto>> SettleProfitAsync(Guid companyId, Guid userId, SettleProfitDto dto)
    {
        var amountCheck = ValidationHelper.ValidateAmount(dto.Amount, "Settlement amount");
        if (!amountCheck.IsValid)
            return new ApiResponse<ExchangeFloatDto>(false, amountCheck.Error!, null);

        using var dbTransaction = await _context.Database.BeginTransactionAsync();

        try
        {
            var float_ = await GetOrCreateFloatAsync(companyId);

            // Validate profit is available
            var availableProfit = dto.Currency == Currency.KES ? float_.KesProfit : float_.UsdProfit;
            if (dto.Amount > availableProfit)
                return new ApiResponse<ExchangeFloatDto>(false,
                    $"Insufficient profit. Available: {dto.Currency} {availableProfit:N2}", null);

            // Also check float balance (profit is part of float balance)
            var floatBalance = dto.Currency == Currency.KES ? float_.KesBalance : float_.UsdBalance;
            if (dto.Amount > floatBalance)
                return new ApiResponse<ExchangeFloatDto>(false,
                    $"Insufficient float balance to settle. Float: {dto.Currency} {floatBalance:N2}", null);

            // Capture balances before
            var floatBalanceBefore = floatBalance;
            var destBalanceBefore = await GetAccountBalanceAsync(dto.DestinationType, dto.DestinationAccountId);

            // Deduct from float
            if (dto.Currency == Currency.KES)
            {
                float_.KesBalance -= dto.Amount;
                float_.KesProfit -= dto.Amount;
            }
            else
            {
                float_.UsdBalance -= dto.Amount;
                float_.UsdProfit -= dto.Amount;
            }
            float_.UpdatedAt = DateTime.UtcNow;

            // Credit destination account
            await UpdateAccountBalanceAsync(dto.DestinationType, dto.DestinationAccountId, dto.Amount);

            // Record float movement
            _context.FloatMovements.Add(new FloatMovement
            {
                CompanyId = companyId,
                ExchangeFloatId = float_.Id,
                MovementDate = DateTime.UtcNow,
                MovementType = FloatMovementType.ProfitSettlement,
                Currency = dto.Currency,
                Amount = -dto.Amount,
                BalanceBefore = floatBalanceBefore,
                BalanceAfter = dto.Currency == Currency.KES ? float_.KesBalance : float_.UsdBalance,
                RelatedAccountType = dto.DestinationType,
                RelatedAccountId = dto.DestinationAccountId,
                Notes = dto.Notes ?? $"Profit settlement: {dto.Currency} {dto.Amount:N2}",
                CreatedByUserId = userId
            });

            // Create transaction record for destination account
            await CreateFloatTransactionAsync(companyId, userId, dto.DestinationType, dto.DestinationAccountId,
                TransactionType.Debit, dto.Amount, dto.Currency,
                destBalanceBefore, destBalanceBefore + dto.Amount,
                $"Exchange profit settlement - {dto.Currency} {dto.Amount:N2}", dto.Notes);

            await _context.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            return new ApiResponse<ExchangeFloatDto>(true,
                $"Profit settled: {dto.Currency} {dto.Amount:N2} transferred", MapToFloatDto(float_));
        }
        catch (DbUpdateConcurrencyException)
        {
            await dbTransaction.RollbackAsync();
            return new ApiResponse<ExchangeFloatDto>(false,
                "Balance was modified by another operation. Please retry.", null);
        }
        catch (Exception ex)
        {
            await dbTransaction.RollbackAsync();
            await _systemLog.LogErrorAsync("ExchangeService", $"Failed to settle profit: {ex.Message}",
                ex.StackTrace, companyId, userId);
            return new ApiResponse<ExchangeFloatDto>(false, "Failed to settle profit. Please try again.", null);
        }
    }

    /// <summary>
    /// Get float movement history — all fund, withdraw, exchange, and settlement movements.
    /// </summary>
    public async Task<ApiResponse<List<FloatMovementDto>>> GetFloatMovementsAsync(Guid companyId, DateTime? from, DateTime? to)
    {
        var query = _context.FloatMovements
            .Where(m => m.CompanyId == companyId && !m.IsDeleted);

        if (from.HasValue)
            query = query.Where(m => m.MovementDate >= from.Value);
        if (to.HasValue)
            query = query.Where(m => m.MovementDate <= to.Value.AddDays(1));

        var movements = await query
            .OrderByDescending(m => m.MovementDate)
            .ThenByDescending(m => m.CreatedAt)
            .ToListAsync();

        var dtos = movements.Select(m => new FloatMovementDto(
            m.Id,
            m.MovementDate,
            m.MovementType.ToString(),
            m.Currency,
            m.Amount,
            m.BalanceBefore,
            m.BalanceAfter,
            m.RelatedAccountType?.ToString(),
            m.Notes
        )).ToList();

        return new ApiResponse<List<FloatMovementDto>>(true, "Success", dtos);
    }

    /// <summary>
    /// FIXED: Create exchange with database transaction and proper client transaction records
    /// </summary>
    public async Task<ApiResponse<ExchangeResponseDto>> CreateExchangeAsync(Guid companyId, Guid userId, CreateExchangeDto dto)
    {
        // Input validation
        var amountCheck = ValidationHelper.ValidateAmount(dto.Amount, "Exchange amount");
        if (!amountCheck.IsValid)
            return new ApiResponse<ExchangeResponseDto>(false, amountCheck.Error!, null);

        if (!string.IsNullOrEmpty(dto.ClientName))
        {
            var nameCheck = ValidationHelper.ValidateName(dto.ClientName, "Client name");
            if (!nameCheck.IsValid)
                return new ApiResponse<ExchangeResponseDto>(false, nameCheck.Error!, null);
        }

        using var dbTransaction = await _context.Database.BeginTransactionAsync();

        try
        {
            var float_ = await GetOrCreateFloatAsync(companyId);
            var rate = await _context.ExchangeRates.FirstOrDefaultAsync(r => r.CompanyId == companyId && r.IsActive);

            if (rate == null)
                return new ApiResponse<ExchangeResponseDto>(false, "No active exchange rate set", null);

            if (rate.BuyRate <= 0 || rate.SellRate <= 0)
                return new ApiResponse<ExchangeResponseDto>(false, "Exchange rate is invalid (zero or negative)", null);

            // Client validation depends on exchange type
            string clientName;
            string? clientIdNumber = dto.ClientIdNumber;
            User? client = null;  // Needed for FromAccount balance operations

            if (dto.ExchangeType == ExchangeType.FromAccount)
            {
                // FromAccount REQUIRES a registered client with an account
                if (!dto.ClientId.HasValue)
                    return new ApiResponse<ExchangeResponseDto>(false, "ClientId is required for FromAccount exchanges", null);
                    
                client = await _context.Users.FindAsync(dto.ClientId.Value);
                if (client == null || client.CompanyId != companyId)
                    return new ApiResponse<ExchangeResponseDto>(false, "Client not found", null);
                    
                clientName = client.FullName;
                clientIdNumber ??= client.IdPassport;
            }
            else
            {
                // Cash exchange — client is optional (walk-in)
                if (dto.ClientId.HasValue)
                {
                    client = await _context.Users.FindAsync(dto.ClientId.Value);
                    if (client == null || client.CompanyId != companyId)
                        return new ApiResponse<ExchangeResponseDto>(false, "Client not found", null);
                    clientName = client.FullName;
                    clientIdNumber ??= client.IdPassport;
                }
                else
                {
                    clientName = dto.ClientName ?? "Walk-in Client";
                }
            }

            // Calculate exchange
            Currency currencyGiven, currencyReceived;
            decimal exchangeRate, amountReceived, profit, spread;

            if (dto.Direction == ExchangeDirection.UsdToKes)
            {
                currencyGiven = Currency.USD;
                currencyReceived = Currency.KES;
                exchangeRate = rate.BuyRate;  // FIX: Bureau BUYS USD at BuyRate
                amountReceived = dto.Amount * exchangeRate;
                spread = rate.SellRate - rate.BuyRate;
                profit = spread * dto.Amount;

                if (dto.ExchangeType == ExchangeType.Cash && float_.KesBalance < amountReceived)
                    return new ApiResponse<ExchangeResponseDto>(false,
                        $"Insufficient KES float. Available: {float_.KesBalance:N2}", null);
            }
            else
            {
                currencyGiven = Currency.KES;
                currencyReceived = Currency.USD;
                exchangeRate = rate.SellRate;  // FIX: Bureau SELLS USD at SellRate
                amountReceived = dto.Amount / exchangeRate;
                spread = rate.SellRate - rate.BuyRate;
                profit = spread * amountReceived;

                if (dto.ExchangeType == ExchangeType.Cash && float_.UsdBalance < amountReceived)
                    return new ApiResponse<ExchangeResponseDto>(false,
                        $"Insufficient USD float. Available: ${float_.UsdBalance:N2}", null);
            }

            // Large transaction check
            decimal kesEquivalent = dto.Direction == ExchangeDirection.UsdToKes ? amountReceived : dto.Amount;
            bool isLargeTransaction = kesEquivalent >= float_.LargeTransactionThreshold;

            // Generate code
            var code = await CodeGenerator.GenerateExchangeCodeAsync(_context, companyId);

            // Create exchange transaction record
            var exchange = new ExchangeTransaction
            {
                Code = code,
                CompanyId = companyId,
                TransactionDate = DateTime.UtcNow,
                ClientId = dto.ClientId,
                ClientName = clientName,
                ClientIdNumber = clientIdNumber,
                ExchangeType = dto.ExchangeType,
                Direction = dto.Direction,
                AmountGiven = dto.Amount,
                CurrencyGiven = currencyGiven,
                AmountReceived = amountReceived,
                CurrencyReceived = currencyReceived,
                ExchangeRate = exchangeRate,
                Profit = profit,
                ProfitCurrency = Currency.KES,
                Status = "Completed",
                Notes = dto.Notes,
                CreatedByUserId = userId
            };

            // FIXED: Capture float balances BEFORE update
            var kesBalanceBefore = float_.KesBalance;
            var usdBalanceBefore = float_.UsdBalance;

            // Update balances based on exchange type
            if (dto.ExchangeType == ExchangeType.Cash)
            {
                // Cash exchange - update float
                if (dto.Direction == ExchangeDirection.UsdToKes)
                {
                    float_.UsdBalance += dto.Amount;
                    float_.KesBalance -= amountReceived;
                    float_.UsdTotalCost += dto.Amount * exchangeRate;
                    float_.UsdAverageCost = float_.UsdBalance > 0 ? float_.UsdTotalCost / float_.UsdBalance : 0;
                }
                else
                {
                    float_.KesBalance += dto.Amount;
                    var costReduction = float_.UsdBalance > 0
                        ? (amountReceived / float_.UsdBalance) * float_.UsdTotalCost
                        : 0;
                    float_.UsdBalance -= amountReceived;
                    float_.UsdTotalCost -= costReduction;
                    float_.UsdAverageCost = float_.UsdBalance > 0 ? float_.UsdTotalCost / float_.UsdBalance : 0;
                }

                // Record float movements with CORRECT balances
                _context.FloatMovements.Add(new FloatMovement
                {
                    CompanyId = companyId,
                    ExchangeFloatId = float_.Id,
                    MovementDate = DateTime.UtcNow,
                    MovementType = FloatMovementType.ExchangeIn,
                    Currency = currencyGiven,
                    Amount = dto.Amount,
                    BalanceBefore = currencyGiven == Currency.KES ? kesBalanceBefore : usdBalanceBefore,
                    BalanceAfter = currencyGiven == Currency.KES ? float_.KesBalance : float_.UsdBalance,
                    Reference = code,
                    Notes = $"Exchange {code} - Received from {clientName}",
                    CreatedByUserId = userId
                });

                _context.FloatMovements.Add(new FloatMovement
                {
                    CompanyId = companyId,
                    ExchangeFloatId = float_.Id,
                    MovementDate = DateTime.UtcNow,
                    MovementType = FloatMovementType.ExchangeOut,
                    Currency = currencyReceived,
                    Amount = -amountReceived,
                    BalanceBefore = currencyReceived == Currency.KES ? kesBalanceBefore : usdBalanceBefore,
                    BalanceAfter = currencyReceived == Currency.KES ? float_.KesBalance : float_.UsdBalance,
                    Reference = code,
                    Notes = $"Exchange {code} - Given to {clientName}",
                    CreatedByUserId = userId
                });
            }
            else
            {
                // FIXED: From Account - Create proper transaction records for audit trail
                var clientId = dto.ClientId!.Value;  // Guaranteed non-null (validated above)
                var clientBalanceKesBefore = client!.BalanceKES;
                var clientBalanceUsdBefore = client.BalanceUSD;

                if (dto.Direction == ExchangeDirection.UsdToKes)
                {
                    // Check client has enough USD
                    if (client.BalanceUSD < dto.Amount)
                        return new ApiResponse<ExchangeResponseDto>(false,
                            $"Insufficient client USD balance. Available: ${client.BalanceUSD:N2}", null);

                    client.BalanceUSD -= dto.Amount;
                    client.BalanceKES += amountReceived;

                    // Create debit transaction (USD out of client)
                    var txn1Code = await CodeGenerator.GenerateTransactionCodeAsync(_context, companyId);
                    var txn1 = new Transaction
                    {
                        Code = txn1Code,
                        CompanyId = companyId,
                        Reference = $"EXC-{code}",
                        TransactionDate = DateTime.UtcNow,
                        TransactionType = TransactionType.Debit,
                        Amount = dto.Amount,
                        Currency = Currency.USD,
                        Description = $"Exchange: Give USD {dto.Amount:N2} @ {exchangeRate:N4}",
                        Notes = $"Exchange Code: {code}",
                        SourceAccountType = AccountType.Client,
                        SourceAccountId = clientId,
                        SourceBalanceBefore = clientBalanceUsdBefore,
                        SourceBalanceAfter = client.BalanceUSD,
                        DestAccountType = AccountType.Client,
                        DestAccountId = clientId,
                        DestBalanceBefore = clientBalanceUsdBefore,
                        DestBalanceAfter = client.BalanceUSD,
                        CreatedByUserId = userId
                    };
                    _context.Transactions.Add(txn1);
                    exchange.ClientSourceTransactionId = txn1.Id;

                    // Create credit transaction (KES into client)
                    var txn2Code = await CodeGenerator.GenerateTransactionCodeAsync(_context, companyId);
                    var txn2 = new Transaction
                    {
                        Code = txn2Code,
                        CompanyId = companyId,
                        Reference = $"EXC-{code}",
                        TransactionDate = DateTime.UtcNow,
                        TransactionType = TransactionType.Credit,
                        Amount = amountReceived,
                        Currency = Currency.KES,
                        Description = $"Exchange: Receive KES {amountReceived:N2} @ {exchangeRate:N4}",
                        Notes = $"Exchange Code: {code}",
                        SourceAccountType = AccountType.Client,
                        SourceAccountId = clientId,
                        SourceBalanceBefore = clientBalanceKesBefore,
                        SourceBalanceAfter = client.BalanceKES,
                        DestAccountType = AccountType.Client,
                        DestAccountId = clientId,
                        DestBalanceBefore = clientBalanceKesBefore,
                        DestBalanceAfter = client.BalanceKES,
                        CreatedByUserId = userId
                    };
                    _context.Transactions.Add(txn2);
                    exchange.ClientDestTransactionId = txn2.Id;
                }
                else
                {
                    // Check client has enough KES
                    if (client.BalanceKES < dto.Amount)
                        return new ApiResponse<ExchangeResponseDto>(false,
                            $"Insufficient client KES balance. Available: KES {client.BalanceKES:N2}", null);

                    client.BalanceKES -= dto.Amount;
                    client.BalanceUSD += amountReceived;

                    // Create transactions for KES -> USD exchange
                    var txn1Code = await CodeGenerator.GenerateTransactionCodeAsync(_context, companyId);
                    var txn1 = new Transaction
                    {
                        Code = txn1Code,
                        CompanyId = companyId,
                        Reference = $"EXC-{code}",
                        TransactionDate = DateTime.UtcNow,
                        TransactionType = TransactionType.Debit,
                        Amount = dto.Amount,
                        Currency = Currency.KES,
                        Description = $"Exchange: Give KES {dto.Amount:N2} @ {exchangeRate:N4}",
                        Notes = $"Exchange Code: {code}",
                        SourceAccountType = AccountType.Client,
                        SourceAccountId = clientId,
                        SourceBalanceBefore = clientBalanceKesBefore,
                        SourceBalanceAfter = client.BalanceKES,
                        DestAccountType = AccountType.Client,
                        DestAccountId = clientId,
                        DestBalanceBefore = clientBalanceKesBefore,
                        DestBalanceAfter = client.BalanceKES,
                        CreatedByUserId = userId
                    };
                    _context.Transactions.Add(txn1);
                    exchange.ClientSourceTransactionId = txn1.Id;

                    var txn2Code = await CodeGenerator.GenerateTransactionCodeAsync(_context, companyId);
                    var txn2 = new Transaction
                    {
                        Code = txn2Code,
                        CompanyId = companyId,
                        Reference = $"EXC-{code}",
                        TransactionDate = DateTime.UtcNow,
                        TransactionType = TransactionType.Credit,
                        Amount = amountReceived,
                        Currency = Currency.USD,
                        Description = $"Exchange: Receive USD {amountReceived:N2} @ {exchangeRate:N4}",
                        Notes = $"Exchange Code: {code}",
                        SourceAccountType = AccountType.Client,
                        SourceAccountId = clientId,
                        SourceBalanceBefore = clientBalanceUsdBefore,
                        SourceBalanceAfter = client.BalanceUSD,
                        DestAccountType = AccountType.Client,
                        DestAccountId = clientId,
                        DestBalanceBefore = clientBalanceUsdBefore,
                        DestBalanceAfter = client.BalanceUSD,
                        CreatedByUserId = userId
                    };
                    _context.Transactions.Add(txn2);
                    exchange.ClientDestTransactionId = txn2.Id;
                }

                client.UpdatedAt = DateTime.UtcNow;

                // ============================================================
                // UPDATE FLOAT for FromAccount exchanges
                // Bureau's currency position changes the SAME as cash exchanges:
                // UsdToKes: Bureau receives USD (float UP), gives KES (float DOWN)
                // KesToUsd: Bureau receives KES (float UP), gives USD (float DOWN)
                // ============================================================
                if (dto.Direction == ExchangeDirection.UsdToKes)
                {
                    float_.UsdBalance += dto.Amount;
                    float_.KesBalance -= amountReceived;
                    float_.UsdTotalCost += dto.Amount * exchangeRate;
                    float_.UsdAverageCost = float_.UsdBalance > 0 ? float_.UsdTotalCost / float_.UsdBalance : 0;
                }
                else
                {
                    float_.KesBalance += dto.Amount;
                    var costReduction = float_.UsdBalance > 0
                        ? (amountReceived / float_.UsdBalance) * float_.UsdTotalCost
                        : 0;
                    float_.UsdBalance -= amountReceived;
                    float_.UsdTotalCost -= costReduction;
                    float_.UsdAverageCost = float_.UsdBalance > 0 ? float_.UsdTotalCost / float_.UsdBalance : 0;
                }
            }

            // Add profit to float
            float_.KesProfit += profit;
            float_.UpdatedAt = DateTime.UtcNow;

            _context.ExchangeTransactions.Add(exchange);
            await _context.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            return new ApiResponse<ExchangeResponseDto>(true,
                $"Exchange completed. Profit: KES {profit:N2}",
                MapToExchangeDto(exchange, clientName, client?.ClientType?.ToString() ?? "WalkIn", isLargeTransaction));
        }
        catch (DbUpdateConcurrencyException)
        {
            await dbTransaction.RollbackAsync();
            return new ApiResponse<ExchangeResponseDto>(false,
                "Balance was modified by another operation. Please retry.", null);
        }
        catch (Exception ex)
        {
            await dbTransaction.RollbackAsync();
            await _systemLog.LogErrorAsync("ExchangeService", $"Exchange failed: {ex.Message}",
                ex.StackTrace, companyId, userId);
            return new ApiResponse<ExchangeResponseDto>(false, "Exchange failed. Please try again.", null);
        }
    }

    /// <summary>
    /// Get exchange transactions with pagination, search, and filters.
    /// </summary>
    public async Task<ApiResponse<PagedResult<ExchangeResponseDto>>> GetExchangesAsync(Guid companyId, int page, int pageSize, string? search, ExchangeType? type, DateTime? from,
        DateTime? to)
    {
        var query = _context.ExchangeTransactions
            .Where(e => e.CompanyId == companyId && !e.IsDeleted && e.Status != "Voided");

        if (type.HasValue)
            query = query.Where(e => e.ExchangeType == type.Value);
        if (from.HasValue)
            query = query.Where(e => e.TransactionDate >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.TransactionDate <= to.Value.AddDays(1));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLower();
            // Get matching client IDs first to avoid complex join in EF
            var clientIds = await _context.Users
                .Where(u => u.CompanyId == companyId && u.FullName.ToLower().Contains(s))
                .Select(u => u.Id).ToListAsync();

            query = query.Where(e => e.Code.ToLower().Contains(s) ||
                                     (e.ClientIdNumber != null && e.ClientIdNumber.ToLower().Contains(s)) ||
                                     e.ClientId.HasValue && clientIds.Contains(e.ClientId.Value));
        }

        var totalCount = await query.CountAsync();

        var exchanges = await query
            .OrderByDescending(e => e.TransactionDate)
            .ThenByDescending(e => e.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        // Get client names for registered clients
        var clientIdSet = exchanges.Where(e => e.ClientId.HasValue).Select(e => e.ClientId!.Value).Distinct().ToList();
        var clients = await _context.Users
            .Where(u => clientIdSet.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new { u.FullName, ClientType = u.ClientType.ToString() });

        var float_ = await GetOrCreateFloatAsync(companyId);

        var items = exchanges.Select(e =>
        {
            string name;
            string clientType;
            if (e.ClientId.HasValue && clients.TryGetValue(e.ClientId.Value, out var c))
            {
                name = c.FullName;
                clientType = c.ClientType ?? "Unknown";
            }
            else
            {
                name = e.ClientName ?? "Walk-in Client";
                clientType = "WalkIn";
            }
            decimal kesEquivalent = e.CurrencyGiven == Currency.KES ? e.AmountGiven : e.AmountReceived;
            return MapToExchangeDto(e, name, clientType,
                kesEquivalent >= float_.LargeTransactionThreshold);
        }).ToList();

        return new ApiResponse<PagedResult<ExchangeResponseDto>>(true, "Success",
            new PagedResult<ExchangeResponseDto>(items, totalCount, page, pageSize));
    }

    /// <summary>
    /// Get a single exchange transaction by ID.
    /// </summary>
    public async Task<ApiResponse<ExchangeResponseDto>> GetExchangeByIdAsync(Guid companyId, Guid exchangeId)
    {
        var exchange = await _context.ExchangeTransactions
            .FirstOrDefaultAsync(e => e.Id == exchangeId && e.CompanyId == companyId && !e.IsDeleted);

        if (exchange == null)
            return new ApiResponse<ExchangeResponseDto>(false, "Exchange transaction not found", null);

        User? client = exchange.ClientId.HasValue ? await _context.Users.FindAsync(exchange.ClientId.Value) : null;
        var float_ = await GetOrCreateFloatAsync(companyId);

        decimal kesEquivalent = exchange.CurrencyGiven == Currency.KES ? exchange.AmountGiven : exchange.AmountReceived;

        return new ApiResponse<ExchangeResponseDto>(true, "Success",
            MapToExchangeDto(exchange,
                client?.FullName ?? exchange.ClientName ?? "Walk-in Client",
                client?.ClientType?.ToString() ?? "WalkIn",
                kesEquivalent >= float_.LargeTransactionThreshold));
    }

    /// <summary>
    /// Void an exchange transaction — reverses float balances and client account adjustments.
    /// Only Completed exchanges can be voided. Creates reversal audit trail.
    /// </summary>
    public async Task<ApiResponse<bool>> VoidExchangeAsync(Guid companyId, Guid userId, Guid exchangeId, string reason)
    {
        using var dbTransaction = await _context.Database.BeginTransactionAsync();

        try
        {
            var exchange = await _context.ExchangeTransactions
                .FirstOrDefaultAsync(e => e.Id == exchangeId && e.CompanyId == companyId && !e.IsDeleted);

            if (exchange == null)
                return new ApiResponse<bool>(false, "Exchange transaction not found", false);

            if (exchange.Status != "Completed")
                return new ApiResponse<bool>(false, $"Cannot void exchange with status '{exchange.Status}'", false);

            var float_ = await GetOrCreateFloatAsync(companyId);

            // Reverse float balances — applies to BOTH Cash and FromAccount
            // Bureau's position was changed during the exchange regardless of type
            if (exchange.Direction == ExchangeDirection.UsdToKes)
            {
                // Original: float got USD, lost KES → Reverse: lose USD, gain KES
                float_.UsdBalance -= exchange.AmountGiven;
                float_.KesBalance += exchange.AmountReceived;
            }
            else
            {
                // Original: float got KES, lost USD → Reverse: lose KES, gain USD
                float_.KesBalance -= exchange.AmountGiven;
                float_.UsdBalance += exchange.AmountReceived;
            }

            // For FromAccount exchanges, also reverse client balances
            if (exchange.ExchangeType == ExchangeType.FromAccount)
            {
                var client = exchange.ClientId.HasValue
                    ? await _context.Users.FindAsync(exchange.ClientId.Value)
                    : null;
                if (client != null)
                {
                    if (exchange.Direction == ExchangeDirection.UsdToKes)
                    {
                        client.BalanceUSD += exchange.AmountGiven;
                        client.BalanceKES -= exchange.AmountReceived;
                    }
                    else
                    {
                        client.BalanceKES += exchange.AmountGiven;
                        client.BalanceUSD -= exchange.AmountReceived;
                    }
                    client.UpdatedAt = DateTime.UtcNow;
                }
            }

            // Reverse profit
            float_.KesProfit -= exchange.Profit;
            float_.UpdatedAt = DateTime.UtcNow;

            // Mark exchange as voided
            exchange.Status = "Voided";
            exchange.VoidReason = reason;
            exchange.UpdatedAt = DateTime.UtcNow;

            // ============================================================
            // MARK LINKED CLIENT TRANSACTIONS AS VOIDED
            // For FromAccount exchanges, there are linked Transaction records
            // in the client statement that need to reflect the void.
            // ============================================================
            if (exchange.ClientSourceTransactionId.HasValue)
            {
                var srcTxn = await _context.Transactions.FindAsync(exchange.ClientSourceTransactionId.Value);
                if (srcTxn != null)
                {
                    srcTxn.Description = $"[VOIDED] {srcTxn.Description}";
                    srcTxn.DeletedAt = DateTime.UtcNow;
                    srcTxn.DeletedByUserId = userId;
                    srcTxn.DeleteReason = $"Exchange {exchange.Code} voided: {reason}";
                    srcTxn.Notes = (srcTxn.Notes ?? "") + $" [Voided: {exchange.Code}]";
                    srcTxn.UpdatedAt = DateTime.UtcNow;
                }
            }
            if (exchange.ClientDestTransactionId.HasValue)
            {
                var destTxn = await _context.Transactions.FindAsync(exchange.ClientDestTransactionId.Value);
                if (destTxn != null)
                {
                    destTxn.Description = $"[VOIDED] {destTxn.Description}";
                    destTxn.DeletedAt = DateTime.UtcNow;
                    destTxn.DeletedByUserId = userId;
                    destTxn.DeleteReason = $"Exchange {exchange.Code} voided: {reason}";
                    destTxn.Notes = (destTxn.Notes ?? "") + $" [Voided: {exchange.Code}]";
                    destTxn.UpdatedAt = DateTime.UtcNow;
                }
            }

            // Create reversal transaction entries for client statement audit trail
            if (exchange.ExchangeType == ExchangeType.FromAccount && exchange.ClientId.HasValue)
            {
                var clientAfterVoid = await _context.Users.FindAsync(exchange.ClientId);
                if (clientAfterVoid != null)
                {
                    if (exchange.Direction == ExchangeDirection.UsdToKes)
                    {
                        // Original took USD out, gave KES in. Void reverses that.
                        var revCode1 = await CodeGenerator.GenerateTransactionCodeAsync(_context, companyId);
                        _context.Transactions.Add(new Transaction
                        {
                            Code = revCode1, CompanyId = companyId,
                            Reference = $"REV-EXC-{exchange.Code}",
                            TransactionDate = DateTime.UtcNow,
                            TransactionType = TransactionType.Credit, // Opposite of original Debit
                            Amount = exchange.AmountGiven, Currency = Currency.USD,
                            Description = $"VOID REVERSAL: Return USD {exchange.AmountGiven:N2}",
                            Notes = $"Void of exchange {exchange.Code}. Reason: {reason}",
                            SourceAccountType = AccountType.Client, SourceAccountId = exchange.ClientId.Value,
                            SourceBalanceBefore = clientAfterVoid.BalanceUSD - exchange.AmountGiven,
                            SourceBalanceAfter = clientAfterVoid.BalanceUSD,
                            DestAccountType = AccountType.Client, DestAccountId = exchange.ClientId.Value,
                            DestBalanceBefore = clientAfterVoid.BalanceUSD - exchange.AmountGiven,
                            DestBalanceAfter = clientAfterVoid.BalanceUSD,
                            CreatedByUserId = userId,
                            ReconciliationStatus = ReconciliationStatus.Matched
                        });
                        var revCode2 = await CodeGenerator.GenerateTransactionCodeAsync(_context, companyId);
                        _context.Transactions.Add(new Transaction
                        {
                            Code = revCode2, CompanyId = companyId,
                            Reference = $"REV-EXC-{exchange.Code}",
                            TransactionDate = DateTime.UtcNow,
                            TransactionType = TransactionType.Debit, // Opposite of original Credit
                            Amount = exchange.AmountReceived, Currency = Currency.KES,
                            Description = $"VOID REVERSAL: Reverse KES {exchange.AmountReceived:N2}",
                            Notes = $"Void of exchange {exchange.Code}. Reason: {reason}",
                            SourceAccountType = AccountType.Client, SourceAccountId = exchange.ClientId.Value,
                            SourceBalanceBefore = clientAfterVoid.BalanceKES + exchange.AmountReceived,
                            SourceBalanceAfter = clientAfterVoid.BalanceKES,
                            DestAccountType = AccountType.Client, DestAccountId = exchange.ClientId.Value,
                            DestBalanceBefore = clientAfterVoid.BalanceKES + exchange.AmountReceived,
                            DestBalanceAfter = clientAfterVoid.BalanceKES,
                            CreatedByUserId = userId,
                            ReconciliationStatus = ReconciliationStatus.Matched
                        });
                    }
                    else
                    {
                        var revCode1 = await CodeGenerator.GenerateTransactionCodeAsync(_context, companyId);
                        _context.Transactions.Add(new Transaction
                        {
                            Code = revCode1, CompanyId = companyId,
                            Reference = $"REV-EXC-{exchange.Code}",
                            TransactionDate = DateTime.UtcNow,
                            TransactionType = TransactionType.Credit, // Opposite of original Debit
                            Amount = exchange.AmountGiven, Currency = Currency.KES,
                            Description = $"VOID REVERSAL: Return KES {exchange.AmountGiven:N2}",
                            Notes = $"Void of exchange {exchange.Code}. Reason: {reason}",
                            SourceAccountType = AccountType.Client, SourceAccountId = exchange.ClientId.Value,
                            SourceBalanceBefore = clientAfterVoid.BalanceKES - exchange.AmountGiven,
                            SourceBalanceAfter = clientAfterVoid.BalanceKES,
                            DestAccountType = AccountType.Client, DestAccountId = exchange.ClientId.Value,
                            DestBalanceBefore = clientAfterVoid.BalanceKES - exchange.AmountGiven,
                            DestBalanceAfter = clientAfterVoid.BalanceKES,
                            CreatedByUserId = userId,
                            ReconciliationStatus = ReconciliationStatus.Matched
                        });
                        var revCode2 = await CodeGenerator.GenerateTransactionCodeAsync(_context, companyId);
                        _context.Transactions.Add(new Transaction
                        {
                            Code = revCode2, CompanyId = companyId,
                            Reference = $"REV-EXC-{exchange.Code}",
                            TransactionDate = DateTime.UtcNow,
                            TransactionType = TransactionType.Debit, // Opposite of original Credit
                            Amount = exchange.AmountReceived, Currency = Currency.USD,
                            Description = $"VOID REVERSAL: Reverse USD {exchange.AmountReceived:N2}",
                            Notes = $"Void of exchange {exchange.Code}. Reason: {reason}",
                            SourceAccountType = AccountType.Client, SourceAccountId = exchange.ClientId.Value,
                            SourceBalanceBefore = clientAfterVoid.BalanceUSD + exchange.AmountReceived,
                            SourceBalanceAfter = clientAfterVoid.BalanceUSD,
                            DestAccountType = AccountType.Client, DestAccountId = exchange.ClientId.Value,
                            DestBalanceBefore = clientAfterVoid.BalanceUSD + exchange.AmountReceived,
                            DestBalanceAfter = clientAfterVoid.BalanceUSD,
                            CreatedByUserId = userId,
                            ReconciliationStatus = ReconciliationStatus.Matched
                        });
                    }
                }
            }

            await _context.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            await _systemLog.LogErrorAsync("ExchangeService",
                $"Exchange {exchange.Code} voided by user {userId}. Reason: {reason}", null, companyId, userId);

            return new ApiResponse<bool>(true, $"Exchange {exchange.Code} voided successfully", true);
        }
        catch (Exception ex)
        {
            await dbTransaction.RollbackAsync();
            await _systemLog.LogErrorAsync("ExchangeService", $"Failed to void exchange: {ex.Message}",
                ex.StackTrace, companyId, userId);
            return new ApiResponse<bool>(false, "Failed to void exchange. Please try again.", false);
        }
    }

    /// <summary>
    /// Get today's daily summary — builds it from live data if not closed.
    /// Morning: shows opening float + today's exchanges.
    /// Evening: shows full day summary before closing.
    /// </summary>
    public async Task<ApiResponse<DailySummaryDto>> GetTodaySummaryAsync(Guid companyId)
    {
        var today = DateTime.UtcNow.Date;

        // Check if there's an existing summary for today
        var existing = await _context.DailyExchangeSummaries
            .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Date == today && !d.IsDeleted);

        if (existing != null)
            return new ApiResponse<DailySummaryDto>(true, "Success", MapToDailySummaryDto(existing));

        // Build live summary from today's exchanges
        var float_ = await GetOrCreateFloatAsync(companyId);
        var todayExchanges = await _context.ExchangeTransactions
            .Where(e => e.CompanyId == companyId && e.TransactionDate >= today
                        && e.Status == "Completed" && !e.IsDeleted)
            .ToListAsync();

        var totalTransactions = await _context.Transactions
            .CountAsync(t => t.CompanyId == companyId && t.TransactionDate >= today && !t.IsDeleted);

        var dto = new DailySummaryDto(
            Date: today,
            TotalTransactions: totalTransactions,
            ExchangeCount: todayExchanges.Count,
            KesVolumeIn: todayExchanges.Where(e => e.CurrencyGiven == Currency.KES).Sum(e => e.AmountGiven),
            KesVolumeOut: todayExchanges.Where(e => e.CurrencyReceived == Currency.KES).Sum(e => e.AmountReceived),
            UsdVolumeIn: todayExchanges.Where(e => e.CurrencyGiven == Currency.USD).Sum(e => e.AmountGiven),
            UsdVolumeOut: todayExchanges.Where(e => e.CurrencyReceived == Currency.USD).Sum(e => e.AmountReceived),
            KesProfit: todayExchanges.Where(e => e.ProfitCurrency == Currency.KES).Sum(e => e.Profit),
            UsdProfit: todayExchanges.Where(e => e.ProfitCurrency == Currency.USD).Sum(e => e.Profit),
            OpeningKes: float_.LastOpeningKes ?? float_.KesBalance,
            OpeningUsd: float_.LastOpeningUsd ?? float_.UsdBalance,
            ClosingKes: float_.KesBalance,
            ClosingUsd: float_.UsdBalance,
            KesVariance: null,
            UsdVariance: null,
            IsClosed: false
        );

        return new ApiResponse<DailySummaryDto>(true, "Success", dto);
    }

    /// <summary>
    /// Record opening float — cashier physically counts KES/USD at start of day.
    /// Stores the opening snapshot and checks for variance against system balance.
    /// </summary>
    public async Task<ApiResponse<DailySummaryDto>> RecordOpeningFloatAsync(Guid companyId, Guid userId, OpeningFloatDto dto)
    {
        var today = DateTime.UtcNow.Date;
        var float_ = await GetOrCreateFloatAsync(companyId);

        // Check if already recorded today
        var existing = await _context.DailyExchangeSummaries
            .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Date == today && !d.IsDeleted);

        if (existing != null)
            return new ApiResponse<DailySummaryDto>(false, "Opening float already recorded for today", MapToDailySummaryDto(existing));

        // Record the opening physical count
        float_.LastOpeningDate = today;
        float_.LastOpeningKes = dto.KesCount;
        float_.LastOpeningUsd = dto.UsdCount;
        float_.UpdatedAt = DateTime.UtcNow;

        // Create daily summary with opening values
        var summary = new DailyExchangeSummary
        {
            CompanyId = companyId,
            Date = today,
            OpeningKes = dto.KesCount,
            OpeningUsd = dto.UsdCount,
            ClosingKes = float_.KesBalance,
            ClosingUsd = float_.UsdBalance,
            Notes = dto.Notes,
            IsClosed = false
        };

        _context.DailyExchangeSummaries.Add(summary);
        await _context.SaveChangesAsync();

        return new ApiResponse<DailySummaryDto>(true, "Opening float recorded", MapToDailySummaryDto(summary));
    }

    /// <summary>
    /// Record closing float — cashier physically counts KES/USD at end of day.
    /// Calculates variance (physical count - system balance).
    /// Locks the day from further edits.
    /// </summary>
    public async Task<ApiResponse<DailySummaryDto>> RecordClosingFloatAsync(Guid companyId, Guid userId, ClosingFloatDto dto)
    {
        var today = DateTime.UtcNow.Date;

        var summary = await _context.DailyExchangeSummaries
            .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Date == today && !d.IsDeleted);

        if (summary == null)
        {
            // No opening recorded — auto-create with current float as opening
            var floatForOpening = await GetOrCreateFloatAsync(companyId);
            summary = new DailyExchangeSummary
            {
                CompanyId = companyId,
                Date = today,
                OpeningKes = floatForOpening.KesBalance,
                OpeningUsd = floatForOpening.UsdBalance,
                IsClosed = false
            };
            _context.DailyExchangeSummaries.Add(summary);
        }

        if (summary.IsClosed)
            return new ApiResponse<DailySummaryDto>(false, "Today's summary is already closed", MapToDailySummaryDto(summary));

        var float_ = await GetOrCreateFloatAsync(companyId);

        // Compute today's exchange volumes
        var todayExchanges = await _context.ExchangeTransactions
            .Where(e => e.CompanyId == companyId && e.TransactionDate >= today
                        && e.Status == "Completed" && !e.IsDeleted)
            .ToListAsync();

        summary.ExchangeCount = todayExchanges.Count;
        summary.KesVolumeIn = todayExchanges.Where(e => e.CurrencyGiven == Currency.KES).Sum(e => e.AmountGiven);
        summary.KesVolumeOut = todayExchanges.Where(e => e.CurrencyReceived == Currency.KES).Sum(e => e.AmountReceived);
        summary.UsdVolumeIn = todayExchanges.Where(e => e.CurrencyGiven == Currency.USD).Sum(e => e.AmountGiven);
        summary.UsdVolumeOut = todayExchanges.Where(e => e.CurrencyReceived == Currency.USD).Sum(e => e.AmountReceived);
        summary.KesProfit = todayExchanges.Where(e => e.ProfitCurrency == Currency.KES).Sum(e => e.Profit);
        summary.UsdProfit = todayExchanges.Where(e => e.ProfitCurrency == Currency.USD).Sum(e => e.Profit);

        // Closing = system balance
        summary.ClosingKes = float_.KesBalance;
        summary.ClosingUsd = float_.UsdBalance;

        // Physical count vs system → variance (positive = surplus, negative = shortage)
        summary.ActualKesCount = dto.KesCount;
        summary.ActualUsdCount = dto.UsdCount;
        summary.KesVariance = dto.KesCount - float_.KesBalance;
        summary.UsdVariance = dto.UsdCount - float_.UsdBalance;

        summary.Notes = dto.Notes;
        summary.IsClosed = true;
        summary.ClosedByUserId = userId;
        summary.ClosedAt = DateTime.UtcNow;
        summary.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return new ApiResponse<DailySummaryDto>(true, "Day closed successfully", MapToDailySummaryDto(summary));
    }

    /// <summary>
    /// Get daily summaries for a date range.
    /// </summary>
    public async Task<ApiResponse<List<DailySummaryDto>>> GetDailySummariesAsync(Guid companyId, DateTime from, DateTime to)
    {
        var summaries = await _context.DailyExchangeSummaries
            .Where(d => d.CompanyId == companyId && d.Date >= from.Date && d.Date <= to.Date && !d.IsDeleted)
            .OrderByDescending(d => d.Date)
            .ToListAsync();

        return new ApiResponse<List<DailySummaryDto>>(true, "Success",
            summaries.Select(MapToDailySummaryDto).ToList());
    }

    /// <summary>
    /// Profit report for a date range — aggregates daily profits and transaction counts.
    /// </summary>
    public async Task<ApiResponse<ProfitReportDto>> GetProfitReportAsync(Guid companyId, DateTime from, DateTime to)
    {
        var exchanges = await _context.ExchangeTransactions
            .Where(e => e.CompanyId == companyId
                        && e.TransactionDate >= from.Date
                        && e.TransactionDate < to.Date.AddDays(1)
                        && e.Status == "Completed"
                        && !e.IsDeleted)
            .ToListAsync();

        var dailyBreakdown = exchanges
            .GroupBy(e => e.TransactionDate.Date)
            .Select(g => new DailyProfitDto(
                Date: g.Key,
                KesProfit: g.Where(e => e.ProfitCurrency == Currency.KES).Sum(e => e.Profit),
                UsdProfit: g.Where(e => e.ProfitCurrency == Currency.USD).Sum(e => e.Profit),
                Transactions: g.Count()
            ))
            .OrderByDescending(d => d.Date)
            .ToList();

        var totalKesProfit = exchanges.Where(e => e.ProfitCurrency == Currency.KES).Sum(e => e.Profit);
        var totalUsdProfit = exchanges.Where(e => e.ProfitCurrency == Currency.USD).Sum(e => e.Profit);

        // Get current sell rate for KES equivalent of USD profit
        var rate = await _context.ExchangeRates.FirstOrDefaultAsync(r => r.CompanyId == companyId && r.IsActive);
        var rateVal = rate?.SellRate ?? 0;
        var totalInKes = totalKesProfit + (totalUsdProfit * rateVal);

        // Average spread across the period
        decimal avgSpread = 0;
        if (exchanges.Count > 0)
        {
            var rates = await _context.ExchangeRates
                .Where(r => r.CompanyId == companyId && r.EffectiveFrom >= from.Date && r.EffectiveFrom <= to.Date.AddDays(1))
                .ToListAsync();
            if (rates.Count > 0)
                avgSpread = rates.Average(r => r.SellRate - r.BuyRate);
            else if (rate != null)
                avgSpread = rate.SellRate - rate.BuyRate;
        }

        var report = new ProfitReportDto(
            FromDate: from.Date,
            ToDate: to.Date,
            TotalKesProfit: totalKesProfit,
            TotalUsdProfit: totalUsdProfit,
            TotalProfitInKes: totalInKes,
            TotalTransactions: exchanges.Count,
            AverageSpread: avgSpread,
            DailyBreakdown: dailyBreakdown
        );

        return new ApiResponse<ProfitReportDto>(true, "Success", report);
    }

    /// <summary>
    /// Large transactions for CBK compliance reporting.
    /// Returns all exchanges where KES equivalent >= threshold.
    /// </summary>
    public async Task<ApiResponse<List<LargeTransactionReportDto>>> GetLargeTransactionsAsync(Guid companyId, DateTime from, DateTime to, decimal threshold)
    {
        var exchanges = await _context.ExchangeTransactions
            .Where(e => e.CompanyId == companyId
                        && e.TransactionDate >= from.Date
                        && e.TransactionDate < to.Date.AddDays(1)
                        && e.Status == "Completed"
                        && !e.IsDeleted)
            .ToListAsync();

        // Get client details for matching exchanges
        var clientIds = exchanges.Where(e => e.ClientId.HasValue).Select(e => e.ClientId!.Value).Distinct().ToList();
        var clients = await _context.Users
            .Where(u => clientIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u);

        var results = new List<LargeTransactionReportDto>();

        foreach (var e in exchanges)
        {
            decimal kesEquivalent = e.CurrencyGiven == Currency.KES
                ? e.AmountGiven
                : e.AmountReceived; // KES side of the exchange

            if (kesEquivalent < threshold) continue;

            User? client = e.ClientId.HasValue ? clients.GetValueOrDefault(e.ClientId.Value) : null;

            results.Add(new LargeTransactionReportDto(
                TransactionId: e.Id,
                Code: e.Code,
                Date: e.TransactionDate,
                ClientName: client?.FullName ?? e.ClientName ?? "Walk-in Client",
                ClientIdNumber: e.ClientIdNumber ?? client?.IdPassport ?? "N/A",
                ClientPhone: client?.WhatsAppNumber,
                Amount: e.AmountGiven,
                Currency: e.CurrencyGiven,
                KesEquivalent: kesEquivalent,
                TransactionType: $"{e.Direction} ({e.ExchangeType})"
            ));
        }

        return new ApiResponse<List<LargeTransactionReportDto>>(true, "Success",
            results.OrderByDescending(r => r.KesEquivalent).ToList());
    }

    /// <summary>
    /// Client's complete exchange history with totals.
    /// </summary>
    public async Task<ApiResponse<ClientExchangeHistoryDto>> GetClientExchangeHistoryAsync(Guid companyId, Guid clientId)
    {
        var client = await _context.Users.FindAsync(clientId);
        if (client == null || client.CompanyId != companyId)
            return new ApiResponse<ClientExchangeHistoryDto>(false, "Client not found", null);

        var exchanges = await _context.ExchangeTransactions
            .Where(e => e.CompanyId == companyId && e.ClientId == clientId
                        && e.Status == "Completed" && !e.IsDeleted)
            .OrderByDescending(e => e.TransactionDate)
            .ToListAsync();

        var float_ = await GetOrCreateFloatAsync(companyId);

        var recentExchanges = exchanges.Take(20).Select(e =>
        {
            decimal kesEq = e.CurrencyGiven == Currency.KES ? e.AmountGiven : e.AmountReceived;
            return MapToExchangeDto(e, client.FullName, client.ClientType.ToString(),
                kesEq >= float_.LargeTransactionThreshold);
        }).ToList();

        var history = new ClientExchangeHistoryDto(
            ClientId: clientId,
            ClientName: client.FullName,
            TotalExchanges: exchanges.Count,
            TotalKesExchanged: exchanges.Where(e => e.CurrencyGiven == Currency.KES).Sum(e => e.AmountGiven)
                             + exchanges.Where(e => e.CurrencyReceived == Currency.KES).Sum(e => e.AmountReceived),
            TotalUsdExchanged: exchanges.Where(e => e.CurrencyGiven == Currency.USD).Sum(e => e.AmountGiven)
                             + exchanges.Where(e => e.CurrencyReceived == Currency.USD).Sum(e => e.AmountReceived),
            TotalProfitGenerated: exchanges.Sum(e => e.Profit),
            FirstExchange: exchanges.LastOrDefault()?.TransactionDate,
            LastExchange: exchanges.FirstOrDefault()?.TransactionDate,
            RecentExchanges: recentExchanges
        );

        return new ApiResponse<ClientExchangeHistoryDto>(true, "Success", history);
    }

    /// <summary>
    /// USD Position — average cost inventory tracking.
    /// Shows current USD holdings, cost basis, and unrealized P&L based on current sell rate.
    /// </summary>
    public async Task<ApiResponse<UsdPositionDto>> GetUsdPositionAsync(Guid companyId)
    {
        var float_ = await GetOrCreateFloatAsync(companyId);
        var rate = await _context.ExchangeRates.FirstOrDefaultAsync(r => r.CompanyId == companyId && r.IsActive);

        var currentMarketRate = rate?.SellRate ?? 0;
        var marketValue = float_.UsdBalance * currentMarketRate;
        var unrealizedPnL = marketValue - float_.UsdTotalCost;
        var unrealizedPnLPercent = float_.UsdTotalCost > 0
            ? (unrealizedPnL / float_.UsdTotalCost) * 100
            : 0;

        var position = new UsdPositionDto(
            UsdBalance: float_.UsdBalance,
            AverageCostPerUsd: float_.UsdAverageCost,
            TotalCostBasis: float_.UsdTotalCost,
            CurrentMarketRate: currentMarketRate,
            CurrentMarketValue: marketValue,
            UnrealizedPnL: unrealizedPnL,
            UnrealizedPnLPercent: Math.Round(unrealizedPnLPercent, 2)
        );

        return new ApiResponse<UsdPositionDto>(true, "Success", position);
    }

    /// <summary>
    /// Float alerts — checks low balance thresholds and returns active warnings.
    /// </summary>
    public async Task<ApiResponse<List<FloatAlertDto>>> GetAlertsAsync(Guid companyId)
    {
        var float_ = await GetOrCreateFloatAsync(companyId);
        var alerts = new List<FloatAlertDto>();
        var now = DateTime.UtcNow;

        // Low KES balance
        if (float_.KesBalance < float_.LowKesThreshold)
        {
            alerts.Add(new FloatAlertDto(
                AlertType: "LowBalance",
                Message: $"KES float balance ({float_.KesBalance:N2}) is below threshold ({float_.LowKesThreshold:N2})",
                Currency: Currency.KES,
                CurrentBalance: float_.KesBalance,
                Threshold: float_.LowKesThreshold,
                Timestamp: now
            ));
        }

        // Low USD balance
        if (float_.UsdBalance < float_.LowUsdThreshold)
        {
            alerts.Add(new FloatAlertDto(
                AlertType: "LowBalance",
                Message: $"USD float balance (${float_.UsdBalance:N2}) is below threshold (${float_.LowUsdThreshold:N2})",
                Currency: Currency.USD,
                CurrentBalance: float_.UsdBalance,
                Threshold: float_.LowUsdThreshold,
                Timestamp: now
            ));
        }

        // Negative balance (critical)
        if (float_.KesBalance < 0)
        {
            alerts.Add(new FloatAlertDto(
                AlertType: "NegativeBalance",
                Message: $"CRITICAL: KES float is negative ({float_.KesBalance:N2})",
                Currency: Currency.KES,
                CurrentBalance: float_.KesBalance,
                Threshold: 0,
                Timestamp: now
            ));
        }

        if (float_.UsdBalance < 0)
        {
            alerts.Add(new FloatAlertDto(
                AlertType: "NegativeBalance",
                Message: $"CRITICAL: USD float is negative (${float_.UsdBalance:N2})",
                Currency: Currency.USD,
                CurrentBalance: float_.UsdBalance,
                Threshold: 0,
                Timestamp: now
            ));
        }

        // Unsettled profit alert (if profit is large)
        var unsettledProfit = float_.KesProfit + float_.UsdProfit;
        if (unsettledProfit > 100000)
        {
            alerts.Add(new FloatAlertDto(
                AlertType: "HighUnsettledProfit",
                Message: $"Unsettled profit is high: KES {float_.KesProfit:N2} + USD {float_.UsdProfit:N2}. Consider settling.",
                Currency: null,
                CurrentBalance: unsettledProfit,
                Threshold: 100000,
                Timestamp: now
            ));
        }

        return new ApiResponse<List<FloatAlertDto>>(true, "Success", alerts);
    }

    /// <summary>
    /// Update alert thresholds for the float.
    /// </summary>
    public async Task<ApiResponse<bool>> UpdateAlertThresholdsAsync(Guid companyId, decimal lowKes, decimal lowUsd, decimal largeTransaction)
    {
        if (lowKes < 0 || lowUsd < 0 || largeTransaction < 0)
            return new ApiResponse<bool>(false, "Thresholds must be non-negative", false);

        var float_ = await GetOrCreateFloatAsync(companyId);

        float_.LowKesThreshold = lowKes;
        float_.LowUsdThreshold = lowUsd;
        float_.LargeTransactionThreshold = largeTransaction;
        float_.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return new ApiResponse<bool>(true, "Thresholds updated", true);
    }

    // ==================== HELPER METHODS ====================

    private async Task<ExchangeFloat> GetOrCreateFloatAsync(Guid companyId)
    {
        var float_ = await _context.ExchangeFloats.FirstOrDefaultAsync(f => f.CompanyId == companyId);
        if (float_ != null) return float_;

        // Float doesn't exist — try to create it
        try
        {
            float_ = new ExchangeFloat
            {
                CompanyId = companyId,
                KesBalance = 0,
                UsdBalance = 0,
                KesProfit = 0,
                UsdProfit = 0,
                LowKesThreshold = 50000,
                LowUsdThreshold = 500,
                LargeTransactionThreshold = 500000
            };
            _context.ExchangeFloats.Add(float_);
            await _context.SaveChangesAsync();
            return float_;
        }
        catch (DbUpdateException)
        {
            // Another request created it first — detach our failed entity and re-query
            _context.Entry(float_).State = EntityState.Detached;
            return await _context.ExchangeFloats.FirstAsync(f => f.CompanyId == companyId);
        }
    }

    private async Task<decimal> GetAccountBalanceAsync(AccountType type, Guid accountId)
    {
        return type switch
        {
            AccountType.Cash => (await _context.CashAccounts.FindAsync(accountId))?.Balance ?? 0,
            AccountType.Bank => (await _context.BankAccounts.FindAsync(accountId))?.Balance ?? 0,
            AccountType.Mpesa => (await _context.MpesaAgents.FindAsync(accountId))?.Balance ?? 0,
            _ => 0
        };
    }

    private async Task UpdateAccountBalanceAsync(AccountType type, Guid accountId, decimal amount)
    {
        switch (type)
        {
            case AccountType.Cash:
                var cash = await _context.CashAccounts.FindAsync(accountId);
                if (cash != null) { cash.Balance += amount; cash.UpdatedAt = DateTime.UtcNow; }
                break;
            case AccountType.Bank:
                var bank = await _context.BankAccounts.FindAsync(accountId);
                if (bank != null) { bank.Balance += amount; bank.UpdatedAt = DateTime.UtcNow; }
                break;
            case AccountType.Mpesa:
                var mpesa = await _context.MpesaAgents.FindAsync(accountId);
                if (mpesa != null) { mpesa.Balance += amount; mpesa.UpdatedAt = DateTime.UtcNow; }
                break;
        }
    }

    /// <summary>
    /// FIXED: Create transaction with proper balance fields
    /// </summary>
    private async Task CreateFloatTransactionAsync(
        Guid companyId, Guid userId, AccountType accountType, Guid accountId,
        TransactionType txnType, decimal amount, Currency currency,
        decimal balanceBefore, decimal balanceAfter,
        string description, string? notes)
    {
        var transaction = new Transaction
        {
            Code = await CodeGenerator.GenerateTransactionCodeAsync(_context, companyId),
            CompanyId = companyId,
            Reference = "FLOAT-TXN",
            TransactionDate = DateTime.UtcNow,
            TransactionType = txnType,
            Amount = amount,
            Currency = currency,
            Description = description,
            Notes = notes,
            SourceAccountType = accountType,
            SourceAccountId = accountId,
            SourceBalanceBefore = balanceBefore,   // FIXED: Now included
            SourceBalanceAfter = balanceAfter,     // FIXED: Now included
            DestAccountType = accountType,
            DestAccountId = accountId,
            DestBalanceBefore = balanceBefore,     // FIXED: Now included
            DestBalanceAfter = balanceAfter,       // FIXED: Now included
            CreatedByUserId = userId
        };
        _context.Transactions.Add(transaction);
    }

    private static ExchangeRateResponseDto MapRateToResponse(ExchangeRate r) => new(
        r.Id, r.BuyRate, r.SellRate, r.EffectiveFrom, r.EffectiveTo, r.IsActive
    );

    private static ExchangeFloatDto MapToFloatDto(ExchangeFloat f) => new(
        f.Id, f.KesBalance, f.UsdBalance, f.KesProfit, f.UsdProfit, f.UsdAverageCost, f.UpdatedAt
    );

    private static ExchangeResponseDto MapToExchangeDto(ExchangeTransaction e, string clientName, string clientType, bool isLarge) => new(
        e.Id, e.Code, e.TransactionDate, e.ClientId, clientName, clientType,
        e.ExchangeType, e.Direction, e.AmountGiven, e.CurrencyGiven,
        e.AmountReceived, e.CurrencyReceived, e.ExchangeRate,
        e.Profit, e.ProfitCurrency, e.Notes, e.Status, isLarge
    );

    private static DailySummaryDto MapToDailySummaryDto(DailyExchangeSummary d) => new(
        d.Date, 0, d.ExchangeCount,
        d.KesVolumeIn, d.KesVolumeOut,
        d.UsdVolumeIn, d.UsdVolumeOut,
        d.KesProfit, d.UsdProfit,
        d.OpeningKes, d.OpeningUsd,
        d.ClosingKes, d.ClosingUsd,
        d.KesVariance, d.UsdVariance,
        d.IsClosed
    );
}