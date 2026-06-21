using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR;
using PeripheralMonitor.Server.Hubs;

namespace PeripheralMonitor.Server.Services;

/// <summary>
/// Bridges Guacamole (on this VPS) to an agent's local RDP/VNC port, even when the
/// agent is behind NAT/firewall with no inbound access — by tunneling raw TCP bytes
/// through the agent's existing outbound SignalR connection.
///
/// Flow:
///   1. StartTunnelAsync opens a local TCP listener on 127.0.0.1:{port} (Guacamole connects here)
///   2. Sends "StartTunnel" to the agent via SignalR, telling it to connect to its own
///      127.0.0.1:3389 (or :5900 for VNC) and start relaying
///   3. Bytes arriving on the local listener are forwarded to the agent via "TunnelData"
///   4. Bytes the agent sends back (its RDP server's responses) arrive via "TunnelData"
///      from the agent and get written to the local TCP socket that Guacamole is reading
///
/// One tunnel per agent at a time (a second StartTunnelAsync call for the same agent
/// replaces/tears down the previous one).
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

    /// <summary>Starts a new tunnel for the given agent. Returns the local port Guacamole should connect to.</summary>
    public async Task<int?> StartTunnelAsync(Guid agentId, int targetPort, TimeSpan idleTimeout)
    {
        var connId = _connections.GetConnectionId(agentId);
        if (connId == null) return null; // agent offline

        // Tear down any existing tunnel for this agent first (one session at a time)
        await StopTunnelAsync(agentId);

        // Bind to all interfaces (not loopback) - guacd runs in a SEPARATE container and
        // reaches this listener via the "server" container's Docker-internal hostname,
        // so loopback-only binding would make it unreachable from outside this container.
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        var session = new TunnelSession(agentId, listener, idleTimeout);
        _sessions[agentId] = session;

        // Tell the agent to open its local RDP/VNC port and start relaying
        var sessionId = Guid.NewGuid().ToString("N");
        session.SessionId = sessionId;
        await _hub.Clients.Client(connId).SendAsync("StartTunnel", sessionId, targetPort);

        // Accept the (single) Guacamole connection on the local listener in the background
        _ = AcceptLoopAsync(session);

        _log.LogInformation("Tunnel started for agent {Agent} -> local port {Port} -> target {TargetPort}", agentId, localPort, targetPort);
        return localPort;
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

    /// <summary>Called by the hub when the agent sends tunnel data back (RDP server -> Guacamole direction).</summary>
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
                if (read == 0) break; // Guacamole closed the connection
                session.Touch();

                if (connId != null)
                {
                    var chunk = buffer[..read];
                    await _hub.Clients.Client(connId).SendAsync("TunnelData", session.SessionId, chunk);
                }
            }
        }
        catch (OperationCanceledException) { /* expected on teardown */ }
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
        public CancellationTokenSource Cts { get; } = new();
        private DateTime _lastActivity = DateTime.UtcNow;
        private readonly Timer _idleTimer;

        public TunnelSession(Guid agentId, TcpListener listener, TimeSpan idleTimeout)
        {
            AgentId = agentId;
            Listener = listener;
            _idleTimer = new Timer(_ =>
            {
                if (DateTime.UtcNow - _lastActivity > idleTimeout)
                    Cts.Cancel();
            }, null, idleTimeout, idleTimeout);
        }

        public void Touch() => _lastActivity = DateTime.UtcNow;

        public void Dispose()
        {
            Cts.Cancel();
            _idleTimer.Dispose();
            ClientStream?.Dispose();
            try { Listener.Stop(); } catch { /* already stopped */ }
        }
    }
}
