using ExpressRepair.Leads.Domain;
using Microsoft.AspNetCore.SignalR;

namespace ExpressRepair.Leads.Realtime;

/// <summary>
/// SignalR hub the leads page connects to. It has no methods: the server only pushes,
/// through <see cref="ICallNotifier"/>.
/// </summary>
public sealed class CallsHub : Hub;

/// <summary>Tells connected browsers that a call was created or changed status.</summary>
public interface ICallNotifier
{
    Task CallChangedAsync(Call call);
}

public sealed record CallChangedEvent(Guid Id, string Status, string? CustomerName, string? Appliance, string From, string? Error);

public sealed class SignalRCallNotifier(IHubContext<CallsHub> hub, ILogger<SignalRCallNotifier> logger) : ICallNotifier
{
    public const string EventName = "callChanged";

    public async Task CallChangedAsync(Call call)
    {
        var evt = new CallChangedEvent(call.Id, call.Status.ToString(), call.CustomerName, call.Appliance, call.From, call.Error);
        try
        {
            await hub.Clients.All.SendAsync(EventName, evt);
        }
        catch (Exception ex)
        {
            // A UI notification must never break the webhook or the processing pipeline.
            logger.LogWarning(ex, "Could not push update for call {CallId}", call.Id);
        }
    }
}
