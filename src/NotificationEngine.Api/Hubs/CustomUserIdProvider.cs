using Microsoft.AspNetCore.SignalR;

namespace NotificationEngine.Api.Hubs;

public class UserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connectionContext)
    {
        return connectionContext.User?.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value
            ?? connectionContext.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    }
}
