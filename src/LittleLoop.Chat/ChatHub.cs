using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

sealed class ChatConnections
{
    private readonly ConcurrentDictionary<string, (string Device, Action Abort)> peers = new();
    public void Add(HubCallerContext context, string device) => peers[context.ConnectionId] = (device, context.Abort);
    public void Remove(string connection) => peers.TryRemove(connection, out _);
    public void Abort(string[] devices)
    {
        var revoked = devices.ToHashSet(StringComparer.Ordinal);
        foreach (var (_, peer) in peers)
            if (revoked.Contains(peer.Device))
                try { peer.Abort(); }
                catch (ObjectDisposedException) { }
    }
}

// Recipients are assigned by stored membership. Connections cannot subscribe to arbitrary rooms.
sealed class ChatHub(ChatStore store, ChatConnections connections) : Hub
{
    public override async Task OnConnectedAsync()
    {
        string? user = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        string? device = Context.User?.FindFirstValue(ChatSettingsEndpoints.DeviceClaim);
        if (device is null) { Context.Abort(); return; }
        connections.Add(Context, device);
        if (!await store.ActiveDevice(user, device)) { connections.Remove(Context.ConnectionId); Context.Abort(); return; }
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
