using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SARIFF.Core.Interfaces;
using SARIFF.Infrastructure.Data;
using SARIFF.Infrastructure.Services;

namespace SARIFF.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Database
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

        // HTTP Client for WAHA WhatsApp API
        services.AddHttpClient("WAHA", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // HTTP Client for Expo Push API
        services.AddHttpClient("ExpoPush", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        // Services - Order matters for dependencies
        // Base services (no dependencies on other services)
        services.AddScoped<IPushNotificationService, PushNotificationService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ISystemLogService, SystemLogService>();
        
        // Services with notification dependency
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ICompanyService, CompanyService>();
        services.AddScoped<IClientService, ClientService>();
        services.AddScoped<ITransactionService, TransactionService>();
        
        // Account services
        services.AddScoped<IBankAccountService, BankAccountService>();
        services.AddScoped<IMpesaAgentService, MpesaAgentService>();
        services.AddScoped<ICashAccountService, CashAccountService>();
        services.AddScoped<IExpenseService, ExpenseService>();
        
        // Services with transaction dependency
        services.AddScoped<IExchangeRateService, ExchangeRateService>();
        services.AddScoped<IExchangeService, ExchangeService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IDashboardService, DashboardService>();
        
        // Other services
        services.AddScoped<IInvoiceService, InvoiceService>();
        services.AddScoped<IReconciliationService, ReconciliationService>();
        services.AddScoped<IClientPortalService, ClientPortalService>();
        services.AddScoped<IClientAlertHelper, ClientAlertHelper>(); 
        services.AddScoped<StatementHelper>();
        services.AddScoped<ISuperAdminService, SuperAdminService>();
        services.AddSingleton<ISmsService, SmsService>();
        services.AddScoped<IBalanceAlertService, BalanceAlertService>();
        return services;
    }
}