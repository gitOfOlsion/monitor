using System.Collections.Concurrent;

namespace PeripheralMonitor.Server.Services;

/// <summary>
/// In-memory registry of live agent SignalR connections.
/// Maps AgentEntity.Id -> Hub ConnectionId, so the server can push commands
/// to a specific agent. Also tracks pending request/response correlation
/// for commands, file pulls, and folder listings (all keyed by CommandId).
/// </summary>
public class AgentConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, string> _agentToConnection = new();
    private readonly ConcurrentDictionary<string, Guid> _connectionToAgent = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<object>> _pending = new();

    public void Register(Guid agentId, string connectionId)
    {
        _agentToConnection[agentId] = connectionId;
        _connectionToAgent[connectionId] = agentId;
    }

    public void Unregister(string connectionId)
    {
        if (_connectionToAgent.TryRemove(connectionId, out var agentId))
            _agentToConnection.TryRemove(agentId, out _);
    }

    public string? GetConnectionId(Guid agentId) =>
        _agentToConnection.TryGetValue(agentId, out var c) ? c : null;

    public Guid? GetAgentIdForConnection(string connectionId) =>
        _connectionToAgent.TryGetValue(connectionId, out var id) ? id : null;

    public bool IsOnline(Guid agentId) => _agentToConnection.ContainsKey(agentId);

    /// <summary>
    /// Register a pending request and return a task that completes when the agent
    /// reports a result of type <typeparamref name="T"/> with a matching CommandId.
    /// <paramref name="onTimeout"/> builds the fallback result if the agent never responds.
    /// </summary>
    public Task<T> WaitForResult<T>(string commandId, TimeSpan timeout, Func<T> onTimeout)
    {
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[commandId] = tcs;

        var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() =>
        {
            if (_pending.TryRemove(commandId, out var t))
                t.TrySetResult(onTimeout()!);
            cts.Dispose();
        });

        return tcs.Task.ContinueWith(t => (T)t.Result);
    }

    public void CompleteResult(string commandId, object result)
    {
        if (_pending.TryRemove(commandId, out var tcs))
            tcs.TrySetResult(result);
    }
}
