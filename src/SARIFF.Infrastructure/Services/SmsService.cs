using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SARIFF.Infrastructure.Services;

/// <summary>
/// SMS Service using Africa's Talking API
/// Sends OTP codes via SMS for login verification
/// </summary>
public interface ISmsService
{
    Task<bool> SendOtpAsync(string phoneNumber, string otp);
    Task<bool> SendSmsAsync(string phoneNumber, string message);
}

public class SmsService : ISmsService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SmsService> _logger;
    private readonly string _username;
    private readonly string _apiKey;
    private readonly string _senderId;
    private readonly bool _enabled;

    public SmsService(IConfiguration configuration, ILogger<SmsService> logger)
    {
        _httpClient = new HttpClient();
        _logger = logger;
        
        var smsConfig = configuration.GetSection("AfricasTalking");
        _username = smsConfig["Username"] ?? "sandbox";
        _apiKey = smsConfig["ApiKey"] ?? "";
        _senderId = smsConfig["SenderId"] ?? "SARIFF";
        _enabled = smsConfig.GetValue<bool>("Enabled", false);
    }

    public async Task<bool> SendOtpAsync(string phoneNumber, string otp)
    {
        var message = $"SARIFF: Your verification code is {otp}. Valid for 5 minutes. Do not share this code.";
        return await SendSmsAsync(phoneNumber, message);
    }

    public async Task<bool> SendSmsAsync(string phoneNumber, string message)
    {
        if (!_enabled || string.IsNullOrEmpty(_apiKey))
        {
            _logger.LogWarning("[SMS] Africa's Talking SMS is disabled. Message to {Phone}: {Message}", phoneNumber, message);
            return true; // Return true so login flow continues (dev mode)
        }

        try
        {
            // Format phone number for Africa's Talking (must be +254...)
            var formattedPhone = FormatPhoneNumber(phoneNumber);
            
            // Determine API URL based on environment
            var isSandbox = _username == "sandbox";
            var baseUrl = isSandbox 
                ? "https://api.sandbox.africastalking.com/version1/messaging"
                : "https://api.africastalking.com/version1/messaging";

            // Build form data — sandbox doesn't support custom SenderId
            var formData = new Dictionary<string, string>
            {
                { "username", _username },
                { "to", formattedPhone },
                { "message", message }
            };
            if (!isSandbox && !string.IsNullOrEmpty(_senderId))
            {
                formData.Add("from", _senderId);
            }

            var content = new FormUrlEncodedContent(formData);

            var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);
            request.Content = content;
            request.Headers.Add("apiKey", _apiKey);
            request.Headers.Add("Accept", "application/json");

            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            
            // Always log the response for debugging
            _logger.LogInformation("[SMS] Response: {Status} - {Body}", response.StatusCode, responseBody);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("[SMS] OTP sent successfully to {Phone}", formattedPhone);
                return true;
            }
            else
            {
                _logger.LogError("[SMS] Failed to send SMS to {Phone}. Status: {Status}. Response: {Response}", 
                    formattedPhone, response.StatusCode, responseBody);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SMS] Exception sending SMS to {Phone}", phoneNumber);
            return false;
        }
    }

    private static string FormatPhoneNumber(string phone)
    {
        var clean = phone.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");
        
        // Already has country code with +
        if (clean.StartsWith("+") && clean.Length >= 10) return clean;
        
        // Has country code without +  (e.g., 254..., 256..., 255...)
        if (!clean.StartsWith("0") && clean.Length >= 11) return "+" + clean;
        
        // Kenya local format (0712...) — 10 digits starting with 0
        if (clean.StartsWith("0") && clean.Length == 10) return "+254" + clean.Substring(1);
        
        // Kenya short format (712...) — 9 digits starting with 7
        if (clean.StartsWith("7") && clean.Length == 9) return "+254" + clean;
        
        // Uganda local (0772...) — handled by the 0+10 rule above
        // Tanzania local (0712...) — handled by the 0+10 rule above
        // NOTE: Local numbers without country code default to Kenya (+254)
        // To support other countries, enter numbers with country code (e.g., +256...)
        
        return clean.StartsWith("+") ? clean : "+" + clean;
    }
}