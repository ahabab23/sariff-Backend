
namespace SARIFF.Core.Enums;

/// <summary>

/// Super Admin = Platform owner
/// Office User = Company/Business subscriber
/// Client = End customer
/// </summary>
public enum UserRole
{
    SuperAdmin = 0,
    OfficeUser = 1,
    Client = 2
}

/// <summary>

/// Permanent = Can login, receives messages
/// Temporary = Cannot login, no messages (accounting only)
/// </summary>
public enum ClientType
{
    Permanent = 0,
    Temporary = 1
}

/// <summary>
/// Currency types - KES and USD as per PDF
/// </summary>
public enum Currency
{
    KES = 0,
    USD = 1
}

/// <summary>
/// Transaction type for double-entry accounting
/// Debit = Money IN (increases asset accounts)
/// Credit = Money OUT (decreases asset accounts)
/// </summary>
public enum TransactionType
{
    Debit = 0,
    Credit = 1
}

/// <summary>
/// Account types for transfer matrix (PDF Section 4)
/// </summary>
public enum AccountType
{
    Cash = 0,
    Bank = 1,
    Mpesa = 2,
    Client = 3,
    Expense = 4 
}

/// <summary>

/// </summary>
public enum PaymentMethod
{
    Cash = 0,
    Bank = 1,
    Mpesa = 2,
    AccountTransfer = 3
}

/// <summary>

/// </summary>
public enum MpesaAgentType
{
    Standard = 0,
    Super = 1
}

/// <summary>

/// </summary>
public enum InvoiceStatus
{
    Draft = 0,
    Sent = 1,
    Paid = 2,
    Cancelled = 3
}

/// <summary>

/// </summary>
public enum ReconciliationStatus
{
    Pending = 0,
    Matched = 1,
    Unmatched = 2
}

/// <summary>
/// Notification type for WhatsApp messages
/// </summary>
public enum NotificationType
{
    Transaction = 0,
    Login = 1,
    SystemError = 2,
    DailySummary = 3,
    LowBalance = 4,
    NewClient = 5,
    ReconciliationVariance = 6
}

/// <summary>

/// </summary>
public enum AuditAction
{
    Create = 0,
    Update = 1,
    Delete = 2,
    Login = 3,
    Logout = 4
}

/// <summary>
/// Subscription plan tiers for companies
/// </summary>
public enum SubscriptionPlan
{
    Free = 0,
    Starter = 1,
    Professional = 2,
    Enterprise = 3
}

/// <summary>
/// Subscription status for companies
/// </summary>
public enum SubscriptionStatus
{
    Active = 0,
    Trial = 1,
    Expired = 2,
    Cancelled = 3,
    Suspended = 4
}

/// <summary>
/// Security alert types
/// </summary>
public enum SecurityAlertType
{
    FailedLogin = 0,
    SuspiciousActivity = 1,
    UnauthorizedAccess = 2,
    IPBlocked = 3,
    AccountLocked = 4,
    PasswordReset = 5,
    NewDevice = 6,
    RateLimitExceeded = 7
}
/// <summary>
/// Exchange type - Cash or from client account
/// </summary>
public enum ExchangeType
{
    Cash = 0,        // Physical cash exchange
    FromAccount = 1  // From client's account balance
}

/// <summary>
/// Exchange direction - Which currency client is giving
/// </summary>
public enum ExchangeDirection
{
    UsdToKes = 0,  // Client gives USD, receives KES
    KesToUsd = 1   // Client gives KES, receives USD
}

/// <summary>
/// Float movement types for tracking
/// </summary>
public enum FloatMovementType
{
    Fund = 0,
    Withdraw = 1,
    ExchangeIn = 2,
    ExchangeOut = 3,
    ProfitSettlement = 4,
    Adjustment = 5
}