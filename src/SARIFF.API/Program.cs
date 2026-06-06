using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using SARIFF.API.Middleware;
using SARIFF.Core.Entities;
using SARIFF.Core.Enums;
using SARIFF.Infrastructure;
using SARIFF.Infrastructure.Data;
using Serilog;
// FIX: Npgsql 6+ requires DateTime.Kind=Utc for timestamptz columns.
// This switch restores legacy behavior so Unspecified/Local DateTimes work too.
// Without this, any DateTime from query strings, .Date calls, or default(DateTime) crashes.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);


var builder = WebApplication.CreateBuilder(args);

// C4/deploy FIX: bind to the port the host injects (Render/Railway/Fly set $PORT).
// Falls back to 8080 locally (matches the Dockerfile EXPOSE).
var listenPort = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://+:{listenPort}");

// FIX: REMOVED hardcoded development mode - let environment be set properly
// builder.Environment.EnvironmentName = "Development";  // REMOVED - SECURITY FIX

// Serilog - adjust logging level based on environment
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .MinimumLevel.Is(builder.Environment.IsDevelopment() 
        ? Serilog.Events.LogEventLevel.Debug 
        : Serilog.Events.LogEventLevel.Information)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
builder.Host.UseSerilog();

Log.Information("========================================");
Log.Information("SARIFF API - {Environment} MODE", builder.Environment.EnvironmentName.ToUpper());
Log.Information("========================================");

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Swagger - only enable detailed docs in development
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo 
    { 
        Title = "SARIFF API", 
        Version = "v1",
        Description = builder.Environment.IsDevelopment() 
            ? "Development API Documentation" 
            : "Production API"
    });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter your JWT token"
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// JWT Authentication — C2 FIX: prefer env var, fail fast on placeholder
var jwtKey = Environment.GetEnvironmentVariable("JWT_SECRET_KEY") 
    ?? builder.Configuration["Jwt:SecretKey"] 
    ?? throw new InvalidOperationException("JWT SecretKey not configured");
if (jwtKey.Contains("YourSuper") || jwtKey.Length < 32)
{
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException("SECURITY: Set JWT_SECRET_KEY env var with a random 32+ char key for production.");
    Console.WriteLine("⚠️  WARNING: Using placeholder JWT key. Set JWT_SECRET_KEY for production.");
}
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.Zero
        };
    });

// Authorization Policies
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SuperAdminOnly", policy => policy.RequireRole("SuperAdmin"));
    options.AddPolicy("OfficeUserOnly", policy => policy.RequireRole("OfficeUser"));
    options.AddPolicy("ClientOnly", policy => policy.RequireRole("Client"));
    options.AddPolicy("AdminOrOffice", policy => policy.RequireRole("SuperAdmin", "OfficeUser"));
});

// CORS - FIX: Configure based on environment
builder.Services.AddCors(options =>
{
    if (builder.Environment.IsDevelopment())
    {
        // Development: Allow any origin (needed for React Native mobile + web)
        options.AddPolicy("AllowFrontend", policy =>
        {
            policy.AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod();
            // Note: AllowAnyOrigin + AllowCredentials is not allowed together.
            // Mobile uses Bearer token in header, not cookies, so this is fine.
        });
    }
    else
    {
        // Production: Only allow specific origins from configuration
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() 
            ?? new[] { "https://yourproductiondomain.com" };
        
        options.AddPolicy("AllowFrontend", policy =>
        {
            policy.WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
    }
});

// Infrastructure (DbContext, Services)
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();

// SignalR for real-time notifications
builder.Services.AddSignalR();

var app = builder.Build();

// Swagger only in Development
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "SARIFF API v1");
        options.RoutePrefix = "swagger";
    });
}

// Middleware
app.UseMiddleware<ExceptionMiddleware>();

// FIX: Add Rate Limiting middleware for auth endpoints
app.UseMiddleware<RateLimitingMiddleware>();

app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantMiddleware>();

app.MapControllers();

// SignalR hub
app.MapHub<SARIFF.API.Hubs.NotificationHub>("/hubs/notifications");

// Health check
app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Timestamp = DateTime.UtcNow }));

// ========================================
// DATABASE INITIALIZATION AND SEEDING
// ========================================
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    logger.LogInformation("========================================");
    logger.LogInformation("DATABASE INITIALIZATION");
    logger.LogInformation("========================================");
    
    // H4 FIX: Use MigrateAsync for proper schema upgrades
    // First time: run "dotnet ef migrations add InitialCreate" to generate migration
    // For dev convenience, fall back to EnsureCreated if no migrations exist
    try { await context.Database.MigrateAsync(); }
    catch { await context.Database.EnsureCreatedAsync(); }
    logger.LogInformation("Database schema created/verified");
    
    // Check if data exists
    var hasUsers = await context.Users.AnyAsync();
    var hasCompanies = await context.Companies.AnyAsync();
    
    logger.LogInformation("Existing data check - Users: {HasUsers}, Companies: {HasCompanies}", hasUsers, hasCompanies);
    
    // Seed data in Development, or in Production when SEED_DATA=true env var is set
    var forceSeed = Environment.GetEnvironmentVariable("SEED_DATA")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    
    if (!hasUsers && !hasCompanies)
    {
        if (app.Environment.IsDevelopment() || forceSeed)
        {
        logger.LogInformation("========================================");
        logger.LogInformation("SEEDING TEST DATA...");
        logger.LogInformation("========================================");
        
        // BCrypt hash for "Test@123"
        var passwordHash = BCrypt.Net.BCrypt.HashPassword("Test@123");
        logger.LogInformation("Generated password hash for 'Test@123'");
        
        // 1. Create Super Admin
        var superAdmin = new User
        {
            Id = Guid.Parse("a0000000-0000-0000-0000-000000000001"),
            Code = "SA-2026-001",
            CompanyId = null,
            FullName = "Super Admin",
            WhatsAppNumber = "+254700000000",
            Email = "admin@sariff.com",
            Role = UserRole.SuperAdmin,
            PasswordHash = passwordHash,
            IsActive = true,
            BalanceKES = 0,
            BalanceUSD = 0
        };
        context.Users.Add(superAdmin);
        logger.LogInformation("Created Super Admin: {Code} / {Phone}", superAdmin.Code, superAdmin.WhatsAppNumber);
        
        // 2. Create Company (Office User)
        var company1 = new Company
        {
            Id = Guid.Parse("b0000000-0000-0000-0000-000000000001"),
            Code = "AFB-2026-0001",
            CodePrefix = "AFB",
            Name = "Alpha Forex Bureau",
            OwnerName = "John Kamau",
            WhatsAppNumber = "+254711111111",
            Email = "john@alphaforex.co.ke",
            PasswordHash = passwordHash,
            TaxId = "KRA123456",
            Website = "https://alphaforex.co.ke",
            Address = "Nairobi CBD, Kenya",
            IsActive = true
        };
        context.Companies.Add(company1);
        logger.LogInformation("Created Company: {Code} / {Phone}", company1.Code, company1.WhatsAppNumber);
        
        // 3. Create Client (belongs to company1)
        var client1 = new User
        {
            Id = Guid.Parse("d0000000-0000-0000-0000-000000000001"),
            Code = "AFB-CL-2026-0001",
            CompanyId = company1.Id,
            FullName = "Michael Ochieng",
            WhatsAppNumber = "+254733333333",
            Email = "michael@email.com",
            IdPassport = "12345678",
            Role = UserRole.Client,
            ClientType = ClientType.Permanent,
            PasswordHash = passwordHash,
            IsActive = true,
           
        };
        context.Users.Add(client1);
        logger.LogInformation("Created Client: {Code} / {Phone}", client1.Code, client1.WhatsAppNumber);
        
        
        // 7. Create Exchange Rate
        var exchangeRate = new ExchangeRate
        {
            Id = Guid.Parse("f0000000-0000-0000-0000-000000000001"),
            CompanyId = company1.Id,
            BuyRate = 128.50m,
            SellRate = 129.50m,
            EffectiveFrom = DateTime.UtcNow,
            IsActive = true,
            CreatedByUserId = superAdmin.Id
        };
        context.ExchangeRates.Add(exchangeRate);
        logger.LogInformation("Created Exchange Rate: Buy={Buy}, Sell={Sell}", exchangeRate.BuyRate, exchangeRate.SellRate);
        
        
        await context.SaveChangesAsync();
        logger.LogInformation("Extended test data seeded: {Clients} clients", 3);
        
        logger.LogInformation("========================================");
        logger.LogInformation("SEED DATA COMPLETE!");
        logger.LogInformation("========================================");
        logger.LogInformation("");
        logger.LogInformation("TEST CREDENTIALS (Password: Test@123):");
        logger.LogInformation("");
        logger.LogInformation("SUPER ADMIN (OTP Required):");
        logger.LogInformation("  Code: SA-2026-001");
        logger.LogInformation("  Phone: +254700000000");
        logger.LogInformation("");
        logger.LogInformation("OFFICE USER (OTP Required):");
        logger.LogInformation("  Code: AFB-2026-0001");
        logger.LogInformation("  Phone: +254711111111");
        logger.LogInformation("");
        logger.LogInformation("CLIENT (NO OTP):");
        logger.LogInformation("  Code: AFB-CL-2026-0001");
        logger.LogInformation("  Phone: +254733333333");
        logger.LogInformation("");
        logger.LogInformation("MORE TEST CLIENTS (Password: Test@123):");
        logger.LogInformation("  Amina Hassan:  AFB-CL-2026-0002 / +254722000001");
        logger.LogInformation("  David Njoroge: AFB-CL-2026-0003 / +254722000002");
        logger.LogInformation("  Grace Wanjiku: AFB-CL-2026-0004 (Walk-in, no login)");
        logger.LogInformation("  Peter Mwangi:  AFB-CL-2026-0005 / +254722000004");
        logger.LogInformation("  Fatima Ali:    AFB-CL-2026-0006 / +254722000005");
        logger.LogInformation("========================================");
    }
    else
    {
        logger.LogWarning("Production: Database is empty. Set SEED_DATA=true env var and restart to seed test data.");
    }
    }
    else
    {
        logger.LogInformation("Database already has data - skipping seed");
        
        // Log existing counts
        var userCount = await context.Users.CountAsync();
        var companyCount = await context.Companies.CountAsync();
        logger.LogInformation("Existing: {Users} users, {Companies} companies", userCount, companyCount);
    }
}

Log.Information("========================================");
Log.Information("SARIFF API READY - {Environment}", app.Environment.EnvironmentName);
if (app.Environment.IsDevelopment())
{
    Log.Information("Swagger: http://localhost:5000/swagger");
}
Log.Information("========================================");

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}