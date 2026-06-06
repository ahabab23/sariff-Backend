using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SARIFF.Core.Interfaces;
using SARIFF.Infrastructure.Data;

namespace SARIFF.Infrastructure.Services;

/// <summary>
/// Sends push notifications to registered devices via the Expo Push API.
/// Self-contained and fail-safe: any error is logged and swallowed so a push
/// failure can NEVER break the transaction/exchange flow that triggered it.
/// No Firebase Admin SDK or service-account file required — Expo relays to FCM/APNs.
/// </summary>
public class PushNotificationService : IPushNotificationService
{
    private const string ExpoEndpoint = "https://exp.host/--/api/v2/push/send";

    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PushNotificationService> _logger;

    public PushNotificationService(
        AppDbContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<PushNotificationService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task SendToUserAsync(Guid userId, string title, string body, Dictionary<string, string>? data = null)
    {
        try
        {
            var tokens = await _context.DeviceTokens
                .Where(d => d.UserId == userId && d.IsActive && !d.IsDeleted)
                .Select(d => d.Token)
                .ToListAsync();

            await SendToTokensAsync(tokens, title, body, data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Push notification to user {UserId} failed (non-fatal)", userId);
        }
    }

    public async Task SendToTokensAsync(IEnumerable<string> tokens, string title, string body, Dictionary<string, string>? data = null)
    {
        try
        {
            // Only valid Expo push tokens, de-duplicated.
            var valid = tokens
                .Where(t => !string.IsNullOrWhiteSpace(t) &&
                            (t.StartsWith("ExponentPushToken[") || t.StartsWith("ExpoPushToken[")))
                .Distinct()
                .ToList();

            if (valid.Count == 0)
            {
                _logger.LogDebug("No valid Expo push tokens to notify");
                return;
            }

            var client = _httpClientFactory.CreateClient("ExpoPush");

            // Expo accepts up to 100 messages per request.
            foreach (var batch in Chunk(valid, 100))
            {
                var messages = batch.Select(token => new
                {
                    to = token,
                    title,
                    body,
                    sound = "default",
                    channelId = "transactions",
                    data = data ?? new Dictionary<string, string>()
                }).ToList();

                using var response = await client.PostAsJsonAsync(ExpoEndpoint, messages);
                if (!response.IsSuccessStatusCode)
                {
                    var payload = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Expo push returned {Status}: {Payload}", (int)response.StatusCode, payload);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Push notification send failed (non-fatal)");
        }
    }

    private static IEnumerable<List<string>> Chunk(List<string> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }
}