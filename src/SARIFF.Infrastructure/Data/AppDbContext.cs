using Microsoft.EntityFrameworkCore;
using SARIFF.Core.Entities;

namespace SARIFF.Infrastructure.Data;

/// <summary>
/// FIXED AppDbContext
/// 
/// Changes:
/// 1. Added HasPrecision(18, 2) to ALL decimal fields
/// 2. Added missing indexes for frequently queried columns
/// 3. Added concurrency token for balance fields (optimistic locking)
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<User> Users => Set<User>();
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<MpesaAgent> MpesaAgents => Set<MpesaAgent>();
    public DbSet<CashAccount> CashAccounts => Set<CashAccount>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<ExpenseCategory> ExpenseCategories => Set<ExpenseCategory>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceItem> InvoiceItems => Set<InvoiceItem>();
    public DbSet<Reconciliation> Reconciliations => Set<Reconciliation>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<LoginHistory> LoginHistories => Set<LoginHistory>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<SystemLog> SystemLogs => Set<SystemLog>();
    public DbSet<AdminIpWhitelist> AdminIpWhitelists => Set<AdminIpWhitelist>();
    public DbSet<ClientAlert> ClientAlerts => Set<ClientAlert>();
    public DbSet<TrustedDevice> TrustedDevices => Set<TrustedDevice>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>(); // C3: Push notifications
    public DbSet<BalanceAlertRule> BalanceAlertRules => Set<BalanceAlertRule>();
    public DbSet<SecurityAlert> SecurityAlerts => Set<SecurityAlert>();
    public DbSet<SubscriptionPayment> SubscriptionPayments => Set<SubscriptionPayment>();
    public DbSet<BlockedIP> BlockedIPs => Set<BlockedIP>();
    public DbSet<ExchangeFloat> ExchangeFloats => Set<ExchangeFloat>();
    public DbSet<FloatMovement> FloatMovements => Set<FloatMovement>();
    public DbSet<ExchangeTransaction> ExchangeTransactions => Set<ExchangeTransaction>();
    public DbSet<DailyExchangeSummary> DailyExchangeSummaries => Set<DailyExchangeSummary>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // =====================================================
        // COMPANY
        // =====================================================
        modelBuilder.Entity<Company>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.Name).IsRequired().HasMaxLength(255);
            e.Property(x => x.OwnerName).IsRequired().HasMaxLength(255);
            e.Property(x => x.WhatsAppNumber).IsRequired().HasMaxLength(50);
            e.Property(x => x.Email).HasMaxLength(255);
            e.Property(x => x.PasswordHash).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.WhatsAppNumber).IsUnique();
            e.HasQueryFilter(x => !x.IsDeleted);
            
            // FIXED: All decimal fields with precision
            e.Property(x => x.MonthlyFee).HasPrecision(18, 2);
            e.Property(x => x.TotalPaid).HasPrecision(18, 2);
        });

        // =====================================================
        // USER (Clients + SuperAdmin)
        // =====================================================
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.FullName).IsRequired().HasMaxLength(255);
            e.Property(x => x.WhatsAppNumber).IsRequired().HasMaxLength(50);
            
            // FIXED: All balance fields with precision
            e.Property(x => x.BalanceKES).HasPrecision(18, 2);
            e.Property(x => x.BalanceUSD).HasPrecision(18, 2);
            e.Property(x => x.OpeningBalanceKES).HasPrecision(18, 2);  // NEW
            e.Property(x => x.OpeningBalanceUSD).HasPrecision(18, 2);  // NEW
            
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.Role });
            e.HasQueryFilter(x => !x.IsDeleted);
            
            // FIX #18: Optimistic concurrency — prevents lost updates on balance fields under load
            e.UseXminAsConcurrencyToken();
        });

        // =====================================================
        // BANK ACCOUNT
        // =====================================================
        modelBuilder.Entity<BankAccount>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.BankName).HasMaxLength(100);
            e.Property(x => x.AccountNumber).HasMaxLength(50);
            e.Property(x => x.AccountName).HasMaxLength(255);
            
            // FIXED: All decimal fields
            e.Property(x => x.Balance).HasPrecision(18, 2);
            e.Property(x => x.OpeningBalance).HasPrecision(18, 2);  // NEW
            
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.HasQueryFilter(x => !x.IsDeleted);
            e.UseXminAsConcurrencyToken();
        });

        // =====================================================
        // M-PESA AGENT
        // =====================================================
        modelBuilder.Entity<MpesaAgent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.AgentName).HasMaxLength(100);
            e.Property(x => x.PhoneNumber).HasMaxLength(50);
            e.Property(x => x.AgentNumber).HasMaxLength(50);
            
            // FIXED: All decimal fields
            e.Property(x => x.Balance).HasPrecision(18, 2);
            e.Property(x => x.OpeningBalance).HasPrecision(18, 2);  // NEW
            
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.HasQueryFilter(x => !x.IsDeleted);
            e.UseXminAsConcurrencyToken();
        });

        // =====================================================
        // CASH ACCOUNT
        // =====================================================
        modelBuilder.Entity<CashAccount>(e =>
        {
            e.HasKey(x => x.Id);
            
            // FIXED: All decimal fields
            e.Property(x => x.Balance).HasPrecision(18, 2);
            e.Property(x => x.OpeningBalance).HasPrecision(18, 2);  // NEW
            
            e.HasIndex(x => new { x.CompanyId, x.Currency });  // NEW: Composite index
            e.HasQueryFilter(x => !x.IsDeleted);
            e.UseXminAsConcurrencyToken();
        });

        // =====================================================
        // TRANSACTION
        // =====================================================
        modelBuilder.Entity<Transaction>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(30);
            e.Property(x => x.Reference).HasMaxLength(50);
            e.Property(x => x.Description).HasMaxLength(500);
            
            // FIXED: All decimal fields
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.CounterAmount).HasPrecision(18, 2);  // NEW
            e.Property(x => x.ExchangeRate).HasPrecision(18, 6);   // NEW: Higher precision for rates
            e.Property(x => x.SourceBalanceBefore).HasPrecision(18, 2);  // NEW
            e.Property(x => x.SourceBalanceAfter).HasPrecision(18, 2);   // NEW
            e.Property(x => x.DestBalanceBefore).HasPrecision(18, 2);    // NEW
            e.Property(x => x.DestBalanceAfter).HasPrecision(18, 2);     // NEW
            e.Property(x => x.ActualAmount).HasPrecision(18, 2);
            e.Property(x => x.Variance).HasPrecision(18, 2);
            
            // FIXED: Better indexes
            e.HasIndex(x => x.ReconciliationStatus);
            e.HasIndex(x => new { x.CompanyId, x.TransactionDate });
            e.HasIndex(x => x.SourceAccountId);
            e.HasIndex(x => x.DestAccountId);
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // =====================================================
        // EXPENSE CATEGORY
        // =====================================================
        modelBuilder.Entity<ExpenseCategory>(e => 
        { 
            e.HasKey(x => x.Id); 
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.CompanyId);  // NEW
            e.HasQueryFilter(x => !x.IsDeleted); 
        });

        // =====================================================
        // EXPENSE
        // =====================================================
        modelBuilder.Entity<Expense>(e => 
        { 
            e.HasKey(x => x.Id); 
            e.Property(x => x.Code).HasMaxLength(30);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.HasIndex(x => x.CategoryId);
            e.HasQueryFilter(x => !x.IsDeleted); 
        });

        // =====================================================
        // EXCHANGE RATE
        // =====================================================
        modelBuilder.Entity<ExchangeRate>(e => 
        { 
            e.HasKey(x => x.Id); 
            e.Property(x => x.BuyRate).HasPrecision(18, 6); 
            e.Property(x => x.SellRate).HasPrecision(18, 6); 
            e.HasIndex(x => new { x.CompanyId, x.IsActive });  // NEW
            e.HasQueryFilter(x => !x.IsDeleted); 
        });

        // =====================================================
        // INVOICE
        // =====================================================
        modelBuilder.Entity<Invoice>(e => 
        { 
            e.HasKey(x => x.Id); 
            e.Property(x => x.InvoiceNumber).HasMaxLength(30);
            e.Property(x => x.Subtotal).HasPrecision(18, 2);   // NEW
            e.Property(x => x.TaxRate).HasPrecision(5, 2);     // NEW
            e.Property(x => x.TaxAmount).HasPrecision(18, 2);  // NEW
            e.Property(x => x.DiscountAmount).HasPrecision(18, 2);  // NEW
            e.Property(x => x.Total).HasPrecision(18, 2); 
            e.HasIndex(x => new { x.CompanyId, x.InvoiceNumber }).IsUnique();
            e.HasQueryFilter(x => !x.IsDeleted); 
        });

        // =====================================================
        // INVOICE ITEM
        // =====================================================
        modelBuilder.Entity<InvoiceItem>(e => 
        { 
            e.HasKey(x => x.Id); 
            e.Property(x => x.Quantity).HasPrecision(18, 4);   // NEW
            e.Property(x => x.UnitPrice).HasPrecision(18, 2);  // NEW
            e.Property(x => x.Amount).HasPrecision(18, 2); 
            e.HasIndex(x => x.InvoiceId);  // NEW
            e.HasQueryFilter(x => !x.IsDeleted); 
        });

        // =====================================================
        // RECONCILIATION
        // =====================================================
        modelBuilder.Entity<Reconciliation>(e => 
        { 
            e.HasKey(x => x.Id); 
            e.Property(x => x.ExpectedBalance).HasPrecision(18, 2);  // NEW
            e.Property(x => x.ActualBalance).HasPrecision(18, 2);    // NEW
            e.Property(x => x.Variance).HasPrecision(18, 2); 
            e.HasIndex(x => x.CompanyId);  // NEW
            e.HasQueryFilter(x => !x.IsDeleted); 
        });

        // =====================================================
        // OTP CODE
        // =====================================================
        modelBuilder.Entity<OtpCode>(e => 
        { 
            e.HasKey(x => x.Id); 
            e.Property(x => x.UserCode).HasMaxLength(20);
            e.Property(x => x.PhoneNumber).HasMaxLength(50);
            e.Property(x => x.Code).HasMaxLength(10);
            e.HasIndex(x => new { x.PhoneNumber, x.IsUsed, x.ExpiresAt });  // NEW
        });

        // =====================================================
        // USER SESSION
        // =====================================================
        modelBuilder.Entity<UserSession>(e => 
        { 
            e.HasKey(x => x.Id); 
            e.Property(x => x.RefreshTokenHash).HasMaxLength(100);
            e.HasIndex(x => x.RefreshTokenHash);  // NEW
            e.HasIndex(x => new { x.UserId, x.IsRevoked });  // NEW
        });

        // =====================================================
        // OTHER ENTITIES
        // =====================================================
        modelBuilder.Entity<LoginHistory>(e => { e.HasKey(x => x.Id); });
        modelBuilder.Entity<AuditLog>(e => { e.HasKey(x => x.Id); });
        modelBuilder.Entity<NotificationLog>(e => { e.HasKey(x => x.Id); });
        modelBuilder.Entity<SystemLog>(e => { 
            e.HasKey(x => x.Id); 
            e.HasIndex(x => new { x.Level, x.CreatedAt });  // NEW
        });
        modelBuilder.Entity<AdminIpWhitelist>(e => { e.HasKey(x => x.Id); });

        // =====================================================
        // CLIENT ALERT
        // =====================================================
        modelBuilder.Entity<ClientAlert>(e => { 
            e.HasKey(x => x.Id); 
            e.Property(x => x.Type).HasMaxLength(20);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Message).HasMaxLength(1000);
            e.HasIndex(x => new { x.ClientId, x.CompanyId, x.IsDeleted });
            e.HasIndex(x => new { x.ClientId, x.IsRead });
            e.HasQueryFilter(x => !x.IsDeleted); 
        });

        // =====================================================
        // SECURITY ALERT
        // =====================================================
        modelBuilder.Entity<SecurityAlert>(e => { 
            e.HasKey(x => x.Id); 
            e.Property(x => x.Severity).HasMaxLength(20);
            e.Property(x => x.Message).HasMaxLength(1000);
            e.HasIndex(x => new { x.IsResolved, x.CreatedAt });
            e.HasQueryFilter(x => !x.IsDeleted); 
        });

        // =====================================================
        // SUBSCRIPTION PAYMENT
        // =====================================================
        modelBuilder.Entity<SubscriptionPayment>(e => { 
            e.HasKey(x => x.Id); 
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(10);
            e.Property(x => x.PaymentMethod).HasMaxLength(50);
            e.Property(x => x.Status).HasMaxLength(20);
            e.HasIndex(x => x.CompanyId);
            e.HasQueryFilter(x => !x.IsDeleted); 
        });

        // =====================================================
        // BLOCKED IP
        // =====================================================
        modelBuilder.Entity<BlockedIP>(e => { 
            e.HasKey(x => x.Id); 
            e.Property(x => x.IpAddress).HasMaxLength(50);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasIndex(x => x.IpAddress);
            e.HasQueryFilter(x => !x.IsDeleted); 
        });

        // =====================================================
        // EXCHANGE FLOAT
        // =====================================================
        modelBuilder.Entity<ExchangeFloat>(e => { 
            e.HasKey(x => x.Id);
            e.Property(x => x.KesBalance).HasPrecision(18, 2);
            e.Property(x => x.UsdBalance).HasPrecision(18, 2);
            e.Property(x => x.KesProfit).HasPrecision(18, 2);
            e.Property(x => x.UsdProfit).HasPrecision(18, 2);
            e.Property(x => x.UsdTotalCost).HasPrecision(18, 2);
            e.Property(x => x.UsdAverageCost).HasPrecision(18, 6);
            e.Property(x => x.LastOpeningKes).HasPrecision(18, 2);  // NEW
            e.Property(x => x.LastOpeningUsd).HasPrecision(18, 2);  // NEW
            e.Property(x => x.LowKesThreshold).HasPrecision(18, 2);  // NEW
            e.Property(x => x.LowUsdThreshold).HasPrecision(18, 2);  // NEW
            e.Property(x => x.LargeTransactionThreshold).HasPrecision(18, 2);  // NEW
            e.HasIndex(x => x.CompanyId).IsUnique();
            e.HasQueryFilter(x => !x.IsDeleted);
            e.UseXminAsConcurrencyToken();
        });
        
        // =====================================================
        // FLOAT MOVEMENT
        // =====================================================
        modelBuilder.Entity<FloatMovement>(e => { 
            e.HasKey(x => x.Id);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.BalanceBefore).HasPrecision(18, 2);
            e.Property(x => x.BalanceAfter).HasPrecision(18, 2);
            e.HasIndex(x => new { x.CompanyId, x.MovementDate });
            e.HasQueryFilter(x => !x.IsDeleted);
        });
        
        // =====================================================
        // EXCHANGE TRANSACTION
        // =====================================================
        modelBuilder.Entity<ExchangeTransaction>(e => { 
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.AmountGiven).HasPrecision(18, 2);
            e.Property(x => x.AmountReceived).HasPrecision(18, 2);
            e.Property(x => x.ExchangeRate).HasPrecision(18, 6);
            e.Property(x => x.Profit).HasPrecision(18, 2);
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.TransactionDate });
            e.HasIndex(x => x.ClientId);  // NEW
            e.HasQueryFilter(x => !x.IsDeleted);
        });
        
        // =====================================================
        // DAILY EXCHANGE SUMMARY
        // =====================================================
        modelBuilder.Entity<DailyExchangeSummary>(e => { 
            e.HasKey(x => x.Id);
            e.Property(x => x.OpeningKes).HasPrecision(18, 2);
            e.Property(x => x.OpeningUsd).HasPrecision(18, 2);
            e.Property(x => x.ClosingKes).HasPrecision(18, 2);
            e.Property(x => x.ClosingUsd).HasPrecision(18, 2);
            e.Property(x => x.KesVolumeIn).HasPrecision(18, 2);
            e.Property(x => x.KesVolumeOut).HasPrecision(18, 2);
            e.Property(x => x.UsdVolumeIn).HasPrecision(18, 2);
            e.Property(x => x.UsdVolumeOut).HasPrecision(18, 2);
            e.Property(x => x.KesProfit).HasPrecision(18, 2);
            e.Property(x => x.UsdProfit).HasPrecision(18, 2);
            e.Property(x => x.ActualKesCount).HasPrecision(18, 2);  // NEW
            e.Property(x => x.ActualUsdCount).HasPrecision(18, 2);  // NEW
            e.Property(x => x.KesVariance).HasPrecision(18, 2);     // NEW
            e.Property(x => x.UsdVariance).HasPrecision(18, 2);     // NEW
            e.HasIndex(x => new { x.CompanyId, x.Date }).IsUnique();
            e.HasQueryFilter(x => !x.IsDeleted);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = DateTime.UtcNow;
                entry.Entity.UpdatedAt = DateTime.UtcNow;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = DateTime.UtcNow;
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}