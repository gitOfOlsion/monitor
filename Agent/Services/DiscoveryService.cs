using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Agent.Services;

public class DiscoveryService : IDisposable
{
    private UdpClient? _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<DiscoveryService> _log;
    private const string Group = "224.0.0.251";
    private const int Port = 5001;

    public DiscoveryService(ILogger<DiscoveryService> log) => _log = log;

    public void StartBroadcasting(string machineName, string mac, AgentPlatform platform, Guid institutionId)
    {
        try { _udp = new UdpClient { EnableBroadcast = true }; _udp.JoinMulticastGroup(IPAddress.Parse(Group)); }
        catch (Exception ex) { _log.LogWarning(ex, "UDP discovery disabled"); return; }

        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                        new DiscoveryBroadcast(machineName, GetIp(), mac, platform, institutionId, DateTime.UtcNow)));
                    await _udp.SendAsync(data, data.Length, Group, Port);
                    _log.LogDebug("Discovery broadcast (inst={Inst})", institutionId);
                }
                catch (Exception ex) { _log.LogDebug(ex, "Broadcast error"); }
                await Task.Delay(30_000, _cts.Token);
            }
        }, _cts.Token);
    }

    private static string GetIp()
    {
        try { return Dns.GetHostEntry(Dns.GetHostName()).AddressList
            .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            ?.ToString() ?? "127.0.0.1"; }
        catch { return "127.0.0.1"; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp?.DropMulticastGroup(IPAddress.Parse(Group)); } catch { }
        _udp?.Dispose(); _cts.Dispose();
    }
}
