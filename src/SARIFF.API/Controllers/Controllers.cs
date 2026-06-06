using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SARIFF.Core.DTOs;
using SARIFF.Core.Entities;
using SARIFF.Core.Enums;
using SARIFF.Core.Interfaces;
using SARIFF.Infrastructure.Data;
using SARIFF.Infrastructure.Services;

namespace SARIFF.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public abstract class BaseApiController : ControllerBase
{
    // SECURITY: Only read from JWT claims (set by TenantMiddleware).
    // NEVER fall back to request headers — that's an IDOR vulnerability.
    protected Guid? CompanyId => HttpContext.Items["CompanyId"] as Guid?;
    protected Guid? UserId => HttpContext.Items["UserId"] as Guid?;
    protected string? UserRole => HttpContext.Items["UserRole"] as string;
    protected string IpAddress => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    protected string? UserAgent => HttpContext.Request.Headers["User-Agent"].FirstOrDefault();

    /// <summary>
    /// Cap page size to prevent abuse (e.g. pageSize=1000000)
    /// </summary>
    protected static int ClampPageSize(int pageSize, int max = 200) => Math.Clamp(pageSize, 1, max);
    protected static int ClampPage(int page) => Math.Max(1, page);
}

[Route("api/auth")]
public class AuthController : BaseApiController
{
    private readonly IAuthService _authService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService authService, ILogger<AuthController> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    /// <summary>
    /// UNIFIED LOGIN - One endpoint for ALL users (SuperAdmin, OfficeUser, Client)
    /// - SuperAdmin/OfficeUser: Returns OTP requirement, then call /verify-otp
    /// - Client: Returns tokens directly (no OTP needed)
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] UnifiedLoginDto dto)
    {
        _logger.LogInformation("POST /api/auth/login - Code: {Code}", dto.Code);
        var result = await _authService.UnifiedLoginAsync(dto, IpAddress, UserAgent);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Verify OTP - Step 2 for SuperAdmin/OfficeUser only
    /// </summary>
    [HttpPost("verify-otp")]
    public async Task<IActionResult> VerifyOtp([FromBody] OtpVerifyWithCodeDto dto)
    {
        _logger.LogInformation("POST /api/auth/verify-otp - Code: {Code}", dto.Code);
        var result = await _authService.VerifyOtpAsync(dto, IpAddress, UserAgent);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Refresh Token
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenDto dto)
    {
        var result = await _authService.RefreshTokenAsync(dto, IpAddress);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Logout
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        if (UserId.HasValue)
            await _authService.LogoutAsync(UserId.Value);
        return Ok(new ApiResponse<bool>(true, "Logged out", true));
    }
}
[Authorize(Policy = "SuperAdminOnly")] 
[Route("api/company")]
public class CompanyController : BaseApiController
{
    private readonly ICompanyService _companyService;

    public CompanyController(ICompanyService companyService) => _companyService = companyService;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCompanyDto dto)
    {
        var result = await _companyService.CreateAsync(dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await _companyService.GetByIdAsync(id);
        return result.Success ? Ok(result) : NotFound(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int page = 1, [FromQuery] int pageSize = 10, [FromQuery] string? search = null)
    {
        var result = await _companyService.GetAllAsync(page, pageSize, search);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCompanyDto dto)
    {
        var result = await _companyService.UpdateAsync(id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id)
    {
        var result = await _companyService.ActivateAsync(id);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        var result = await _companyService.DeactivateAsync(id);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid id, [FromBody] AdminResetPasswordDto dto)
    {
        var result = await _companyService.ResetPasswordAsync(id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("{id:guid}/summary")]
    public async Task<IActionResult> GetSummary(Guid id)
    {
        var result = await _companyService.GetSummaryAsync(id);
        return result.Success ? Ok(result) : NotFound(result);
    }

    [HttpGet("summaries")]
    public async Task<IActionResult> GetAllSummaries()
    {
        var result = await _companyService.GetAllSummariesAsync();
        return Ok(result);
    }
}

[Authorize(Policy = "OfficeUserOnly")] 
[Route("api/client")]
public class ClientController : BaseApiController
{
    private readonly IClientService _clientService;

    public ClientController(IClientService clientService) => _clientService = clientService;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateClientDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _clientService.CreateAsync(CompanyId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _clientService.GetByIdAsync(CompanyId.Value, id);
        return result.Success ? Ok(result) : NotFound(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int page = 1, [FromQuery] int pageSize = 10, [FromQuery] string? search = null, [FromQuery] string? filter = null)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _clientService.GetAllAsync(CompanyId.Value, page, pageSize, search, filter);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateClientDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _clientService.UpdateAsync(CompanyId.Value, id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("{id:guid}/convert")]
    public async Task<IActionResult> Convert(Guid id, [FromBody] ConvertClientDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _clientService.ConvertToPermamentAsync(CompanyId.Value, id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid id, [FromBody] ResetClientPasswordDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _clientService.ResetPasswordAsync(CompanyId.Value, id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _clientService.DeleteAsync(CompanyId.Value, id);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _clientService.GetStatsAsync(CompanyId.Value);
        return Ok(result);
    }

    [HttpGet("{id:guid}/statement")]
    public async Task<IActionResult> GetStatement(Guid id, 
        [FromQuery] DateTime? startDate, 
        [FromQuery] DateTime? endDate,
        [FromQuery] Currency? currency,
        [FromQuery] TransactionType? transactionType)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _clientService.GetStatementAsync(CompanyId.Value, id, new StatementFilterDto(startDate, endDate, currency, transactionType));
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>
    /// Reverse a client transaction from the client statement.
    /// This is the ONLY way to delete transactions involving client accounts.
    /// General DELETE /api/transaction/{id} blocks client transactions.
    /// </summary>
    [HttpDelete("{clientId:guid}/transaction/{transactionId:guid}")]
    public async Task<IActionResult> ReverseTransaction(Guid clientId, Guid transactionId, [FromBody] DeleteTransactionDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _clientService.ReverseTransactionAsync(CompanyId.Value, clientId, transactionId, UserId.Value, dto.Reason);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// PERF: Lightweight client list for dropdowns — returns only id, code, name, phone, isActive.
    /// No balance calculation, no transaction totals. Use this for TransactionForm, ExchangeSection, etc.
    /// </summary>
    [HttpGet("lookup")]
    public async Task<IActionResult> Lookup([FromQuery] string? search = null)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _clientService.GetLookupAsync(CompanyId.Value, search);
        return Ok(result);
    }
}

[Authorize(Policy = "OfficeUserOnly")]
[Route("api/transaction")]
public class TransactionController : BaseApiController
{
    private readonly ITransactionService _transactionService;

    public TransactionController(ITransactionService transactionService) => _transactionService = transactionService;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTransactionDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _transactionService.CreateAsync(CompanyId.Value, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _transactionService.GetByIdAsync(CompanyId.Value, id);
        return result.Success ? Ok(result) : NotFound(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int page = 1, [FromQuery] int pageSize = 10, [FromQuery] DateTime? startDate = null, [FromQuery] DateTime? endDate = null, [FromQuery] TransactionType? type = null, [FromQuery] Currency? currency = null)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var filter = new ReportFilterDto(startDate, endDate, type, currency, null);
        var result = await _transactionService.GetAllAsync(CompanyId.Value, page, pageSize, filter);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTransactionDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _transactionService.UpdateAsync(CompanyId.Value, id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromBody] DeleteTransactionDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _transactionService.DeleteAsync(CompanyId.Value, id, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("today")]
    public async Task<IActionResult> GetTodaySummary()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _transactionService.GetTodaySummaryAsync(CompanyId.Value);
        return Ok(result);
    }

    [HttpGet("recent")]
    public async Task<IActionResult> GetRecent([FromQuery] int count = 10)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _transactionService.GetRecentAsync(CompanyId.Value, count);
        return Ok(result);
    }
}

[Authorize(Policy = "OfficeUserOnly")] 
[Route("api/bank")]
public class BankAccountController : BaseApiController
{
    private readonly IBankAccountService _bankService;

    public BankAccountController(IBankAccountService bankService) => _bankService = bankService;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBankAccountDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _bankService.CreateAsync(CompanyId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _bankService.GetAllAsync(CompanyId.Value);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _bankService.GetByIdAsync(CompanyId.Value, id);
        return result.Success ? Ok(result) : NotFound(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateBankAccountDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _bankService.UpdateAsync(CompanyId.Value, id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _bankService.DeleteAsync(CompanyId.Value, id);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _bankService.GetStatsAsync(CompanyId.Value);
        return Ok(result);
    }

    [HttpGet("{id:guid}/statement")]
    public async Task<IActionResult> GetStatement( Guid id, 
        [FromQuery] DateTime? startDate, 
        [FromQuery] DateTime? endDate,
        [FromQuery] Currency? currency,
        [FromQuery] TransactionType? transactionType)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _bankService.GetStatementAsync(CompanyId.Value, id, new StatementFilterDto(startDate, endDate, currency, transactionType));
        return result.Success ? Ok(result) : NotFound(result);
    }
}

[Authorize(Policy = "OfficeUserOnly")] 
[Route("api/mpesa")]
public class MpesaAgentController : BaseApiController
{
    private readonly IMpesaAgentService _mpesaService;

    public MpesaAgentController(IMpesaAgentService mpesaService) => _mpesaService = mpesaService;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMpesaAgentDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _mpesaService.CreateAsync(CompanyId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _mpesaService.GetAllAsync(CompanyId.Value);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _mpesaService.GetByIdAsync(CompanyId.Value, id);
        return result.Success ? Ok(result) : NotFound(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateMpesaAgentDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _mpesaService.UpdateAsync(CompanyId.Value, id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _mpesaService.DeleteAsync(CompanyId.Value, id);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _mpesaService.GetStatsAsync(CompanyId.Value);
        return Ok(result);
    }

    [HttpGet("{id:guid}/statement")]
    public async Task<IActionResult> GetStatement( Guid id, 
        [FromQuery] DateTime? startDate, 
        [FromQuery] DateTime? endDate,
        [FromQuery] Currency? currency,
        [FromQuery] TransactionType? transactionType)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _mpesaService.GetStatementAsync(CompanyId.Value, id, new StatementFilterDto(startDate, endDate, currency, transactionType));
        return result.Success ? Ok(result) : NotFound(result);
    }
}




[Authorize(Policy = "OfficeUserOnly")]  // FIXED: Was missing!
[Route("api/cash")]
public class CashController : BaseApiController
{
    private readonly ICashAccountService _cashService;
    private readonly ILogger<CashController> _logger;

    public CashController(ICashAccountService cashService, ILogger<CashController> logger)
    {
        _cashService = cashService;
        _logger = logger;
    }

    /// <summary>
    /// NEW: Create a cash account with opening balance
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCashAccountDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();

        _logger.LogInformation("Creating cash account for company {CompanyId}, currency {Currency}, opening balance {Balance}",
            CompanyId.Value, dto.Currency, dto.OpeningBalance);

        var result = await _cashService.CreateAsync(CompanyId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Get all cash accounts for the company
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _cashService.GetAllAsync(CompanyId.Value);
        return Ok(result);
    }

    /// <summary>
    /// NEW: Get cash account by ID
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _cashService.GetByIdAsync(CompanyId.Value, id);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>
    /// NEW: Get cash account by currency
    /// </summary>
    [HttpGet("currency/{currency}")]
    public async Task<IActionResult> GetByCurrency(Currency currency)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _cashService.GetByCurrencyAsync(CompanyId.Value, currency);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>
    /// NEW: Update cash account (opening balance)
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCashAccountDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();

        _logger.LogInformation("Updating cash account {Id} for company {CompanyId}", id, CompanyId.Value);

        var result = await _cashService.UpdateAsync(CompanyId.Value, id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// NEW: Delete cash account (only if no transactions)
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();

        _logger.LogInformation("Deleting cash account {Id} for company {CompanyId}", id, CompanyId.Value);

        var result = await _cashService.DeleteAsync(CompanyId.Value, id);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Get cash account statistics
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _cashService.GetStatsAsync(CompanyId.Value);
        return Ok(result);
    }

    /// <summary>
    /// Get cash account statement by currency
    /// </summary>
    [HttpGet("statement/{currency}")]
    public async Task<IActionResult> GetStatement(
        Currency currency, 
        [FromQuery] DateTime? startDate, 
        [FromQuery] DateTime? endDate,
        [FromQuery] TransactionType? transactionType)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        
        var filter = new StatementFilterDto(startDate, endDate,currency, null);
        var result = await _cashService.GetStatementAsync(CompanyId.Value, currency, filter);
        return result.Success ? Ok(result) : NotFound(result);
    }
}

/// <summary>
/// FIXED Expense Controller
/// 
/// Changes:
/// 1. DeleteAsync now passes UserId for proper audit trail
/// </summary>
[Authorize(Policy = "OfficeUserOnly")]
[Route("api/expense")]
public class ExpenseController : BaseApiController
{
    private readonly IExpenseService _expenseService;
    private readonly ILogger<ExpenseController> _logger;

    public ExpenseController(IExpenseService expenseService, ILogger<ExpenseController> logger)
    {
        _expenseService = expenseService;
        _logger = logger;
    }

    [HttpPost("category")]
    public async Task<IActionResult> CreateCategory([FromBody] CreateExpenseCategoryDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _expenseService.CreateCategoryAsync(CompanyId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("category")]
    public async Task<IActionResult> GetCategories()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _expenseService.GetCategoriesAsync(CompanyId.Value);
        return Ok(result);
    }

    [HttpPut("category/{id:guid}")]
    public async Task<IActionResult> UpdateCategory(Guid id, [FromBody] UpdateExpenseCategoryDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _expenseService.UpdateCategoryAsync(CompanyId.Value, id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("category/{id:guid}")]
    public async Task<IActionResult> DeleteCategory(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _expenseService.DeleteCategoryAsync(CompanyId.Value, id);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateExpenseDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        
        _logger.LogInformation("Creating expense for company {CompanyId}, amount {Amount} {Currency}",
            CompanyId.Value, dto.Amount, dto.Currency);
            
        var result = await _expenseService.CreateAsync(CompanyId.Value, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _expenseService.GetByIdAsync(CompanyId.Value, id);
        return result.Success ? Ok(result) : NotFound(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 10,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] Currency? currency = null)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        
        var filter = new ReportFilterDto(startDate, endDate, null, currency,null,null);
        var result = await _expenseService.GetAllAsync(CompanyId.Value, page, pageSize, filter);
        return Ok(result);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _expenseService.GetStatsAsync(CompanyId.Value);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateExpenseDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        
        _logger.LogInformation("Updating expense {Id} for company {CompanyId}", id, CompanyId.Value);
        
        var result = await _expenseService.UpdateAsync(CompanyId.Value, id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// FIXED: Now passes UserId for proper audit trail and reversal tracking
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        
        _logger.LogInformation("Deleting expense {Id} for company {CompanyId} by user {UserId}", 
            id, CompanyId.Value, UserId.Value);
        
        var result = await _expenseService.DeleteAsync(CompanyId.Value, id, UserId.Value);
        return result.Success ? Ok(result) : BadRequest(result);
    }
}
[Authorize(Policy = "OfficeUserOnly")] 
[Route("api/exchange-rate")]
public class ExchangeRateController : BaseApiController
{
    private readonly IExchangeRateService _exchangeService;

    public ExchangeRateController(IExchangeRateService exchangeService) => _exchangeService = exchangeService;

    [HttpPost]
    public async Task<IActionResult> SetRate([FromBody] SetExchangeRateDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _exchangeService.SetRateAsync(CompanyId.Value, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("current")]
    public async Task<IActionResult> GetCurrentRate()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.GetCurrentRateAsync(CompanyId.Value);
        return result.Success ? Ok(result) : NotFound(result);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.GetHistoryAsync(CompanyId.Value);
        return Ok(result);
    }

    [HttpPost("convert")]
    public async Task<IActionResult> Convert([FromBody] CurrencyConvertDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.ConvertAsync(CompanyId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("transaction")]
    public async Task<IActionResult> CreateExchangeTransaction([FromBody] ExchangeTransactionDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _exchangeService.CreateExchangeTransactionAsync(CompanyId.Value, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }
}

[Authorize(Policy = "OfficeUserOnly")] 
[Route("api/invoice")]
public class InvoiceController : BaseApiController
{
    private readonly IInvoiceService _invoiceService;

    public InvoiceController(IInvoiceService invoiceService) => _invoiceService = invoiceService;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateInvoiceDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _invoiceService.CreateAsync(CompanyId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _invoiceService.GetAllAsync(CompanyId.Value, page, pageSize);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _invoiceService.GetByIdAsync(CompanyId.Value, id);
        return result.Success ? Ok(result) : NotFound(result);
    }

    [HttpPut("{id:guid}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateInvoiceStatusDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _invoiceService.UpdateStatusAsync(CompanyId.Value, id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _invoiceService.DeleteAsync(CompanyId.Value, id);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("{id:guid}/pdf")]
    public async Task<IActionResult> GeneratePdf(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _invoiceService.GeneratePdfAsync(CompanyId.Value, id);
        if (!result.Success || result.Data == null) return BadRequest(result);
        return File(result.Data, "application/pdf", $"invoice-{id}.pdf");
    }
}

[Authorize(Policy = "OfficeUserOnly")] 
[Route("api/reconciliation")]
public class ReconciliationController : BaseApiController
{
    private readonly IReconciliationService _reconciliationService;

    public ReconciliationController(IReconciliationService reconciliationService) => _reconciliationService = reconciliationService;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReconciliationDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _reconciliationService.CreateAsync(CompanyId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] AccountType? accountType = null)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _reconciliationService.GetAllAsync(CompanyId.Value, accountType);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _reconciliationService.GetByIdAsync(CompanyId.Value, id);
        return result.Success ? Ok(result) : NotFound(result);
    }

    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, [FromBody] CompleteReconciliationDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _reconciliationService.CompleteAsync(CompanyId.Value, id, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("expected-balance")]
    public async Task<IActionResult> GetExpectedBalance([FromQuery] AccountType accountType, [FromQuery] Guid accountId)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _reconciliationService.GetExpectedBalanceAsync(CompanyId.Value, accountType, accountId);
        return Ok(result);
    }
    
    /// <summary>
    /// Get all accounts (Bank, M-Pesa, Cash) with pending reconciliation count
    /// </summary>
    [HttpGet("accounts")]
    public async Task<IActionResult> GetAccountsWithStats()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _reconciliationService.GetAccountsWithStatsAsync(CompanyId.Value);
        return Ok(result);
    }

    /// <summary>
    /// Get transactions for a specific account
    /// Filter by: status (Pending/Matched/Unmatched), startDate, endDate
    /// </summary>
    [HttpGet("account/{accountType}/{accountId}/transactions")]
    public async Task<IActionResult> GetAccountTransactions(
        AccountType accountType, 
        Guid accountId,
        [FromQuery] ReconciliationStatus? status = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        
        var filter = new ReconciliationFilterDto(status, startDate, endDate);
        var result = await _reconciliationService.GetAccountTransactionsAsync(
            CompanyId.Value, accountType, accountId, filter, page, pageSize);
        
        return Ok(result);
    }

    /// <summary>
    /// Get reconciliation balance summary for an account
    /// Returns: ExpectedBalance, ActualBalance, Variance, counts
    /// </summary>
    [HttpGet("account/{accountType}/{accountId}/summary")]
    public async Task<IActionResult> GetAccountSummary(AccountType accountType, Guid accountId)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _reconciliationService.GetAccountBalanceSummaryAsync(CompanyId.Value, accountType, accountId);
        return Ok(result);
    }

    /// <summary>
    /// Reconcile a single transaction
    /// Provide ActualAmount and Status (Matched or Unmatched)
    /// </summary>
    [HttpPut("transaction/{transactionId}")]
    public async Task<IActionResult> ReconcileTransaction(Guid transactionId, [FromBody] ReconcileTransactionDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _reconciliationService.ReconcileTransactionAsync(
            CompanyId.Value, transactionId, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Bulk reconcile multiple transactions at once
    /// All transactions get the same status
    /// </summary>
    [HttpPut("bulk")]
    public async Task<IActionResult> BulkReconcile([FromBody] BulkReconcileDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _reconciliationService.BulkReconcileAsync(CompanyId.Value, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }
}

[Authorize(Policy = "OfficeUserOnly")] 
[Route("api/report")]
public class ReportController : BaseApiController
{
    private readonly IReportService _reportService;

    public ReportController(IReportService reportService) => _reportService = reportService;

    [HttpGet("daily")]
    public async Task<IActionResult> GetDailyReport([FromQuery] DateTime? date = null)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _reportService.GetDailyReportAsync(CompanyId.Value, date ?? DateTime.UtcNow);
        return Ok(result);
    }

    [HttpGet("transactions")]
    public async Task<IActionResult> GetTransactionReport([FromQuery] int page = 1, [FromQuery] int pageSize = 10, [FromQuery] DateTime? startDate = null, [FromQuery] DateTime? endDate = null, [FromQuery] TransactionType? type = null, [FromQuery] Currency? currency = null)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var filter = new ReportFilterDto(startDate, endDate, type, currency, null);
        var result = await _reportService.GetTransactionReportAsync(CompanyId.Value, filter, page, pageSize);
        return Ok(result);
    }

    [HttpGet("client-balances")]
    public async Task<IActionResult> GetClientBalanceReport([FromQuery] string? balanceType = null)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _reportService.GetClientBalanceReportAsync(CompanyId.Value, balanceType);
        return Ok(result);
    }

    [HttpGet("account-summary")]
    public async Task<IActionResult> GetAccountSummaryReport()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _reportService.GetAccountSummaryReportAsync(CompanyId.Value);
        return Ok(result);
    }
}

[Authorize(Policy = "OfficeUserOnly")] 
[Route("api/dashboard")]
public class DashboardController : BaseApiController
{
    private readonly IDashboardService _dashboardService;
    private readonly ICompanyService _companyService;

    public DashboardController(IDashboardService dashboardService, ICompanyService companyService)
    {
        _dashboardService = dashboardService;
        _companyService = companyService;
    }

    [HttpGet]
    public async Task<IActionResult> GetDashboard()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _dashboardService.GetOfficeUserDashboardAsync(CompanyId.Value);
        return Ok(result);
    }

    /// <summary>
    /// OfficeUser updates their own company settings (name, email, address, etc.)
    /// </summary>
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateCompanyDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _companyService.UpdateAsync(CompanyId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// OfficeUser gets their own company info
    /// </summary>
    [HttpGet("company")]
    public async Task<IActionResult> GetCompanyInfo()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _companyService.GetByIdAsync(CompanyId.Value);
        return result.Success ? Ok(result) : NotFound(result);
    }
}



[Authorize(Policy = "SuperAdminOnly")] 
[Route("api/admin")]
public class AdminController : BaseApiController
{
    private readonly IDashboardService _dashboardService;
    private readonly IAuditService _auditService;
    private readonly ISystemLogService _systemLogService;
    private readonly ISuperAdminService _superAdminService;
    private readonly ICompanyService _companyService;

    public AdminController(
        IDashboardService dashboardService, 
        IAuditService auditService, 
        ISystemLogService systemLogService,
        ISuperAdminService superAdminService,
        ICompanyService companyService)
    {
        _dashboardService = dashboardService;
        _auditService = auditService;
        _systemLogService = systemLogService;
        _superAdminService = superAdminService;
        _companyService = companyService;
    }
    

    /// <summary>
    /// Basic dashboard (backward compatible)
    /// </summary>
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard()
    {
        var result = await _dashboardService.GetSuperAdminDashboardAsync();
        return Ok(result);
    }

    /// <summary>
    /// Basic audit logs (backward compatible)
    /// </summary>
    [HttpGet("audit-logs")]
    public async Task<IActionResult> GetAuditLogs([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] Guid? companyId = null)
    {
        var result = await _auditService.GetAuditLogsAsync(page, pageSize, companyId);
        return Ok(result);
    }

    /// <summary>
    /// Login history (backward compatible)
    /// </summary>
    [HttpGet("login-history")]
    public async Task<IActionResult> GetLoginHistory([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] Guid? companyId = null)
    {
        var result = await _auditService.GetLoginHistoryAsync(page, pageSize, companyId);
        return Ok(result);
    }

    /// <summary>
    /// System logs (backward compatible)
    /// </summary>
    [HttpGet("system-logs")]
    public async Task<IActionResult> GetSystemLogs([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? level = null)
    {
        var result = await _systemLogService.GetLogsAsync(page, pageSize, level);
        return Ok(result);
    }

  

    /// <summary>
    /// Extended dashboard with full stats
    /// </summary>
    [HttpGet("dashboard/extended")]
    public async Task<IActionResult> GetDashboardExtended()
    {
        var result = await _superAdminService.GetDashboardAsync();
        return result.Success ? Ok(result) : BadRequest(result);
    }

    // ==================== COMPANIES ====================

    /// <summary>
    /// Get all companies with detailed stats
    /// </summary>
    [HttpGet("companies")]
    public async Task<IActionResult> GetCompanies(
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 10, 
        [FromQuery] string? search = null,
        [FromQuery] string? status = null)
    {
        var result = await _superAdminService.GetAllCompaniesWithStatsAsync(page, pageSize, search, status);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Get company details with balances and activity
    /// </summary>
    [HttpGet("companies/{id}/details")]
    public async Task<IActionResult> GetCompanyDetails(Guid id)
    {
        var result = await _superAdminService.GetCompanyDetailAsync(id);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>
    /// Update company subscription
    /// </summary>
    [HttpPut("companies/{id}/subscription")]
    public async Task<IActionResult> UpdateSubscription(Guid id, [FromBody] UpdateSubscriptionDto dto)
    {
        var result = await _superAdminService.UpdateSubscriptionAsync(id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Update company details (name, owner, email, etc.)
    /// </summary>
    [HttpPut("companies/{id}")]
    public async Task<IActionResult> UpdateCompanyDetails(Guid id, [FromBody] UpdateCompanyDto dto)
    {
        var result = await _companyService.UpdateAsync(id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Suspend company
    /// </summary>
    [HttpPost("companies/{id}/suspend")]
    public async Task<IActionResult> SuspendCompany(Guid id, [FromBody] SuspendCompanyDto dto)
    {
        var result = await _superAdminService.SuspendCompanyAsync(id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Activate/Unlock company
    /// </summary>
    [HttpPost("companies/{id}/activate")]
    public async Task<IActionResult> ActivateCompany(Guid id)
    {
        var result = await _superAdminService.ActivateCompanyAsync(id);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Reset password for Office User (Company)
    /// SuperAdmin can reset any company's password
    /// </summary>
    [HttpPost("companies/{id}/reset-password")]
    public async Task<IActionResult> ResetOfficeUserPassword(Guid id, [FromBody] AdminResetPasswordDto dto)
    {
        var result = await _superAdminService.ResetOfficeUserPasswordAsync(id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    // ==================== SYSTEM HEALTH ====================

    /// <summary>
    /// Get system health status
    /// </summary>
    [HttpGet("system/health")]
    public async Task<IActionResult> GetSystemHealth()
    {
        var result = await _superAdminService.GetSystemHealthAsync();
        return result.Success ? Ok(result) : BadRequest(result);
    }

    // ==================== SECURITY ====================

    /// <summary>
    /// Get security overview
    /// </summary>
    [HttpGet("security/overview")]
    public async Task<IActionResult> GetSecurityOverview()
    {
        var result = await _superAdminService.GetSecurityOverviewAsync();
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Get security alerts
    /// </summary>
    [HttpGet("security/alerts")]
    public async Task<IActionResult> GetSecurityAlerts(
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 20, 
        [FromQuery] bool? resolved = null)
    {
        var result = await _superAdminService.GetSecurityAlertsAsync(page, pageSize, resolved);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Resolve a security alert
    /// </summary>
    [HttpPost("security/alerts/{id}/resolve")]
    public async Task<IActionResult> ResolveSecurityAlert(Guid id, [FromBody] ResolveAlertDto dto)
    {
        var result = await _superAdminService.ResolveSecurityAlertAsync(id, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Block an IP address
    /// </summary>
    [HttpPost("security/block-ip")]
    public async Task<IActionResult> BlockIP([FromBody] BlockIPDto dto)
    {
        var result = await _superAdminService.BlockIPAsync(dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Unblock an IP address
    /// </summary>
    [HttpDelete("security/blocked-ips/{id}")]
    public async Task<IActionResult> UnblockIP(Guid id)
    {
        var result = await _superAdminService.UnblockIPAsync(id);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Get IP whitelist
    /// </summary>
    [HttpGet("security/ip-whitelist")]
    public async Task<IActionResult> GetIPWhitelist()
    {
        var result = await _superAdminService.GetIPWhitelistAsync();
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Add IP to whitelist
    /// </summary>
    [HttpPost("security/ip-whitelist")]
    public async Task<IActionResult> AddIPToWhitelist([FromBody] AddIPWhitelistDto dto)
    {
        var result = await _superAdminService.AddIPToWhitelistAsync(dto, UserId ?? Guid.Empty);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Remove IP from whitelist
    /// </summary>
    [HttpDelete("security/ip-whitelist/{id}")]
    public async Task<IActionResult> RemoveIPFromWhitelist(Guid id)
    {
        var result = await _superAdminService.RemoveIPFromWhitelistAsync(id);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    // ==================== FINANCIAL ====================

    /// <summary>
    /// Get financial overview
    /// </summary>
    [HttpGet("financials/overview")]
    public async Task<IActionResult> GetFinancialOverview()
    {
        var result = await _superAdminService.GetFinancialOverviewAsync();
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Get payment history
    /// </summary>
    [HttpGet("financials/payments")]
    public async Task<IActionResult> GetPaymentHistory(
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 20, 
        [FromQuery] Guid? companyId = null)
    {
        var result = await _superAdminService.GetPaymentHistoryAsync(page, pageSize, companyId);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Record a payment
    /// </summary>
    [HttpPost("financials/payments")]
    public async Task<IActionResult> RecordPayment([FromBody] RecordPaymentDto dto)
    {
        var result = await _superAdminService.RecordPaymentAsync(dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    // ==================== ANALYTICS ====================

    /// <summary>
    /// Get analytics overview
    /// </summary>
    [HttpGet("analytics/overview")]
    public async Task<IActionResult> GetAnalyticsOverview(
        [FromQuery] DateTime? startDate = null, 
        [FromQuery] DateTime? endDate = null)
    {
        var result = await _superAdminService.GetAnalyticsOverviewAsync(startDate, endDate);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    // ==================== AUDIT LOGS (EXTENDED) ====================

    /// <summary>
    /// Get audit logs with extended filtering
    /// </summary>
    [HttpGet("audit-logs/extended")]
    public async Task<IActionResult> GetAuditLogsExtended(
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 20,
        [FromQuery] Guid? companyId = null,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? action = null,
        [FromQuery] string? entityType = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] string? severity = null)
    {
        var filter = new AuditLogFilterDto(companyId, userId, action, entityType, startDate, endDate, severity);
        var result = await _superAdminService.GetAuditLogsExtendedAsync(page, pageSize, filter);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Export audit logs as CSV
    /// </summary>
    [HttpGet("audit-logs/export")]
    public async Task<IActionResult> ExportAuditLogs(
        [FromQuery] Guid? companyId = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null)
    {
        var filter = new AuditLogFilterDto(companyId, null, null, null, startDate, endDate, null);
        var result = await _superAdminService.ExportAuditLogsAsync(filter);
        
        if (!result.Success || result.Data == null)
            return BadRequest(result);
            
        return File(result.Data, "text/csv", $"audit-logs-{DateTime.UtcNow:yyyy-MM-dd}.csv");
    }
}


[Authorize(Policy = "ClientOnly")] 
[Route("api/portal")]
public class ClientPortalController : BaseApiController
{
    private readonly IClientPortalService _clientPortalService;

    public ClientPortalController(IClientPortalService clientPortalService) => _clientPortalService = clientPortalService;

    /// <summary>
    /// Get client dashboard with profile, recent transactions, alerts, and quick stats
    /// </summary>
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard()
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _clientPortalService.GetDashboardAsync(CompanyId.Value, UserId.Value);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>
    /// Get client profile
    /// </summary>
    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile()
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _clientPortalService.GetProfileAsync(CompanyId.Value, UserId.Value);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>
    /// Update client profile (email, phone)
    /// </summary>
    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateClientProfileDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _clientPortalService.UpdateProfileAsync(CompanyId.Value, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Get client transactions with pagination and filters
    /// </summary>
    [HttpGet("transactions")]
    public async Task<IActionResult> GetTransactions(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] Currency? currency = null,
        [FromQuery] TransactionType? type = null,
        [FromQuery] string? search = null)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();

        var filters = new TransactionFilters
        {
            StartDate = startDate,
            EndDate = endDate,
            Currency = currency,
            Type = type,
            Search = search
        };

        var result = await _clientPortalService.GetTransactionsAsync(
            CompanyId.Value, UserId.Value, page, pageSize, filters);
        return Ok(result);
    }

    /// <summary>
    /// Get a single transaction by ID
    /// </summary>
    [HttpGet("transactions/{transactionId}")]
    public async Task<IActionResult> GetTransactionById(Guid transactionId)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _clientPortalService.GetTransactionByIdAsync(
            CompanyId.Value, UserId.Value, transactionId);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>
    /// Download transaction receipt
    /// </summary>
    [HttpGet("transactions/{transactionId}/receipt")]
    public async Task<IActionResult> DownloadReceipt(Guid transactionId)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        try
        {
            var bytes = await _clientPortalService.GenerateTransactionReceiptAsync(
                CompanyId.Value, UserId.Value, transactionId);
            return File(bytes, "text/plain", $"receipt-{transactionId}.txt");
        }
        catch (Exception ex)
        {
            return NotFound(new ApiResponse<object>(false, ex.Message, null));
        }
    }

    /// <summary>
    /// Get client statement for date range
    /// </summary>
    [HttpGet("statement")]
    public async Task<IActionResult> GetStatement(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromQuery] Currency? currency = null)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _clientPortalService.GetStatementAsync(
            CompanyId.Value, UserId.Value, startDate, endDate, currency);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>
    /// Download statement as PDF/text
    /// </summary>
    [HttpGet("statement/pdf")]
    public async Task<IActionResult> DownloadStatementPdf(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromQuery] Currency? currency = null)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        try
        {
            var bytes = await _clientPortalService.GenerateStatementPdfAsync(
                CompanyId.Value, UserId.Value, startDate, endDate, currency);
            return File(bytes, "text/plain", $"statement-{startDate:yyyy-MM-dd}-to-{endDate:yyyy-MM-dd}.txt");
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<object>(false, ex.Message, null));
        }
    }

    /// <summary>
    /// Export transactions as CSV
    /// </summary>
    [HttpGet("transactions/export")]
    public async Task<IActionResult> ExportTransactionsCsv(
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] Currency? currency = null)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        try
        {
            var bytes = await _clientPortalService.ExportTransactionsCsvAsync(
                CompanyId.Value, UserId.Value, startDate, endDate, currency);
            return File(bytes, "text/csv", $"transactions-{DateTime.UtcNow:yyyy-MM-dd}.csv");
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<object>(false, ex.Message, null));
        }
    }

    /// <summary>
    /// Get client alerts with pagination
    /// </summary>
    [HttpGet("alerts")]
    public async Task<IActionResult> GetAlerts(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool unreadOnly = false)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _clientPortalService.GetAlertsAsync(
            CompanyId.Value, UserId.Value, page, pageSize, unreadOnly);
        return Ok(result);
    }

    /// <summary>
    /// Get unread alerts count
    /// </summary>
    [HttpGet("alerts/unread-count")]
    public async Task<IActionResult> GetUnreadAlertsCount()
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _clientPortalService.GetUnreadCountAsync(CompanyId.Value, UserId.Value);
        return Ok(result);
    }

    /// <summary>
    /// Mark single alert as read
    /// </summary>
    [HttpPost("alerts/{alertId}/read")]
    public async Task<IActionResult> MarkAlertAsRead(Guid alertId)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _clientPortalService.MarkAlertAsReadAsync(
            CompanyId.Value, UserId.Value, alertId);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>
    /// Mark all alerts as read
    /// </summary>
    [HttpPost("alerts/read-all")]
    public async Task<IActionResult> MarkAllAlertsAsRead()
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _clientPortalService.MarkAllAlertsAsReadAsync(CompanyId.Value, UserId.Value);
        return Ok(result);
    }

    /// <summary>
    /// Get client analytics
    /// </summary>
    [HttpGet("analytics")]
    public async Task<IActionResult> GetAnalytics([FromQuery] int months = 6)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _clientPortalService.GetAnalyticsAsync(CompanyId.Value, UserId.Value, months);
        return Ok(result);
    }

    /// <summary>
    /// Change client password
    /// </summary>
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _clientPortalService.ChangePasswordAsync(CompanyId.Value, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }
    
}

// =====================================================
// EXCHANGE CONTROLLER - Forex Bureau Operations
// =====================================================

/// <summary>
/// Exchange Account Controller - Forex Bureau Operations
/// Handles exchange rates, float management, transactions, and reports
/// </summary>
[Authorize(Policy = "OfficeUserOnly")]
[Route("api/exchange")]
public class ExchangeController : BaseApiController
{
    private readonly IExchangeService _exchangeService;
    private readonly ILogger<ExchangeController> _logger;

    public ExchangeController(IExchangeService exchangeService, ILogger<ExchangeController> logger)
    {
        _exchangeService = exchangeService;
        _logger = logger;
    }

    // ==================== RATE MANAGEMENT ====================

    /// <summary>
    /// Get current active exchange rate
    /// </summary>
    [HttpGet("rate")]
    public async Task<IActionResult> GetCurrentRate()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.GetCurrentRateAsync(CompanyId.Value);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Set new exchange rate (deactivates previous)
    /// </summary>
    [HttpPost("rate")]
    public async Task<IActionResult> SetRate([FromBody] SetExchangeRateDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();

        if (dto.BuyRate <= 0 || dto.SellRate <= 0)
            return BadRequest(new ApiResponse<object>(false, "Rates must be greater than 0", null));

        if (dto.BuyRate >= dto.SellRate)
            return BadRequest(new ApiResponse<object>(false, "Sell rate must be higher than buy rate", null));

        var result = await _exchangeService.SetRateAsync(CompanyId.Value, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Get exchange rate history
    /// </summary>
    [HttpGet("rate/history")]
    public async Task<IActionResult> GetRateHistory([FromQuery] int days = 30)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.GetRateHistoryAsync(CompanyId.Value, days);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    // ==================== FLOAT MANAGEMENT ====================

    /// <summary>
    /// Get current float balances and profit
    /// </summary>
    [HttpGet("float")]
    public async Task<IActionResult> GetFloat()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.GetFloatAsync(CompanyId.Value);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Fund the exchange float (add KES or buy USD)
    /// </summary>
    [HttpPost("float/fund")]
    public async Task<IActionResult> FundFloat([FromBody] FundFloatDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();

        if (dto.Amount <= 0)
            return BadRequest(new ApiResponse<object>(false, "Amount must be greater than 0", null));

        if (dto.Currency == Currency.USD && (!dto.PurchaseRate.HasValue || dto.PurchaseRate <= 0))
            return BadRequest(new ApiResponse<object>(false, "Purchase rate is required for USD funding", null));

        var result = await _exchangeService.FundFloatAsync(CompanyId.Value, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Withdraw from exchange float
    /// </summary>
    [HttpPost("float/withdraw")]
    public async Task<IActionResult> WithdrawFloat([FromBody] WithdrawFloatDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();

        if (dto.Amount <= 0)
            return BadRequest(new ApiResponse<object>(false, "Amount must be greater than 0", null));

        var result = await _exchangeService.WithdrawFloatAsync(CompanyId.Value, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Settle accumulated profit to another account
    /// </summary>
    [HttpPost("float/settle-profit")]
    public async Task<IActionResult> SettleProfit([FromBody] SettleProfitDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();

        if (dto.Amount <= 0)
            return BadRequest(new ApiResponse<object>(false, "Amount must be greater than 0", null));

        var result = await _exchangeService.SettleProfitAsync(CompanyId.Value, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Get float movement history
    /// </summary>
    [HttpGet("float/movements")]
    public async Task<IActionResult> GetFloatMovements([FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.GetFloatMovementsAsync(CompanyId.Value, from, to);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    // ==================== EXCHANGE TRANSACTIONS ====================

    /// <summary>
    /// Create a new exchange transaction
    /// </summary>
    [HttpPost("transaction")]
    public async Task<IActionResult> CreateExchange([FromBody] CreateExchangeDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();

        if (dto.Amount <= 0)
            return BadRequest(new ApiResponse<object>(false, "Amount must be greater than 0", null));

        if (dto.ClientId == Guid.Empty)
            return BadRequest(new ApiResponse<object>(false, "Client is required", null));

        var result = await _exchangeService.CreateExchangeAsync(CompanyId.Value, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Get exchange transactions with filtering and pagination
    /// </summary>
    [HttpGet("transactions")]
    public async Task<IActionResult> GetExchanges(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 15,
        [FromQuery] string? search = null,
        [FromQuery] ExchangeType? type = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.GetExchangesAsync(CompanyId.Value, page, pageSize, search, type, from, to);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Get a single exchange transaction by ID
    /// </summary>
    [HttpGet("transaction/{id}")]
    public async Task<IActionResult> GetExchange(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.GetExchangeByIdAsync(CompanyId.Value, id);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>
    /// Void an exchange transaction
    /// </summary>
    [HttpPost("transaction/{id}/void")]
    public async Task<IActionResult> VoidExchange(Guid id, [FromBody] VoidExchangeDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();

        if (string.IsNullOrWhiteSpace(dto.Reason))
            return BadRequest(new ApiResponse<object>(false, "Void reason is required", null));

        var result = await _exchangeService.VoidExchangeAsync(CompanyId.Value, UserId.Value, id, dto.Reason);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    // ==================== DAILY OPERATIONS ====================

    /// <summary>
    /// Get today's summary (volume, profit, opening/closing)
    /// </summary>
    [HttpGet("daily/today")]
    public async Task<IActionResult> GetTodaySummary()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.GetTodaySummaryAsync(CompanyId.Value);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Record opening float (start of day)
    /// </summary>
    [HttpPost("daily/opening")]
    public async Task<IActionResult> RecordOpeningFloat([FromBody] OpeningFloatDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();

        if (dto.KesCount < 0 || dto.UsdCount < 0)
            return BadRequest(new ApiResponse<object>(false, "Counts cannot be negative", null));

        var result = await _exchangeService.RecordOpeningFloatAsync(CompanyId.Value, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Record closing float (end of day)
    /// </summary>
    [HttpPost("daily/closing")]
    public async Task<IActionResult> RecordClosingFloat([FromBody] ClosingFloatDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();

        if (dto.KesCount < 0 || dto.UsdCount < 0)
            return BadRequest(new ApiResponse<object>(false, "Counts cannot be negative", null));

        var result = await _exchangeService.RecordClosingFloatAsync(CompanyId.Value, UserId.Value, dto);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Get daily summaries for a date range
    /// </summary>
    [HttpGet("daily/summaries")]
    public async Task<IActionResult> GetDailySummaries([FromQuery] DateTime from, [FromQuery] DateTime to)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.GetDailySummariesAsync(CompanyId.Value, from, to);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    // ==================== REPORTS ====================

    /// <summary>
    /// Get profit report for a date range
    /// </summary>
    [HttpGet("reports/profit")]
    public async Task<IActionResult> GetProfitReport([FromQuery] DateTime from, [FromQuery] DateTime to)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.GetProfitReportAsync(CompanyId.Value, from, to);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Get large transactions report (for compliance)
    /// </summary>
    [HttpGet("reports/large-transactions")]
    public async Task<IActionResult> GetLargeTransactions(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        [FromQuery] decimal threshold = 500000)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.GetLargeTransactionsAsync(CompanyId.Value, from, to, threshold);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Get exchange history for a specific client
    /// </summary>
    [HttpGet("reports/client/{clientId}")]
    public async Task<IActionResult> GetClientExchangeHistory(Guid clientId)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.GetClientExchangeHistoryAsync(CompanyId.Value, clientId);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Get USD position (inventory value and unrealized P&L)
    /// </summary>
    [HttpGet("position/usd")]
    public async Task<IActionResult> GetUsdPosition()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.GetUsdPositionAsync(CompanyId.Value);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    // ==================== ALERTS ====================

    /// <summary>
    /// Get active float alerts
    /// </summary>
    [HttpGet("alerts")]
    public async Task<IActionResult> GetAlerts()
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.GetAlertsAsync(CompanyId.Value);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Update alert thresholds
    /// </summary>
    [HttpPut("alerts/thresholds")]
    public async Task<IActionResult> UpdateAlertThresholds([FromBody] UpdateAlertThresholdsDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _exchangeService.UpdateAlertThresholdsAsync(
            CompanyId.Value, dto.LowKesThreshold, dto.LowUsdThreshold, dto.LargeTransactionThreshold);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    // ==================== CALCULATOR ====================

    /// <summary>
    /// Calculate exchange amount without creating transaction
    /// </summary>
    [HttpPost("calculate")]
    public async Task<IActionResult> Calculate([FromBody] CalculateExchangeDto dto)
    {
        if (!CompanyId.HasValue) return Unauthorized();

        var rateResult = await _exchangeService.GetCurrentRateAsync(CompanyId.Value);
        if (!rateResult.Success || rateResult.Data == null)
            return BadRequest(new ApiResponse<object>(false, "No active exchange rate", null));

        var rate = dto.Direction == ExchangeDirection.UsdToKes
            ? rateResult.Data.BuyRate
            : rateResult.Data.SellRate;

        var spread = rateResult.Data.SellRate - rateResult.Data.BuyRate;

        decimal result;
        decimal profit;

        if (dto.Direction == ExchangeDirection.UsdToKes)
        {
            result = dto.Amount * rate;
            profit = spread * dto.Amount;
        }
        else
        {
            result = dto.Amount / rate;
            profit = spread * result;
        }

        return Ok(new ApiResponse<CalculationResultDto>(true, "Success", new CalculationResultDto(
            dto.Amount,
            dto.Direction == ExchangeDirection.UsdToKes ? Currency.USD : Currency.KES,
            result,
            dto.Direction == ExchangeDirection.UsdToKes ? Currency.KES : Currency.USD,
            rate,
            profit
        )));
    }
}
// ==================== TRUSTED DEVICE CONTROLLER ====================
[ApiController]
[Route("api/trusted-devices")]
[Authorize]
public class TrustedDeviceController : BaseApiController
{
    private readonly AppDbContext _context;
    
    public TrustedDeviceController(AppDbContext context) => _context = context;

    [HttpGet]
    public async Task<IActionResult> GetMyDevices()
    {
        if (!UserId.HasValue) return Unauthorized();
        var devices = await _context.TrustedDevices
            .Where(d => d.UserId == UserId.Value && !d.IsDeleted && d.TrustedUntil > DateTime.UtcNow)
            .Select(d => new { d.Id, d.DeviceId, d.Platform, d.DeviceName, d.LastUsedAt, d.TrustedUntil, d.CreatedAt })
            .OrderByDescending(d => d.LastUsedAt)
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, "Trusted devices", devices));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> RemoveDevice(Guid id)
    {
        if (!UserId.HasValue) return Unauthorized();
        var device = await _context.TrustedDevices
            .FirstOrDefaultAsync(d => d.Id == id && d.UserId == UserId.Value);
        if (device == null) return NotFound();
        _context.TrustedDevices.Remove(device);
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool>(true, "Device removed", true));
    }

    [HttpDelete]
    public async Task<IActionResult> RemoveAllDevices()
    {
        if (!UserId.HasValue) return Unauthorized();
        var devices = await _context.TrustedDevices.Where(d => d.UserId == UserId.Value).ToListAsync();
        _context.TrustedDevices.RemoveRange(devices);
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool>(true, "All devices removed", true));
    }
}

// ==================== BALANCE ALERT CONTROLLER ====================
[ApiController]
[Route("api/balance-alerts")]
[Authorize(Policy = "AdminOrOffice")]  // M5 FIX: Clients cannot manage alert rules
public class BalanceAlertController : BaseApiController
{
    private readonly IBalanceAlertService _alertService;
    
    public BalanceAlertController(IBalanceAlertService alertService) => _alertService = alertService;

    [HttpGet]
    public async Task<IActionResult> GetRules([FromQuery] Guid? clientId = null)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _alertService.GetRulesAsync(CompanyId.Value, clientId);
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> CreateRule([FromBody] CreateBalanceAlertDto dto)
    {
        if (!CompanyId.HasValue || !UserId.HasValue) return Unauthorized();
        var result = await _alertService.CreateRuleAsync(CompanyId.Value, UserId.Value, dto);
        return Ok(result);
    }

    [HttpPut("{id:guid}/toggle")]
    public async Task<IActionResult> ToggleRule(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _alertService.ToggleRuleAsync(CompanyId.Value, id);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteRule(Guid id)
    {
        if (!CompanyId.HasValue) return Unauthorized();
        var result = await _alertService.DeleteRuleAsync(CompanyId.Value, id);
        return Ok(result);
    }
}

// ==================== TRANSACTION PIN CONTROLLER ====================
[ApiController]
[Route("api/transaction-pin")]
[Authorize(Policy = "AdminOrOffice")]  // H1 FIX: Clients cannot access PIN endpoints
public class TransactionPinController : BaseApiController
{
    private readonly AppDbContext _context;
    
    public TransactionPinController(AppDbContext context) => _context = context;

    // Resolve companyId: from JWT for OfficeUser, from query for SuperAdmin
    private Guid? ResolveCompanyId(Guid? queryCompanyId) =>
        CompanyId ?? queryCompanyId;

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus([FromQuery] Guid? companyId = null)
    {
        var cid = ResolveCompanyId(companyId);
        if (!cid.HasValue) return Unauthorized();
        var company = await _context.Companies.FindAsync(cid.Value);
        if (company == null) return NotFound();
        return Ok(new ApiResponse<TransactionPinStatusDto>(true, "PIN status", 
            new TransactionPinStatusDto(company.IsTransactionPinEnabled, !string.IsNullOrEmpty(company.TransactionPinHash))));
    }

    [HttpPost("set")]
    public async Task<IActionResult> SetPin([FromBody] SetTransactionPinDto dto)
    {
        var cid = CompanyId ?? dto.CompanyId;
        if (!cid.HasValue) return Ok(new ApiResponse<bool>(false, "Company not specified", false));
        if (string.IsNullOrEmpty(dto.Pin) || dto.Pin.Length != 4 || !dto.Pin.All(char.IsDigit))
            return Ok(new ApiResponse<bool>(false, "PIN must be exactly 4 digits", false));
        
        var company = await _context.Companies.FindAsync(cid.Value);
        if (company == null) return NotFound();
        
        company.TransactionPinHash = BCrypt.Net.BCrypt.HashPassword(dto.Pin);
        company.IsTransactionPinEnabled = true;
        company.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        
        return Ok(new ApiResponse<bool>(true, "Transaction PIN set successfully", true));
    }

    [HttpPost("verify")]
    public async Task<IActionResult> VerifyPin([FromBody] VerifyTransactionPinDto dto, [FromQuery] Guid? companyId = null)
    {
        var cid = ResolveCompanyId(companyId);
        if (!cid.HasValue) return Unauthorized();
        var company = await _context.Companies.FindAsync(cid.Value);
        if (company == null) return NotFound();
        
        if (!company.IsTransactionPinEnabled || string.IsNullOrEmpty(company.TransactionPinHash))
            return Ok(new ApiResponse<bool>(true, "PIN not required", true));
        
        var valid = BCrypt.Net.BCrypt.Verify(dto.Pin, company.TransactionPinHash);
        return Ok(valid 
            ? new ApiResponse<bool>(true, "PIN verified", true)
            : new ApiResponse<bool>(false, "Incorrect PIN", false));
    }

    [HttpPost("toggle")]
    public async Task<IActionResult> TogglePin([FromQuery] Guid? companyId = null)
    {
        var cid = ResolveCompanyId(companyId);
        if (!cid.HasValue) return Unauthorized();
        var company = await _context.Companies.FindAsync(cid.Value);
        if (company == null) return NotFound();
        
        if (string.IsNullOrEmpty(company.TransactionPinHash))
            return BadRequest(new ApiResponse<bool>(false, "Set a PIN first before enabling", false));
        
        company.IsTransactionPinEnabled = !company.IsTransactionPinEnabled;
        company.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        
        return Ok(new ApiResponse<bool>(true, 
            company.IsTransactionPinEnabled ? "PIN requirement enabled" : "PIN requirement disabled", true));
    }

    [HttpDelete]
    public async Task<IActionResult> RemovePin([FromQuery] Guid? companyId = null)
    {
        var cid = ResolveCompanyId(companyId);
        if (!cid.HasValue) return Unauthorized();
        var company = await _context.Companies.FindAsync(cid.Value);
        if (company == null) return NotFound();
        
        company.TransactionPinHash = null;
        company.IsTransactionPinEnabled = false;
        company.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        
        return Ok(new ApiResponse<bool>(true, "Transaction PIN removed", true));
    }
}

// ==================== C3 FIX: DEVICE TOKEN CONTROLLER ====================
[ApiController]
[Route("api/device-token")]
[Authorize]
public class DeviceTokenController : BaseApiController
{
    private readonly AppDbContext _context;
    public DeviceTokenController(AppDbContext context) => _context = context;

    [HttpPost]
    public async Task<IActionResult> Register([FromBody] RegisterTokenDto dto)
    {
        var userId = UserId;
        if (!userId.HasValue) return Unauthorized();

        // Upsert: update if token exists, create if not
        var existing = await _context.DeviceTokens
            .FirstOrDefaultAsync(d => d.Token == dto.Token && d.UserId == userId);

        if (existing != null)
        {
            existing.LastUsedAt = DateTime.UtcNow;
            existing.Platform = dto.Platform ?? existing.Platform;
            existing.DeviceName = dto.DeviceName ?? existing.DeviceName;
            existing.IsActive = true;
        }
        else
        {
            _context.DeviceTokens.Add(new DeviceToken
            {
                UserId = userId,
                CompanyId = CompanyId,
                Token = dto.Token,
                Platform = dto.Platform ?? "unknown",
                DeviceName = dto.DeviceName,
            });
        }

        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, "Token registered"));
    }

    [HttpDelete]
    public async Task<IActionResult> Unregister([FromBody] UnregisterTokenDto dto)
    {
        var userId = UserId;
        if (!userId.HasValue) return Unauthorized();

        var existing = await _context.DeviceTokens
            .FirstOrDefaultAsync(d => d.Token == dto.Token && d.UserId == userId);

        if (existing != null)
        {
            existing.IsActive = false;
            await _context.SaveChangesAsync();
        }

        return Ok(new ApiResponse<object>(true, "Token unregistered"));
    }
}

public record RegisterTokenDto(string Token, string? Platform, string? DeviceName);
public record UnregisterTokenDto(string Token);