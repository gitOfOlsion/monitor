using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PeripheralMonitor.Server.Services;
using PeripheralMonitor.Shared.Models;
using PeripheralMonitor.Shared.Security;

namespace PeripheralMonitor.Server.Services;

/// <summary>
/// Listens for UDP multicast discovery broadcasts.
/// Extracts institution_id from the agent's JWT in the broadcast (if included),
/// or falls back to looking it up from MAC address if already registered.
/// </summary>
public class DiscoveryHostedService : BackgroundService
{
    private readonly AgentService  _agents;
    private readonly ILogger<DiscoveryHostedService> _logger;
    private readonly IConfiguration _config;
    private const string Group = "224.0.0.251";
    private const int    Port  = 5001;

    public DiscoveryHostedService(
        AgentService agents, IConfiguration config,
        ILogger<DiscoveryHostedService> logger)
    {
        _agents = agents;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(Port);
            udp.JoinMulticastGroup(IPAddress.Parse(Group));
            _logger.LogInformation("UDP discovery listener on port {Port}", Port);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UDP listener could not bind to port {Port}", Port);
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result    = await udp.ReceiveAsync(ct);
                var json      = Encoding.UTF8.GetString(result.Buffer);

                // Try to parse as DiscoveryBroadcastWithToken (includes JWT)
                var broadcast = JsonSerializer.Deserialize<DiscoveryBroadcastWithToken>(json);
                if (broadcast == null) continue;

                // Extract institution_id from JWT
                Guid? institutionId = null;
                if (!string.IsNullOrEmpty(broadcast.JwtToken))
                {
                    var secret = _config["Jwt:Secret"] ?? "";
                    var claims = JwtHelper.ValidateToken(broadcast.JwtToken, secret);
                    var instStr = claims?.FindFirst("institution_id")?.Value;
                    if (Guid.TryParse(instStr, out var gid)) institutionId = gid;
                }

                if (institutionId == null)
                {
                    // Agent broadcasting without token — check if already known
                    var existing = await _agents.GetByMacAsync(broadcast.MacAddress);
                    if (existing != null) institutionId = existing.InstitutionId;
                }

                if (institutionId == null)
                {
                    _logger.LogDebug("Discovery broadcast from unknown agent {Mac} — no institution resolved", broadcast.MacAddress);
                    continue;
                }

                var payload = new DiscoveryBroadcast(
                    broadcast.MachineName, broadcast.IPAddress,
                    broadcast.MacAddress, broadcast.Platform, broadcast.Timestamp);

                await _agents.UpsertFromDiscoveryAsync(payload, institutionId.Value);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Discovery receive error"); }
        }

        try { udp.DropMulticastGroup(IPAddress.Parse(Group)); } catch { }
        udp.Dispose();
    }
}

// Extended broadcast payload that includes the JWT token
public record DiscoveryBroadcastWithToken(
    string        MachineName,
    string        IPAddress,
    string        MacAddress,
    AgentPlatform Platform,
    DateTime      Timestamp,
    string?       JwtToken = null
);

/// <summary>Marks agents offline when heartbeat stops.</summary>
public class OfflineDetectorService : BackgroundService
{
    private readonly AgentService _agents;
    private readonly ILogger<OfflineDetectorService> _logger;

    public OfflineDetectorService(AgentService agents, ILogger<OfflineDetectorService> logger)
    {
        _agents = agents;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _agents.MarkStaleAgentsOfflineAsync(TimeSpan.FromSeconds(60)); }
            catch (Exception ex) { _logger.LogWarning(ex, "OfflineDetector error"); }
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
