using Microsoft.EntityFrameworkCore;
using SARIFF.Infrastructure.Data;

namespace SARIFF.Infrastructure.Services;


public static class CodeGenerator
{
    private static readonly SemaphoreSlim _lock = new(1, 1);
    
    // FIX: Track the last generated sequence per prefix to prevent duplicates
    // when multiple codes are generated before SaveChanges
    private static readonly Dictionary<string, int> _lastGenerated = new();

    /// <summary>
    /// Auto-generate company code prefix from company name.
    /// Skips common words, takes first letter of remaining words.
    /// Examples: "Alpha Forex Bureau Ltd" → "AFB", "Sariff Exchange" → "SE"
    /// </summary>
    public static string GenerateCodePrefix(string companyName)
    {
        var skipWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "limited", "ltd", "inc", "incorporated", "llc", "plc", "corp", "corporation",
              "and", "the", "of", "for", "company", "co", "group" };
        
        var words = companyName.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !skipWords.Contains(w))
            .ToArray();

        string prefix;
        if (words.Length == 0)
            prefix = companyName.Length >= 2 ? companyName.Substring(0, 2) : companyName;
        else if (words.Length == 1)
            prefix = words[0].Length >= 3 ? words[0].Substring(0, 3) : words[0];
        else
            prefix = string.Concat(words.Select(w => w[0]));
        
        // Cap at 3 characters, uppercase
        prefix = prefix.Length > 3 ? prefix.Substring(0, 3) : prefix;
        prefix = prefix.ToUpper();
        
        // Ensure it doesn't conflict with reserved prefixes
        if (prefix == "SA" || prefix == "CL")
            prefix = prefix + "X";
        
        return prefix;
    }

    /// <summary>
    /// Get next sequence: max of DB value and in-memory last generated
    /// </summary>
    private static int GetNextSequence(string cacheKey, int dbMax)
    {
        int memMax = _lastGenerated.ContainsKey(cacheKey) ? _lastGenerated[cacheKey] : 0;
        int next = Math.Max(dbMax, memMax) + 1;
        _lastGenerated[cacheKey] = next;
        return next;
    }

    /// <summary>
    /// Generate transaction code: TXN-{YEAR}-{SEQUENCE}
    /// FIXED: Tracks in-memory max to prevent duplicates within same request
    /// </summary>
    public static async Task<string> GenerateTransactionCodeAsync(AppDbContext context, Guid companyId)
    {
        await _lock.WaitAsync();
        try
        {
            var year = DateTime.UtcNow.Year;
            var prefix = $"TXN-{year}-";
            var cacheKey = $"{companyId}_{prefix}";
            
            // Get the max sequence number from existing codes
            var maxCode = await context.Transactions.IgnoreQueryFilters()
                .Where(t => t.CompanyId == companyId && t.Code.StartsWith(prefix))
                .Select(t => t.Code)
                .OrderByDescending(c => c)
                .FirstOrDefaultAsync();

            int dbMax = 0;
            if (maxCode != null)
            {
                var sequencePart = maxCode.Substring(prefix.Length);
                if (int.TryParse(sequencePart, out int currentMax))
                    dbMax = currentMax;
            }

            int nextSequence = GetNextSequence(cacheKey, dbMax);
            return $"{prefix}{nextSequence:D6}";
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Generate client code: {PREFIX}-CL-{YEAR}-{SEQUENCE}
    /// Example: FB-CL-2026-0001 for Alpha Forex Bureau
    /// Falls back to CL-{YEAR}-{SEQ} if no prefix provided
    /// </summary>
    public static async Task<string> GenerateClientCodeAsync(AppDbContext context, Guid companyId, string? companyPrefix = null)
    {
        await _lock.WaitAsync();
        try
        {
            var year = DateTime.UtcNow.Year;
            var prefix = string.IsNullOrEmpty(companyPrefix) 
                ? $"CL-{year}-" 
                : $"{companyPrefix}-CL-{year}-";
            var cacheKey = $"{companyId}_{prefix}";
            
            var maxCode = await context.Users.IgnoreQueryFilters()
                .Where(u => u.CompanyId == companyId && u.Code != null && u.Code.Contains($"-CL-{year}-"))
                .Select(u => u.Code)
                .OrderByDescending(c => c)
                .FirstOrDefaultAsync();

            int dbMax = 0;
            if (maxCode != null)
            {
                // Extract sequence from end of code: "FB-CL-2026-0001" → "0001"
                var lastDash = maxCode.LastIndexOf('-');
                if (lastDash >= 0)
                {
                    var sequencePart = maxCode.Substring(lastDash + 1);
                    if (int.TryParse(sequencePart, out int currentMax))
                        dbMax = currentMax;
                }
            }

            int nextSequence = GetNextSequence(cacheKey, dbMax);
            return $"{prefix}{nextSequence:D4}";
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Generate company code: {PREFIX}-{YEAR}-{SEQUENCE}
    /// Sequence is GLOBAL across all companies (not per-prefix)
    /// Example: AFB-2026-0001, ASW-2026-0002, KMT-2026-0003
    /// </summary>
    public static async Task<string> GenerateCompanyCodeAsync(AppDbContext context, string? companyPrefix = null)
    {
        await _lock.WaitAsync();
        try
        {
            var year = DateTime.UtcNow.Year;
            var codePrefix = string.IsNullOrEmpty(companyPrefix) ? "CO" : companyPrefix;
            var yearPart = $"-{year}-";
            var cacheKey = $"company_global_{year}";
            
            // Search ALL companies for this year — global sequence
            var allCodes = await context.Companies.IgnoreQueryFilters()
                .Where(c => c.Code.Contains(yearPart))
                .Select(c => c.Code)
                .ToListAsync();

            int dbMax = 0;
            foreach (var code in allCodes)
            {
                var lastDash = code.LastIndexOf('-');
                if (lastDash >= 0)
                {
                    var sequencePart = code.Substring(lastDash + 1);
                    if (int.TryParse(sequencePart, out int seq) && seq > dbMax)
                        dbMax = seq;
                }
            }

            int nextSequence = GetNextSequence(cacheKey, dbMax);
            return $"{codePrefix}-{year}-{nextSequence:D4}";
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Generate bank account code: BA-{YEAR}-{SEQUENCE}
    /// </summary>
    public static async Task<string> GenerateBankAccountCodeAsync(AppDbContext context, Guid companyId)
    {
        await _lock.WaitAsync();
        try
        {
            var year = DateTime.UtcNow.Year;
            var prefix = $"BA-{year}-";
            var cacheKey = $"{companyId}_{prefix}";
            
            var maxCode = await context.BankAccounts.IgnoreQueryFilters()
                .Where(b => b.CompanyId == companyId && b.Code.StartsWith(prefix))
                .Select(b => b.Code)
                .OrderByDescending(c => c)
                .FirstOrDefaultAsync();

            int dbMax = 0;
            if (maxCode != null)
            {
                var sequencePart = maxCode.Substring(prefix.Length);
                if (int.TryParse(sequencePart, out int currentMax))
                    dbMax = currentMax;
            }

            int nextSequence = GetNextSequence(cacheKey, dbMax);
            return $"{prefix}{nextSequence:D3}";
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Generate M-Pesa agent code: MP-{YEAR}-{SEQUENCE}
    /// </summary>
    public static async Task<string> GenerateMpesaAgentCodeAsync(AppDbContext context, Guid companyId)
    {
        await _lock.WaitAsync();
        try
        {
            var year = DateTime.UtcNow.Year;
            var prefix = $"MP-{year}-";
            var cacheKey = $"{companyId}_{prefix}";
            
            var maxCode = await context.MpesaAgents.IgnoreQueryFilters()
                .Where(m => m.CompanyId == companyId && m.Code.StartsWith(prefix))
                .Select(m => m.Code)
                .OrderByDescending(c => c)
                .FirstOrDefaultAsync();

            int dbMax = 0;
            if (maxCode != null)
            {
                var sequencePart = maxCode.Substring(prefix.Length);
                if (int.TryParse(sequencePart, out int currentMax))
                    dbMax = currentMax;
            }

            int nextSequence = GetNextSequence(cacheKey, dbMax);
            return $"{prefix}{nextSequence:D3}";
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Generate expense code: EXP-{YEAR}-{SEQUENCE}
    /// </summary>
    public static async Task<string> GenerateExpenseCodeAsync(AppDbContext context, Guid companyId)
    {
        await _lock.WaitAsync();
        try
        {
            var year = DateTime.UtcNow.Year;
            var prefix = $"EXP-{year}-";
            var cacheKey = $"{companyId}_{prefix}";
            
            var maxCode = await context.Expenses.IgnoreQueryFilters()
                .Where(e => e.CompanyId == companyId && e.Code.StartsWith(prefix))
                .Select(e => e.Code)
                .OrderByDescending(c => c)
                .FirstOrDefaultAsync();

            int dbMax = 0;
            if (maxCode != null)
            {
                var sequencePart = maxCode.Substring(prefix.Length);
                if (int.TryParse(sequencePart, out int currentMax))
                    dbMax = currentMax;
            }

            int nextSequence = GetNextSequence(cacheKey, dbMax);
            return $"{prefix}{nextSequence:D5}";
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Generate invoice number: INV-{YEAR}-{SEQUENCE}
    /// </summary>
    public static async Task<string> GenerateInvoiceNumberAsync(AppDbContext context, Guid companyId)
    {
        await _lock.WaitAsync();
        try
        {
            var year = DateTime.UtcNow.Year;
            var prefix = $"INV-{year}-";
            var cacheKey = $"{companyId}_{prefix}";
            
            var maxCode = await context.Invoices.IgnoreQueryFilters()
                .Where(i => i.CompanyId == companyId && i.InvoiceNumber.StartsWith(prefix))
                .Select(i => i.InvoiceNumber)
                .OrderByDescending(c => c)
                .FirstOrDefaultAsync();

            int dbMax = 0;
            if (maxCode != null)
            {
                var sequencePart = maxCode.Substring(prefix.Length);
                if (int.TryParse(sequencePart, out int currentMax))
                    dbMax = currentMax;
            }

            int nextSequence = GetNextSequence(cacheKey, dbMax);
            return $"{prefix}{nextSequence:D4}";
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Generate Super Admin code: SA-{YEAR}-{SEQUENCE}
    /// </summary>
    public static async Task<string> GenerateSuperAdminCodeAsync(AppDbContext context)
    {
        await _lock.WaitAsync();
        try
        {
            var year = DateTime.UtcNow.Year;
            var prefix = $"SA-{year}-";
            
            var maxCode = await context.Users.IgnoreQueryFilters()
                .Where(u => u.Role == Core.Enums.UserRole.SuperAdmin && u.Code != null && u.Code.StartsWith(prefix))
                .Select(u => u.Code)
                .OrderByDescending(c => c)
                .FirstOrDefaultAsync();

            int dbMax = 0;
            if (maxCode != null)
            {
                var sequencePart = maxCode.Substring(prefix.Length);
                if (int.TryParse(sequencePart, out int currentMax))
                    dbMax = currentMax;
            }

            int nextSequence = GetNextSequence(prefix, dbMax);
            return $"{prefix}{nextSequence:D3}";
        }
        finally
        {
            _lock.Release();
        }
    }
    
    /// <summary>
    /// Generate exchange code: EXC-{YEAR}-{SEQUENCE}
    /// FIXED: Uses MAX + 1 with semaphore lock (was COUNT + 1 without lock)
    /// </summary>
    public static async Task<string> GenerateExchangeCodeAsync(AppDbContext context, Guid companyId)
    {
        await _lock.WaitAsync();
        try
        {
            var year = DateTime.UtcNow.Year;
            var prefix = $"EXC-{year}-";
            var cacheKey = $"{companyId}_{prefix}";

            var maxCode = await context.ExchangeTransactions.IgnoreQueryFilters()
                .Where(e => e.CompanyId == companyId && e.Code.StartsWith(prefix))
                .Select(e => e.Code)
                .OrderByDescending(c => c)
                .FirstOrDefaultAsync();

            int dbMax = 0;
            if (maxCode != null)
            {
                var sequencePart = maxCode.Substring(prefix.Length);
                if (int.TryParse(sequencePart, out int currentMax))
                    dbMax = currentMax;
            }

            int nextSequence = GetNextSequence(cacheKey, dbMax);
            return $"{prefix}{nextSequence:D4}";
        }
        finally
        {
            _lock.Release();
        }
    }
}