using System.Text.RegularExpressions;

namespace SARIFF.Infrastructure.Services;

/// <summary>
/// Centralized validation for all input fields.
/// Used by services before processing any create/update request.
/// </summary>
public static partial class ValidationHelper
{
    // Phone: must be +{country}{number}, 10-15 digits total after +
    // Examples: +254712345678, +1234567890, +447911123456
    private static readonly Regex PhoneRegex = new(@"^\+[1-9]\d{9,14}$", RegexOptions.Compiled);
    
    // Email: basic format check
    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    // Names: at least 2 chars, no special chars except spaces, hyphens, apostrophes, periods
    private static readonly Regex NameRegex = new(@"^[\p{L}\p{M}\s'\-\.]{2,255}$", RegexOptions.Compiled);
    
    // Account numbers: alphanumeric, 4-30 chars
    private static readonly Regex AccountNumberRegex = new(@"^[a-zA-Z0-9\-]{4,30}$", RegexOptions.Compiled);

    /// <summary>
    /// Validate phone number format: +254712345678
    /// </summary>
    public static (bool IsValid, string? Error) ValidatePhone(string? phone, string fieldName = "Phone number")
    {
        if (string.IsNullOrWhiteSpace(phone))
            return (false, $"{fieldName} is required");
        
        var cleaned = phone.Trim();
        
        if (!cleaned.StartsWith("+"))
            return (false, $"{fieldName} must start with + and country code (e.g. +254712345678)");
        
        if (!PhoneRegex.IsMatch(cleaned))
            return (false, $"{fieldName} format is invalid. Use international format: +254712345678");
        
        return (true, null);
    }

    /// <summary>
    /// Validate email format (optional field — only validates if provided)
    /// </summary>
    public static (bool IsValid, string? Error) ValidateEmail(string? email, string fieldName = "Email")
    {
        if (string.IsNullOrWhiteSpace(email))
            return (true, null); // Optional
        
        if (email.Length > 255)
            return (false, $"{fieldName} is too long (max 255 characters)");
        
        if (!EmailRegex.IsMatch(email.Trim()))
            return (false, $"{fieldName} format is invalid");
        
        return (true, null);
    }

    /// <summary>
    /// Validate a required name field (person name, company name, etc.)
    /// </summary>
    public static (bool IsValid, string? Error) ValidateName(string? name, string fieldName = "Name")
    {
        if (string.IsNullOrWhiteSpace(name))
            return (false, $"{fieldName} is required");
        
        var trimmed = name.Trim();
        
        if (trimmed.Length < 2)
            return (false, $"{fieldName} must be at least 2 characters");
        
        if (trimmed.Length > 255)
            return (false, $"{fieldName} is too long (max 255 characters)");
        
        if (!NameRegex.IsMatch(trimmed))
            return (false, $"{fieldName} contains invalid characters");
        
        return (true, null);
    }

    /// <summary>
    /// Validate password strength
    /// </summary>
    public static (bool IsValid, string? Error) ValidatePassword(string? password, string fieldName = "Password")
    {
        if (string.IsNullOrEmpty(password))
            return (false, $"{fieldName} is required");
        
        if (password.Length < 6)
            return (false, $"{fieldName} must be at least 6 characters");
        
        if (password.Length > 128)
            return (false, $"{fieldName} is too long (max 128 characters)");
        
        return (true, null);
    }

    /// <summary>
    /// Validate a monetary amount (must be positive)
    /// </summary>
    public static (bool IsValid, string? Error) ValidateAmount(decimal amount, string fieldName = "Amount", bool allowZero = false)
    {
        if (allowZero ? amount < 0 : amount <= 0)
            return (false, $"{fieldName} must be {(allowZero ? "zero or positive" : "greater than zero")}");
        
        if (amount > 999_999_999.99m)
            return (false, $"{fieldName} exceeds maximum allowed value");
        
        return (true, null);
    }

    /// <summary>
    /// Validate exchange rate
    /// </summary>
    public static (bool IsValid, string? Error) ValidateRate(decimal rate, string fieldName = "Rate")
    {
        if (rate <= 0)
            return (false, $"{fieldName} must be greater than zero");
        
        if (rate > 999_999.999999m)
            return (false, $"{fieldName} exceeds maximum allowed value");
        
        return (true, null);
    }

    /// <summary>
    /// Validate an optional text field (max length, no script injection)
    /// </summary>
    public static (bool IsValid, string? Error) ValidateText(string? text, string fieldName, int maxLength = 500, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return required ? (false, $"{fieldName} is required") : (true, null);
        
        if (text.Length > maxLength)
            return (false, $"{fieldName} is too long (max {maxLength} characters)");
        
        // Block obvious script injection
        if (text.Contains("<script", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
            return (false, $"{fieldName} contains invalid content");
        
        return (true, null);
    }

    /// <summary>
    /// Validate bank account number
    /// </summary>
    public static (bool IsValid, string? Error) ValidateAccountNumber(string? number, string fieldName = "Account number")
    {
        if (string.IsNullOrWhiteSpace(number))
            return (false, $"{fieldName} is required");
        
        if (!AccountNumberRegex.IsMatch(number.Trim()))
            return (false, $"{fieldName} must be 4-30 alphanumeric characters");
        
        return (true, null);
    }

    /// <summary>
    /// Run multiple validations and return the first error, or null if all pass
    /// </summary>
    public static string? FirstError(params (bool IsValid, string? Error)[] validations)
    {
        foreach (var (isValid, error) in validations)
        {
            if (!isValid) return error;
        }
        return null;
    }
}