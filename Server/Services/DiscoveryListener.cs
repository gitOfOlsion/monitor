using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Server.Services;

public class DiscoveryListener : IDisposable
{
    private readonly UdpClient _udpClient;
    private readonly AgentRegistry _registry;
    private readonly ILogger<DiscoveryListener> _logger;
    private readonly CancellationTokenSource _cts = new();
    private const string DiscoveryGroup = "224.0.0.251";
    private const int DiscoveryPort = 5001;

    public DiscoveryListener(AgentRegistry registry, ILogger<DiscoveryListener> logger)
    {
        _registry = registry;
        _logger = logger;
        _udpClient = new UdpClient(DiscoveryPort);
        _udpClient.JoinMulticastGroup(IPAddress.Parse(DiscoveryGroup));
    }

    public void Start()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync();
                    var json = Encoding.UTF8.GetString(result.Buffer);
                    var broadcast = JsonSerializer.Deserialize<DiscoveryBroadcast>(json);
                    
                    if (broadcast != null && !_registry.TryGetAgent(broadcast.MacAddress, out _))
                    {
                        var agent = new AgentRegistration(
                            broadcast.MachineName, broadcast.IPAddress, broadcast.MacAddress,
                            DateTime.UtcNow, AgentStatus.Pending, null, null, null
                        );
                        _registry.AddOrUpdate(agent);
                        _logger.LogInformation("🔍 New agent discovered: {Machine} ({Mac})", broadcast.MachineName, broadcast.MacAddress);
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Discovery receive error"); }
            }
        }, _cts.Token);
    }

    public async Task SendApprovalResponse(string ipAddress, string macAddress, string jwtToken)
    {
        try
        {
            var response = JsonSerializer.Serialize(new { approved = true, token = jwtToken });
            var data = Encoding.UTF8.GetBytes(response);
            await _udpClient.SendAsync(data, data.Length, ipAddress, DiscoveryPort);
            _logger.LogDebug("✅ Approval sent to {IP}", ipAddress);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to send approval"); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udpClient?.DropMulticastGroup(IPAddress.Parse(DiscoveryGroup));
        _udpClient?.Dispose();
        _cts.Dispose();
    }
}