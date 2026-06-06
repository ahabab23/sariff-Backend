using SARIFF.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace SARIFF.Core.Entities;

/// <summary>
/// Base entity with common audit fields
/// </summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; } = false;
}

/// <summary>
/// Base entity with custom readable ID (e.g., CL-2025-001)
/// </summary>
public abstract class BaseEntityWithCode : BaseEntity
{
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// Company/Office User entity (PDF Section 2.2)
/// Represents a business subscribing to the platform
/// </summary>
public class Company : BaseEntity
{
    public string Code { get; set; } = string.Empty; // FB-2026-0001
    public string CodePrefix { get; set; } = string.Empty; // FB, AFB, SE (2-3 chars)
    public string Name { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string WhatsAppNumber { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    
    // Company branding for invoices (PDF Section 5.9)
    public string? LogoUrl { get; set; }
    public string? TaxId { get; set; }
    public string? Website { get; set; }
    public string? Address { get; set; }
    
    public bool IsActive { get; set; } = true;
    public int FailedLoginAttempts { get; set; } = 0;
    public DateTime? LockedUntil { get; set; }
    public DateTime? LastLoginAt { get; set; }
    // Subscription fields for SuperAdmin billing
    public SubscriptionPlan SubscriptionPlan { get; set; } = SubscriptionPlan.Free;
    public SubscriptionStatus SubscriptionStatus { get; set; } = SubscriptionStatus.Trial;
    public DateTime? SubscriptionStartDate { get; set; }
    public DateTime? SubscriptionExpiresAt { get; set; }
    public decimal MonthlyFee { get; set; } = 0;
    public decimal TotalPaid { get; set; } = 0;
    public DateTime? LastPaymentDate { get; set; }
    
    // Transaction PIN — set by SuperAdmin, verified by office users
    public string? TransactionPinHash { get; set; }
    public bool IsTransactionPinEnabled { get; set; } = false;
    
}

/// <summary>
/// User entity - includes Super Admin and Clients (PDF Section 2.1, 2.3)
/// Super Admin: CompanyId = null, Role = SuperAdmin
/// Client: CompanyId = company they belong to, Role = Client
/// </summary>
public class User : BaseEntity
{
    public string Code { get; set; } = string.Empty; // CL-2025-001 for clients
    public Guid? CompanyId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string WhatsAppNumber { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? IdPassport { get; set; }
    public UserRole Role { get; set; }
    public ClientType? ClientType { get; set; } // Only for Role = Client
    public string? PasswordHash { get; set; } // Required for permanent clients and SuperAdmin
    public bool IsActive { get; set; } = true;
    
    // Client balances (PDF Section 3.1)
    public decimal BalanceKES { get; set; } = 0;
    public decimal BalanceUSD { get; set; } = 0;
    public decimal OpeningBalanceKES { get; set; } = 0;
    public decimal OpeningBalanceUSD { get; set; } = 0;
    
    public int FailedLoginAttempts { get; set; } = 0;
    public DateTime? LockedUntil { get; set; }
    public DateTime? LastLoginAt { get; set; }
    
    [Timestamp]
    public byte[]? RowVersion { get; set; } // H3: Concurrency control for balance updates
}

/// <summary>
/// Bank Account entity (PDF Section 5.4)
/// </summary>
public class BankAccount : BaseEntity
{
    public string Code { get; set; } = string.Empty; // BA-2025-001
    public Guid CompanyId { get; set; }
    public string BankName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string? BranchCode { get; set; }
    public Currency Currency { get; set; }
    public decimal Balance { get; set; } = 0;
    public decimal OpeningBalance { get; set; } = 0;
    public bool IsActive { get; set; } = true;
    [Timestamp] public byte[]? RowVersion { get; set; }
}

/// <summary>
/// M-Pesa Agent entity (PDF Section 5.5)
/// </summary>
public class MpesaAgent : BaseEntity
{
    public string Code { get; set; } = string.Empty; // MP-2025-001
    public Guid CompanyId { get; set; }
    public string AgentName { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string AgentNumber { get; set; } = string.Empty;
    public string? StoreNumber { get; set; }
    public MpesaAgentType AgentType { get; set; }
    public decimal Balance { get; set; } = 0;
    public decimal OpeningBalance { get; set; } = 0;
    public bool IsActive { get; set; } = true;
    [Timestamp] public byte[]? RowVersion { get; set; }
}

/// <summary>
/// Cash Account entity (PDF Section 5.3)
/// Each company has one KES and one USD cash account
/// </summary>
public class CashAccount : BaseEntity
{
    public Guid CompanyId { get; set; }
    public Currency Currency { get; set; }
    public decimal Balance { get; set; } = 0;
    public decimal OpeningBalance { get; set; } = 0;
    public bool IsActive { get; set; } = true;
    public string Code { get; set; } = string.Empty;
    [Timestamp] public byte[]? RowVersion { get; set; }
}


public class Transaction : BaseEntity
{
    public string Code { get; set; } = string.Empty; // TXN-2025-001
    public Guid CompanyId { get; set; }
    public string Reference { get; set; } = string.Empty;
    public DateTime TransactionDate { get; set; }
    public TransactionType TransactionType { get; set; }
    
    // Primary account amount (Source) - the account being debited/credited
    public decimal Amount { get; set; }
    public Currency Currency { get; set; }
    
    // Counter account amount (Dest) - for forex transactions
    // If null, same as Amount (no forex involved)
    public decimal? CounterAmount { get; set; }
    public Currency? CounterCurrency { get; set; }
    
    public string Description { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public decimal? ExchangeRate { get; set; }
    
    // Source account (Primary - the one being debited/credited)
    public AccountType SourceAccountType { get; set; }
    public Guid SourceAccountId { get; set; }
    public decimal SourceBalanceBefore { get; set; }
    public decimal SourceBalanceAfter { get; set; }
    
    // Destination account (Counter - cash, bank, mpesa, transfer)
    public AccountType DestAccountType { get; set; }
    public Guid DestAccountId { get; set; }
    public decimal DestBalanceBefore { get; set; }
    public decimal DestBalanceAfter { get; set; }
    
    // ========== RECONCILIATION FIELDS ==========
    /// <summary>
    /// Default: Pending for Cash, Bank, M-Pesa transactions
    /// Client-only transactions are auto-Matched
    /// </summary>
    public ReconciliationStatus ReconciliationStatus { get; set; } = ReconciliationStatus.Pending;
    
    /// <summary>Actual amount entered during reconciliation</summary>
    public decimal? ActualAmount { get; set; }
    
    /// <summary>Variance = ActualAmount - Amount</summary>
    public decimal? Variance { get; set; }
    
    /// <summary>When reconciliation was completed</summary>
    public DateTime? ReconciledAt { get; set; }
    
    /// <summary>Who completed the reconciliation</summary>
    public Guid? ReconciledByUserId { get; set; }
    
    /// <summary>Notes added during reconciliation</summary>
    public string? ReconciliationNotes { get; set; }
    // ================================================
    
    public Guid CreatedByUserId { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeleteReason { get; set; }
}
/// <summary>
/// Expense Category (PDF Section 5.6)
/// </summary>
public class ExpenseCategory : BaseEntity
{
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Expense entity (PDF Section 5.6)
/// Recording expense automatically REDUCES payment account balance
/// </summary>
public class Expense : BaseEntity
{
    public string Code { get; set; } = string.Empty; // EXP-2025-001
    public Guid CompanyId { get; set; }
    public Guid CategoryId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? VendorPayee { get; set; }
    public decimal Amount { get; set; }
    public Currency Currency { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public Guid? TransactionId { get; set; }
    public AccountType PaymentAccountType { get; set; }
    public Guid PaymentAccountId { get; set; }
    public string? Reference { get; set; }
    public DateTime ExpenseDate { get; set; }
    public Guid CreatedByUserId { get; set; }
}
/// <summary>
/// Exchange Rate (PDF Section 5.7)
/// </summary>
public class ExchangeRate : BaseEntity
{
    public Guid CompanyId { get; set; }
    public decimal BuyRate { get; set; }  // KES per USD when buying USD
    public decimal SellRate { get; set; } // KES per USD when selling USD
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid CreatedByUserId { get; set; }
}
/// <summary>
/// Exchange Rate (PDF Section 5.7)
/// </summary>
// =====================================================
// EXCHANGE FLOAT ENTITIES (Forex Bureau Operations)
// =====================================================

/// <summary>
/// Exchange Float - Dedicated working capital for forex operations
/// Separate from main Cash accounts to track forex-specific inventory
/// </summary>
public class ExchangeFloat : BaseEntity
{
    public Guid CompanyId { get; set; }
    
    // Float Balances (Working Capital)
    public decimal KesBalance { get; set; } = 0;
    public decimal UsdBalance { get; set; } = 0;
    
    // Accumulated Profits (Unsettled)
    public decimal KesProfit { get; set; } = 0;
    public decimal UsdProfit { get; set; } = 0;
    
    // USD Position Tracking (Average Cost Method)
    public decimal UsdTotalCost { get; set; } = 0;  // Total KES spent to acquire USD
    public decimal UsdAverageCost { get; set; } = 0; // Average cost per USD
    
    // Daily Tracking
    public DateTime? LastOpeningDate { get; set; }
    public decimal? LastOpeningKes { get; set; }
    public decimal? LastOpeningUsd { get; set; }
    
    // Thresholds for alerts
    public decimal LowKesThreshold { get; set; } = 50000;
    public decimal LowUsdThreshold { get; set; } = 500;
    public decimal LargeTransactionThreshold { get; set; } = 500000; // KES equivalent
    [Timestamp] public byte[]? RowVersion { get; set; }
}

/// <summary>
/// Float Movement - Track all changes to the float
/// </summary>
public class FloatMovement : BaseEntity
{
    public Guid CompanyId { get; set; }
    public Guid? ExchangeFloatId { get; set; }
    public DateTime MovementDate { get; set; }
    public FloatMovementType MovementType { get; set; }
    public Currency Currency { get; set; }
    public decimal Amount { get; set; }
    public decimal BalanceBefore { get; set; }
    public decimal BalanceAfter { get; set; }
    public string? Reference { get; set; }
    public AccountType? RelatedAccountType { get; set; }
    public Guid? RelatedAccountId { get; set; }
    public string? Notes { get; set; }
    public Guid CreatedByUserId { get; set; }
}

/// <summary>
/// Exchange Transaction - Detailed record of each currency exchange
/// </summary>
public class ExchangeTransaction : BaseEntity
{
    public string Code { get; set; } = string.Empty;  // EXC-2025-001
    public Guid CompanyId { get; set; }
    public DateTime TransactionDate { get; set; }
    
    // Client — nullable for walk-in cash clients
    public Guid? ClientId { get; set; }
    public string? ClientName { get; set; }        // Walk-in client name (when no account)
    public string? ClientIdNumber { get; set; }  // For compliance
    
    // Exchange Details
    public ExchangeType ExchangeType { get; set; }
    public ExchangeDirection Direction { get; set; }
    
    // Amounts
    public decimal AmountGiven { get; set; }
    public Currency CurrencyGiven { get; set; }
    public decimal AmountReceived { get; set; }
    public Currency CurrencyReceived { get; set; }
    
    // Rate & Profit
    public decimal ExchangeRate { get; set; }
    public decimal Profit { get; set; }
    public Currency ProfitCurrency { get; set; }
    
    // Status
    public string Status { get; set; } = "Completed";
    public string? VoidReason { get; set; }
    public string? Notes { get; set; }
    
    // Tracking
    public Guid CreatedByUserId { get; set; }
    
    // If from client account - linked transactions
    public Guid? ClientSourceTransactionId { get; set; }
    public Guid? ClientDestTransactionId { get; set; }
}

/// <summary>
/// Daily Exchange Summary - End of day snapshot
/// </summary>
public class DailyExchangeSummary : BaseEntity
{
    public Guid CompanyId { get; set; }
    public DateTime Date { get; set; }
    
    // Opening Balances
    public decimal OpeningKes { get; set; }
    public decimal OpeningUsd { get; set; }
    
    // Closing Balances
    public decimal ClosingKes { get; set; }
    public decimal ClosingUsd { get; set; }
    
    // Volume
    public int ExchangeCount { get; set; }
    public decimal KesVolumeIn { get; set; }
    public decimal KesVolumeOut { get; set; }
    public decimal UsdVolumeIn { get; set; }
    public decimal UsdVolumeOut { get; set; }
    
    // Profit
    public decimal KesProfit { get; set; }
    public decimal UsdProfit { get; set; }
    
    // Verification
    public decimal? ActualKesCount { get; set; }  // Physical count
    public decimal? ActualUsdCount { get; set; }
    public decimal? KesVariance { get; set; }
    public decimal? UsdVariance { get; set; }
    public string? Notes { get; set; }
    
    // Status
    public bool IsClosed { get; set; } = false;
    public Guid? ClosedByUserId { get; set; }
    public DateTime? ClosedAt { get; set; }
}

/// <summary>
/// Invoice entity - Templates only, no backend effect (PDF Section 5.9)
/// </summary>
public class Invoice : BaseEntity
{
    public Guid CompanyId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public Guid? ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string? ClientEmail { get; set; }
    public string? ClientPhone { get; set; }
    public string? ClientAddress { get; set; }
    public DateTime InvoiceDate { get; set; }
    public DateTime DueDate { get; set; }
    public Currency Currency { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public decimal Subtotal { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public string? Terms { get; set; }
}

/// <summary>
/// Invoice Line Item
/// </summary>
public class InvoiceItem : BaseEntity
{
    public Guid InvoiceId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// Reconciliation entity (PDF Section 5.8)
/// </summary>
public class Reconciliation : BaseEntity
{
    public Guid CompanyId { get; set; }
    public AccountType AccountType { get; set; }
    public Guid AccountId { get; set; }
    public Currency Currency { get; set; }
    public decimal ExpectedBalance { get; set; }
    public decimal ActualBalance { get; set; }
    public decimal Variance { get; set; }
    public ReconciliationStatus Status { get; set; } = ReconciliationStatus.Pending;
    public string? Notes { get; set; }
    public DateTime? ReconciledAt { get; set; }
    public Guid? ReconciledByUserId { get; set; }
}

/// <summary>
/// OTP Code for WhatsApp authentication
/// </summary>
public class OtpCode : BaseEntity
{
    public string PhoneNumber { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? UserCode { get; set; } // SA-2026-001 or CO-2026-001
    public UserRole Role { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsUsed { get; set; } = false;
    public int AttemptCount { get; set; } = 0;
}

/// <summary>
/// User Session for refresh tokens
/// </summary>
public class UserSession : BaseEntity
{
    public Guid? CompanyId { get; set; }
    public Guid? UserId { get; set; }
    public UserRole UserRole { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string RefreshToken { get; set; } = string.Empty; // M3: NOT persisted, only used to return to client
    public string RefreshTokenHash { get; set; } = string.Empty; // Lookup by hash only
    public DateTime ExpiresAt { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string? UserAgent { get; set; }
    public string? DeviceInfo { get; set; }
    public bool IsRevoked { get; set; } = false;
}

/// <summary>
/// Device Token for push notifications (C3 FIX)
/// </summary>
public class DeviceToken : BaseEntity
{
    public Guid? UserId { get; set; }
    public Guid? CompanyId { get; set; }
    public string Token { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty; // "ios", "android", "web"
    public string? DeviceName { get; set; }
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Login History (PDF Section 6.2)
/// </summary>
public class LoginHistory : BaseEntity
{
    public Guid? CompanyId { get; set; }
    public Guid? UserId { get; set; }
    public UserRole UserRole { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string? UserAgent { get; set; }
    public string? Location { get; set; }
    public bool IsSuccessful { get; set; }
    public string? FailureReason { get; set; }
    public DateTime LoginAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Audit Log (PDF Section 9.1)
/// </summary>
public class AuditLog : BaseEntity
{
    public Guid? CompanyId { get; set; }
    public Guid? UserId { get; set; }
    public UserRole? UserRole { get; set; }
    public AuditAction Action { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}

/// <summary>
/// Notification Log for WhatsApp messages
/// </summary>
public class NotificationLog : BaseEntity
{
    public Guid? CompanyId { get; set; }
    public Guid? UserId { get; set; }
    public NotificationType NotificationType { get; set; }
    public string RecipientPhone { get; set; } = string.Empty;
    public string MessageContent { get; set; } = string.Empty;
    public bool IsSent { get; set; } = false;
    public DateTime? SentAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ProviderMessageId { get; set; }
}

/// <summary>
/// System Log for errors and monitoring (PDF Section 6.2)
/// </summary>
public class SystemLog : BaseEntity
{
    public string Level { get; set; } = string.Empty; // Error, Warning, Info
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? UserId { get; set; }
    public string? IpAddress { get; set; }
    public string? RequestPath { get; set; }
    public string? RequestMethod { get; set; }
}

/// <summary>
/// Super Admin IP Whitelist
/// </summary>
public class AdminIpWhitelist : BaseEntity
{
    public string IpAddress { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid AddedByUserId { get; set; }
}
/// <summary>
/// Client Alert/Notification entity for Client Portal
/// </summary>
public class ClientAlert : BaseEntity
{
    public Guid ClientId { get; set; }  // References User.Id where Role = Client
    public Guid CompanyId { get; set; }
    public string Type { get; set; } = "info";  // success, info, warning, error
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsRead { get; set; } = false;
    public DateTime? ReadAt { get; set; }
    public Guid? RelatedTransactionId { get; set; }
}
/// <summary>
/// Security Alert entity for tracking security events (SuperAdmin monitoring)
/// </summary>
public class SecurityAlert : BaseEntity
{
    public SecurityAlertType AlertType { get; set; }
    public string Severity { get; set; } = "Info"; // Info, Warning, Critical
    public string Message { get; set; } = string.Empty;
    public Guid? CompanyId { get; set; }
    public Guid? UserId { get; set; }
    public string? IpAddress { get; set; }
    public string? Details { get; set; }
    public bool IsResolved { get; set; } = false;
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public string? ResolutionNotes { get; set; }
}

/// <summary>
/// Subscription Payment entity for tracking company payments to platform
/// </summary>
public class SubscriptionPayment : BaseEntity
{
    public Guid CompanyId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "KES";
    public string PaymentMethod { get; set; } = string.Empty; // M-Pesa, Bank Transfer, etc.
    public string Status { get; set; } = "Pending"; // Pending, Completed, Failed, Refunded
    public string? Reference { get; set; }
    public string? MpesaReceiptNumber { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Blocked IP entity for security
/// </summary>
public class BlockedIP : BaseEntity
{
    public string IpAddress { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime? BlockedUntil { get; set; } // null = permanent
    public Guid BlockedByUserId { get; set; }
    public bool IsActive { get; set; } = true;
}
// ==================== TRUSTED DEVICE (Smart OTP) ====================
/// <summary>
/// Stores trusted devices that can skip OTP verification
/// </summary>
public class TrustedDevice : BaseEntity
{
    public Guid UserId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string Platform { get; set; } = "unknown";
    public string? DeviceName { get; set; }
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    public DateTime TrustedUntil { get; set; } = DateTime.UtcNow.AddDays(90);
}

// ==================== BALANCE ALERT RULES ====================
/// <summary>
/// Rules that trigger push notifications when client balances cross thresholds
/// </summary>
public class BalanceAlertRule : BaseEntity
{
    public Guid CompanyId { get; set; }
    public Guid? ClientId { get; set; }        // null = applies to ALL clients
    public string? ClientName { get; set; }     // Cached for display
    public Currency Currency { get; set; }
    public BalanceAlertDirection Direction { get; set; }
    public decimal Threshold { get; set; }
    public bool IsActive { get; set; } = true;
    public bool NotifyAllOfficeUsers { get; set; } = true;
    public DateTime? LastTriggeredAt { get; set; }
    public Guid CreatedByUserId { get; set; }
}

public enum BalanceAlertDirection
{
    Below = 0,
    Above = 1
}