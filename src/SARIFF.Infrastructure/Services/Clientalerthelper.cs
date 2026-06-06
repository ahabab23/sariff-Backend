
using Microsoft.EntityFrameworkCore;
using SARIFF.Core.Entities;
using SARIFF.Core.Enums;
using SARIFF.Infrastructure.Data;

namespace SARIFF.Infrastructure.Services;

public interface IClientAlertHelper
{
    Task CreateTransactionAlertAsync(Transaction transaction, Guid clientId, Guid companyId, bool isIncoming);
    Task CreateAlertAsync(Guid companyId, Guid clientId, string type, string title, string message, Guid? relatedTransactionId = null);
    Task CreateLowBalanceAlertAsync(Guid companyId, Guid clientId, decimal balance, Currency currency);
    Task CreateWelcomeAlertAsync(Guid companyId, Guid clientId, string clientName);
}

public class ClientAlertHelper : IClientAlertHelper
{
    private readonly AppDbContext _context;

    public ClientAlertHelper(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Creates an alert when a transaction is processed for a client
    /// </summary>
    /// <param name="transaction">The transaction entity</param>
    /// <param name="clientId">The client's user ID</param>
    /// <param name="companyId">The company ID</param>
    /// <param name="isIncoming">True if money is coming TO the client (Credit), false if going FROM client (Debit)</param>
    public async Task CreateTransactionAlertAsync(Transaction transaction, Guid clientId, Guid companyId, bool isIncoming)
    {
        var currencySymbol = transaction.Currency == Currency.KES ? "KES" : "USD";
        var amount = transaction.Amount;
        
        string type, title, message;

        if (isIncoming)
        {
            type = "success";
            title = "Funds Received";
            message = $"You have received {currencySymbol} {amount:N2}. " +
                      $"Description: {transaction.Description}. " +
                      $"Reference: {transaction.Reference}";
        }
        else
        {
            type = "info";
            title = "Funds Sent";
            message = $"A payment of {currencySymbol} {amount:N2} has been processed from your account. " +
                      $"Description: {transaction.Description}. " +
                      $"Reference: {transaction.Reference}";
        }

        await CreateAlertAsync(companyId, clientId, type, title, message, transaction.Id);
    }

    /// <summary>
    /// Creates a generic alert for a client
    /// </summary>
    public async Task CreateAlertAsync(Guid companyId, Guid clientId, string type, string title, string message, Guid? relatedTransactionId = null)
    {
        // Check for duplicate alert (same title and transaction in last hour)
        var recentDuplicate = await _context.Set<ClientAlert>()
            .AnyAsync(a => a.ClientId == clientId && 
                          a.CompanyId == companyId && 
                          a.Title == title &&
                          a.RelatedTransactionId == relatedTransactionId &&
                          a.CreatedAt > DateTime.UtcNow.AddHours(-1) &&
                          !a.IsDeleted);

        if (recentDuplicate)
            return; // Don't create duplicate alerts

        var alert = new ClientAlert
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CompanyId = companyId,
            Type = type,  // "success", "info", "warning", "error"
            Title = title,
            Message = message,
            IsRead = false,
            RelatedTransactionId = relatedTransactionId,
            CreatedAt = DateTime.UtcNow
        };

        _context.Set<ClientAlert>().Add(alert);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Creates a low balance warning alert
    /// </summary>
    public async Task CreateLowBalanceAlertAsync(Guid companyId, Guid clientId, decimal balance, Currency currency)
    {
        var currencySymbol = currency == Currency.KES ? "KES" : "USD";
        var threshold = currency == Currency.KES ? "10,000" : "100";

        await CreateAlertAsync(
            companyId,
            clientId,
            "warning",
            "Low Balance Alert",
            $"Your {currencySymbol} balance is now {currencySymbol} {balance:N2}, which is below {currencySymbol} {threshold}. Consider topping up your account."
        );
    }

    /// <summary>
    /// Creates a welcome alert for new clients
    /// </summary>
    public async Task CreateWelcomeAlertAsync(Guid companyId, Guid clientId, string clientName)
    {
        await CreateAlertAsync(
            companyId,
            clientId,
            "success",
            "Welcome to Sarif!",
            $"Hello {clientName}! Your client portal account is now active. You can view your transactions, download statements, and track your account activity here."
        );
    }
}