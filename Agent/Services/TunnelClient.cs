using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR.Client;

namespace PeripheralMonitor.Agent.Services;

/// <summary>
/// Agent-side half of the SignalR remote-desktop tunnel. When the server requests a
/// tunnel (because an admin clicked "Remote Desktop" and the agent has no inbound
/// network access), this connects to the agent's own local RDP (3389) or VNC (5900)
/// port and relays raw bytes both directions through the existing SignalR connection.
///
/// One active tunnel at a time, matching the server-side TunnelManager's one-session-per-agent design.
/// </summary>
public class TunnelClient
{
    private readonly ILogger<TunnelClient> _log;
    private TcpClient? _localConn;
    private NetworkStream? _localStream;
    private CancellationTokenSource? _cts;
    private string? _activeSessionId;

    public TunnelClient(ILogger<TunnelClient> log) => _log = log;

    public async Task StartAsync(HubConnection hub, string sessionId, int targetPort)
    {
        // Tear down any previous tunnel first - only one at a time.
        Stop();

        _activeSessionId = sessionId;
        _cts = new CancellationTokenSource();

        try
        {
            _localConn = new TcpClient();
            await _localConn.ConnectAsync("127.0.0.1", targetPort, _cts.Token);
            _localStream = _localConn.GetStream();
            _log.LogInformation("Tunnel {Session} connected to local port {Port}", sessionId, targetPort);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Tunnel {Session} failed to connect to local port {Port} (is RDP/VNC running?)", sessionId, targetPort);
            Stop();
            return;
        }

        // Signal the server that the local RDP connection is live and we're ready to relay.
        // The server waits for this before handing the tunnel port to Guacamole, ensuring
        // guacd never connects to an empty stream.
        try { await hub.SendAsync("TunnelReady", sessionId); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to send TunnelReady for {Session}", sessionId); }

        _ = RelayLoopAsync(hub, sessionId, _cts.Token);
    }

    private async Task RelayLoopAsync(HubConnection hub, string sessionId, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _localStream != null)
            {
                var read = await _localStream.ReadAsync(buffer, ct);
                if (read == 0) break; // local RDP/VNC closed the connection

                var chunk = buffer[..read];
                await hub.SendAsync("TunnelDataFromAgent", sessionId, chunk, ct);
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Tunnel {Session} relay loop ended", sessionId);
        }
        finally
        {
            Stop();
        }
    }

    /// <summary>Called when the server forwards bytes from Guacamole that need to go to the local RDP/VNC port.</summary>
    public async Task OnServerDataAsync(string sessionId, byte[] data)
    {
        if (sessionId != _activeSessionId || _localStream == null) return;
        try
        {
            await _localStream.WriteAsync(data);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Tunnel {Session} failed writing to local connection", sessionId);
            Stop();
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _localStream?.Dispose();
        _localConn?.Dispose();
        _localStream = null;
        _localConn = null;
        _activeSessionId = null;
    }
}
