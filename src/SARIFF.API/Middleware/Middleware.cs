using System.Net;
using System.Security.Claims;
using System.Text.Json;
using SARIFF.Core.DTOs;
using SARIFF.Core.Interfaces;

namespace SARIFF.API.Middleware;

public class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger, IServiceScopeFactory scopeFactory)
    {
        _next = next;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception: {Message}", ex.Message);
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var logService = scope.ServiceProvider.GetRequiredService<ISystemLogService>();
            
            Guid? companyId = null;
            Guid? userId = null;
            
            if (context.User?.Identity?.IsAuthenticated == true)
            {
                var companyIdClaim = context.User.FindFirst("company_id")?.Value;
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                
                if (Guid.TryParse(companyIdClaim, out var cId)) companyId = cId;
                if (Guid.TryParse(userIdClaim, out var uId)) userId = uId;
            }

            var ipAddress = context.Connection.RemoteIpAddress?.ToString();
            var requestPath = context.Request.Path.ToString();
            var method = context.Request.Method;

            await logService.LogApiErrorAsync(
                requestPath,
                method,
                exception.Message,
                exception.StackTrace,
                ipAddress,
                companyId
            );
        }
        catch (Exception logEx)
        {
            _logger.LogError(logEx, "Failed to log exception to database");
        }

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = exception switch
        {
            UnauthorizedAccessException => (int)HttpStatusCode.Unauthorized,
            KeyNotFoundException => (int)HttpStatusCode.NotFound,
            ArgumentException => (int)HttpStatusCode.BadRequest,
            InvalidOperationException => (int)HttpStatusCode.BadRequest,
            _ => (int)HttpStatusCode.InternalServerError
        };

        var response = new ApiResponse<object>(false, GetUserFriendlyMessage(exception), null!);
        
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await context.Response.WriteAsync(JsonSerializer.Serialize(response, options));
    }

    private static string GetUserFriendlyMessage(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "You are not authorized to perform this action",
        KeyNotFoundException => "The requested resource was not found",
        ArgumentException ae when ae.Message.Length < 100 && !ae.Message.Contains("Entity") 
            => ae.Message, // Short, non-internal argument errors are OK to show
        ArgumentException => "Invalid input provided",
        InvalidOperationException ioe when ioe.Message.Length < 100 && !ioe.Message.Contains("Entity")
            => ioe.Message,
        InvalidOperationException => "Operation could not be completed",
        _ => "An unexpected error occurred. Please try again later."
    };
}

/// <summary>
/// PRODUCTION-READY Tenant Middleware
/// 
/// SECURITY FIXES APPLIED:
/// 1. REMOVED X-Company-Id header bypass - was allowing anyone to access any company's data
/// 2. REMOVED X-User-Id header bypass - same vulnerability
/// 3. Now ONLY extracts tenant info from authenticated JWT claims
/// </summary>
public class TenantMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantMiddleware> _logger;

    public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // SECURITY FIX: ONLY extract tenant info from authenticated JWT claims
        // NO header-based bypasses allowed!
        
        if (context.User.Identity?.IsAuthenticated == true)
        {
            // Extract CompanyId from JWT
            var companyIdClaim = context.User.FindFirst("company_id")?.Value;
            if (!string.IsNullOrEmpty(companyIdClaim) && Guid.TryParse(companyIdClaim, out var companyId))
            {
                context.Items["CompanyId"] = companyId;
            }

            // Extract UserId from JWT (try multiple claim types)
            var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                           ?? context.User.FindFirst("user_id")?.Value
                           ?? context.User.FindFirst("sub")?.Value;
            if (!string.IsNullOrEmpty(userIdClaim) && Guid.TryParse(userIdClaim, out var userId))
            {
                context.Items["UserId"] = userId;
            }

            // Extract Role
            var roleClaim = context.User.FindFirst(ClaimTypes.Role)?.Value;
            if (!string.IsNullOrEmpty(roleClaim))
            {
                context.Items["UserRole"] = roleClaim;
                
                // For OfficeUser: If no UserId but has CompanyId, use CompanyId as UserId
                // This is because OfficeUser login is company-based
                if (roleClaim == "OfficeUser" && !context.Items.ContainsKey("UserId") && context.Items.ContainsKey("CompanyId"))
                {
                    context.Items["UserId"] = context.Items["CompanyId"];
                }
            }
        }

        // SECURITY FIX: REMOVED all header-based fallbacks
        // The following code was REMOVED because it allowed authorization bypass:
        //
        // if (!context.Items.ContainsKey("CompanyId"))
        // {
        //     var headerCompanyId = context.Request.Headers["X-Company-Id"].FirstOrDefault();
        //     if (!string.IsNullOrEmpty(headerCompanyId) && Guid.TryParse(headerCompanyId, out var companyId))
        //     {
        //         context.Items["CompanyId"] = companyId;  // VULNERABILITY!
        //     }
        // }

        await _next(context);
    }
}

/// <summary>
/// Rate Limiting Middleware for auth endpoints
/// Prevents brute force attacks
/// </summary>
public class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RateLimitingMiddleware> _logger;
    private static readonly Dictionary<string, (int Count, DateTime ResetTime)> _requestCounts = new();
    private static readonly object _lock = new();

    private const int MaxRequestsPerMinute = 10;
    private const int LockoutMinutes = 1;

    public RateLimitingMiddleware(RequestDelegate next, ILogger<RateLimitingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Apply rate limiting to auth endpoints
        if (context.Request.Path.StartsWithSegments("/api/auth"))
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var key = $"{ip}:{context.Request.Path}";

            lock (_lock)
            {
                // Clean up old entries periodically
                if (_requestCounts.Count > 10000)
                {
                    var oldEntries = _requestCounts
                        .Where(kvp => kvp.Value.ResetTime < DateTime.UtcNow)
                        .Select(kvp => kvp.Key)
                        .ToList();
                    foreach (var oldKey in oldEntries)
                    {
                        _requestCounts.Remove(oldKey);
                    }
                }

                if (_requestCounts.TryGetValue(key, out var entry))
                {
                    if (DateTime.UtcNow < entry.ResetTime)
                    {
                        if (entry.Count >= MaxRequestsPerMinute)
                        {
                            _logger.LogWarning("Rate limit exceeded for {IP} on {Path}", ip, context.Request.Path);
                            context.Response.StatusCode = 429; // Too Many Requests
                            context.Response.Headers["Retry-After"] = LockoutMinutes.ToString();
                            return;
                        }
                        _requestCounts[key] = (entry.Count + 1, entry.ResetTime);
                    }
                    else
                    {
                        _requestCounts[key] = (1, DateTime.UtcNow.AddMinutes(LockoutMinutes));
                    }
                }
                else
                {
                    _requestCounts[key] = (1, DateTime.UtcNow.AddMinutes(LockoutMinutes));
                }
            }
        }

        await _next(context);
    }
}

/// <summary>
/// Request Logging Middleware for audit trail
/// </summary>
public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var startTime = DateTime.UtcNow;
        
        await _next(context);
        
        var duration = DateTime.UtcNow - startTime;
        
        // Log slow requests
        if (duration.TotalMilliseconds > 1000)
        {
            _logger.LogWarning("Slow request: {Method} {Path} took {Duration}ms", 
                context.Request.Method, 
                context.Request.Path, 
                duration.TotalMilliseconds);
        }
    }
}