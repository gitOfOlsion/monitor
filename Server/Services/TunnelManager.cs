using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR;
using PeripheralMonitor.Server.Hubs;

namespace PeripheralMonitor.Server.Services;

/// <summary>
/// Bridges Guacamole (on this VPS) to an agent's local RDP/VNC port via SignalR tunnel.
///
/// CRITICAL ORDERING: guacd must NOT connect to the tunnel port until the agent has
/// connected to its local RDP port and is actively relaying — otherwise guacd reads
/// an empty stream and immediately reports "wrong security type" (it never saw an
/// RDP handshake response because the agent wasn't ready yet).
///
/// Flow:
///   1. Server opens a local TCP listener
///   2. Sends "StartTunnel" to agent
///   3. Agent connects to its own localhost:3389, then sends "TunnelReady" back to server
///   4. ONLY THEN does StartTunnelAsync return the local port to the controller
///   5. Controller builds the Guacamole token pointing at that port
///   6. Guacamole/guacd connects and finds a live RDP handshake waiting to respond
/// </summary>
public class TunnelManager
{
    private readonly IHubContext<MonitorHub> _hub;
    private readonly AgentConnectionRegistry _connections;
    private readonly ILogger<TunnelManager> _log;

    private readonly ConcurrentDictionary<Guid, TunnelSession> _sessions = new();

    public TunnelManager(IHubContext<MonitorHub> hub, AgentConnectionRegistry connections, ILogger<TunnelManager> log)
    {
        _hub = hub; _connections = connections; _log = log;
    }

    /// <summary>
    /// Starts a tunnel and WAITS for the agent to signal it is ready before returning.
    /// This ensures guacd always finds a live RDP stream on the port, not an empty one.
    /// Returns the local port Guacamole should connect to, or null on timeout/offline.
    /// </summary>
    public async Task<int?> StartTunnelAsync(Guid agentId, int targetPort, TimeSpan idleTimeout)
    {
        var connId = _connections.GetConnectionId(agentId);
        if (connId == null) return null;

        await StopTunnelAsync(agentId);

        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Completed by OnAgentReady() when agent signals its local RDP connection is live.
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new TunnelSession(agentId, listener, idleTimeout, readyTcs);
        _sessions[agentId] = session;

        var sessionId = Guid.NewGuid().ToString("N");
        session.SessionId = sessionId;

        await _hub.Clients.Client(connId).SendAsync("StartTunnel", sessionId, targetPort);

        // Wait up to 10 seconds for the agent to connect to its local RDP port and signal ready.
        var completed = await Task.WhenAny(readyTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (completed != readyTcs.Task || !readyTcs.Task.Result)
        {
            _log.LogWarning("Tunnel ready-timeout for agent {Agent} — agent did not connect to local port {Port} within 10s", agentId, targetPort);
            await StopTunnelAsync(agentId);
            return null;
        }

        _ = AcceptLoopAsync(session);

        _log.LogInformation("Tunnel started for agent {Agent} -> local port {Port} -> target {TargetPort}", agentId, localPort, targetPort);
        return localPort;
    }

    /// <summary>Called by the hub when the agent signals its local RDP connection is live and ready to relay.</summary>
    public void OnAgentReady(Guid agentId)
    {
        if (_sessions.TryGetValue(agentId, out var session))
        {
            _log.LogInformation("Tunnel agent-ready for {Agent} — local RDP connected, returning port to Guacamole", agentId);
            session.ReadyTcs.TrySetResult(true);
        }
    }

    public Task StopTunnelAsync(Guid agentId)
    {
        if (_sessions.TryRemove(agentId, out var session))
        {
            session.Dispose();
            var connId = _connections.GetConnectionId(agentId);
            if (connId != null)
                _ = _hub.Clients.Client(connId).SendAsync("StopTunnel", session.SessionId);
            _log.LogInformation("Tunnel stopped for agent {Agent}", agentId);
        }
        return Task.CompletedTask;
    }

    /// <summary>Called by the hub when the agent sends tunnel data (RDP server -> Guacamole direction).</summary>
    public async Task OnAgentDataAsync(Guid agentId, byte[] data)
    {
        if (_sessions.TryGetValue(agentId, out var session) && session.ClientStream != null)
        {
            try
            {
                await session.ClientStream.WriteAsync(data);
                session.Touch();
            }
            catch
            {
                await StopTunnelAsync(agentId);
            }
        }
    }

    private async Task AcceptLoopAsync(TunnelSession session)
    {
        try
        {
            using var client = await session.Listener.AcceptTcpClientAsync(session.Cts.Token);
            session.ClientStream = client.GetStream();

            var connId = _connections.GetConnectionId(session.AgentId);
            var buffer = new byte[16 * 1024];

            while (!session.Cts.IsCancellationRequested)
            {
                var read = await session.ClientStream.ReadAsync(buffer, session.Cts.Token);
                if (read == 0) break;
                session.Touch();

                if (connId != null)
                {
                    var chunk = buffer[..read];
                    await _hub.Clients.Client(connId).SendAsync("TunnelData", session.SessionId, chunk);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Tunnel accept loop ended for agent {Agent}", session.AgentId);
        }
        finally
        {
            await StopTunnelAsync(session.AgentId);
        }
    }

    private class TunnelSession : IDisposable
    {
        public Guid AgentId { get; }
        public TcpListener Listener { get; }
        public NetworkStream? ClientStream { get; set; }
        public string SessionId { get; set; } = "";
        public TaskCompletionSource<bool> ReadyTcs { get; }
        public CancellationTokenSource Cts { get; } = new();
        private DateTime _lastActivity = DateTime.UtcNow;
        private readonly Timer _idleTimer;

        public TunnelSession(Guid agentId, TcpListener listener, TimeSpan idleTimeout, TaskCompletionSource<bool> readyTcs)
        {
            AgentId = agentId;
            Listener = listener;
            ReadyTcs = readyTcs;
            _idleTimer = new Timer(_ =>
            {
                if (DateTime.UtcNow - _lastActivity > idleTimeout)
                    Cts.Cancel();
            }, null, idleTimeout, idleTimeout);
        }

        public void Touch() => _lastActivity = DateTime.UtcNow;

        public void Dispose()
        {
            ReadyTcs.TrySetResult(false);
            Cts.Cancel();
            _idleTimer.Dispose();
            ClientStream?.Dispose();
            try { Listener.Stop(); } catch { }
        }
    }
}
