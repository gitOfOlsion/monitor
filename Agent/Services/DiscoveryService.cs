using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Agent.Services;

public class DiscoveryService : IDisposable
{
    private UdpClient?                      _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<DiscoveryService> _logger;
    private const string Group = "224.0.0.251";
    private const int    Port  = 5001;

    public DiscoveryService(ILogger<DiscoveryService> logger) => _logger = logger;

    public void StartBroadcasting(
        string machineName, string macAddress,
        AgentPlatform platform, string jwtToken)
    {
        try
        {
            _udp = new UdpClient { EnableBroadcast = true };
            _udp.JoinMulticastGroup(IPAddress.Parse(Group));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UDP multicast unavailable — discovery disabled");
            return;
        }

        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var payload = new
                    {
                        MachineName = machineName,
                        IPAddress   = NetworkHelpers.GetLocalIp(),
                        MacAddress  = macAddress,
                        Platform    = platform,
                        Timestamp   = DateTime.UtcNow,
                        JwtToken    = jwtToken   // server uses this to resolve institution
                    };
                    var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                    await _udp.SendAsync(data, data.Length, Group, Port);
                    _logger.LogDebug("Discovery broadcast sent");
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Broadcast error"); }

                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
            }
        }, _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp?.DropMulticastGroup(IPAddress.Parse(Group)); } catch { }
        _udp?.Dispose();
        _cts.Dispose();
    }
}
