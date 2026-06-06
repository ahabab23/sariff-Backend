// // using Microsoft.AspNetCore.Authorization;
// // using Microsoft.AspNetCore.SignalR;
// //
// // namespace SARIFF.API.Hubs;
// //
// // [Authorize]
// // public class NotificationHub : Hub
// // {
// //     public override async Task OnConnectedAsync()
// //     {
// //         var companyId = Context.User?.FindFirst("company_id")?.Value;
// //         if (!string.IsNullOrEmpty(companyId))
// //         {
// //             await Groups.AddToGroupAsync(Context.ConnectionId, $"company_{companyId}");
// //         }
// //         await base.OnConnectedAsync();
// //     }
// //
// //     public override async Task OnDisconnectedAsync(Exception? exception)
// //     {
// //         var companyId = Context.User?.FindFirst("company_id")?.Value;
// //         if (!string.IsNullOrEmpty(companyId))
// //         {
// //             await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"company_{companyId}");
// //         }
// //         await base.OnDisconnectedAsync(exception);
// //     }
// //
// //     public async Task JoinCompanyGroup(string companyId)
// //     {
// //         await Groups.AddToGroupAsync(Context.ConnectionId, $"company_{companyId}");
// //     }
// // }
// using Microsoft.AspNetCore.Authorization;
// using Microsoft.AspNetCore.SignalR;
//
// namespace SARIFF.API.Hubs;
//
// [Authorize]
// public class NotificationHub : Hub
// {
//     public override async Task OnConnectedAsync()
//     {
//         var companyId = Context.User?.FindFirst("company_id")?.Value;
//         if (!string.IsNullOrEmpty(companyId))
//         {
//             await Groups.AddToGroupAsync(Context.ConnectionId, $"company_{companyId}");
//         }
//         await base.OnConnectedAsync();
//     }
//
//     public override async Task OnDisconnectedAsync(Exception? exception)
//     {
//         var companyId = Context.User?.FindFirst("company_id")?.Value;
//         if (!string.IsNullOrEmpty(companyId))
//         {
//             await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"company_{companyId}");
//         }
//         await base.OnDisconnectedAsync(exception);
//     }
//
//     public async Task JoinCompanyGroup(string companyId)
//     {
//         await Groups.AddToGroupAsync(Context.ConnectionId, $"company_{companyId}");
//     }
// }
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace SARIFF.API.Hubs;

[Authorize]
public class NotificationHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var companyId = Context.User?.FindFirst("company_id")?.Value;
        if (!string.IsNullOrEmpty(companyId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"company_{companyId}");
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var companyId = Context.User?.FindFirst("company_id")?.Value;
        if (!string.IsNullOrEmpty(companyId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"company_{companyId}");
        }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinCompanyGroup(string companyId)
    {
        // SECURITY: Only allow joining your own company's group
        var userCompanyId = Context.User?.FindFirst("company_id")?.Value;
        if (string.IsNullOrEmpty(userCompanyId) || userCompanyId != companyId)
            return; // Silently reject — don't leak that the group exists
        
        await Groups.AddToGroupAsync(Context.ConnectionId, $"company_{companyId}");
    }
}