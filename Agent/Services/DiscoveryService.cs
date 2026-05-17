namespace PeripheralMonitor.Agent.Services;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PeripheralMonitor.Shared.Models;



public class DiscoveryService : IDisposable
{
    private readonly UdpClient _udpClient;
    private readonly string _machineName;
    private readonly string _ipAddress;
    private readonly string _macAddress;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<DiscoveryService> _logger;

    // Multicast group for LAN discovery (224.0.0.251 is safe, non-routable)
    private const string DiscoveryGroup = "224.0.0.251";
    private const int DiscoveryPort = 5001;

    public DiscoveryService(ILogger<DiscoveryService> logger)
    {
        _logger = logger;
        _machineName = Environment.MachineName;
        _ipAddress = GetLocalIPAddress();
        _macAddress = GetMacAddress();
        
        _udpClient = new UdpClient();
        _udpClient.EnableBroadcast = true;
        _udpClient.JoinMulticastGroup(IPAddress.Parse(DiscoveryGroup));
    }

    public void StartBroadcasting()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var broadcast = new DiscoveryBroadcast(
                        MachineName: _machineName,
                        IPAddress: _ipAddress,
                        MacAddress: _macAddress,
                        Timestamp: DateTime.UtcNow
                    );
                    
                    var json = JsonSerializer.Serialize(broadcast);
                    var data = Encoding.UTF8.GetBytes(json);
                    
                    // Send to multicast group
                    await _udpClient.SendAsync(data, data.Length, DiscoveryGroup, DiscoveryPort);
                    _logger.LogDebug("📡 Discovery broadcast sent");
                    
                    // Listen for approval response (server unicast reply)
                    _ = ListenForApproval();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Discovery broadcast error");
                }
                
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token); // Broadcast every 30s
            }
        }, _cts.Token);
    }

    private async Task ListenForApproval()
    {
        try
        {
            var timeoutTask = Task.Delay(2000); // 2s timeout for response
            var receiveTask = _udpClient.ReceiveAsync();
            
            var completed = await Task.WhenAny(receiveTask, timeoutTask);
            if (completed == receiveTask)
            {
                var result = await receiveTask;
                var response = Encoding.UTF8.GetString(result.Buffer);
                // Expected: {"approved":true,"token":"jwt..."}
                if (response.Contains("\"approved\":true"))
                {
                    // Extract token and save to config
                    var doc = JsonDocument.Parse(response);
                    if (doc.RootElement.TryGetProperty("token", out var tokenEl))
                    {
                        var token = tokenEl.GetString();
                        // Save to appsettings.local.json (gitignored)
                        var localConfig = new { SignalRUrl = "http://SERVER_IP:5000/monitor", JwtToken = token };
                        var json = JsonSerializer.Serialize(localConfig, new JsonSerializerOptions { WriteIndented = true });
                        await File.WriteAllTextAsync("appsettings.local.json", json);
                        _logger.LogInformation("✅ Agent approved! Token saved.");
                    }
                }
            }
        }
        catch { /* Ignore timeout/no response */ }
    }

    private static string GetLocalIPAddress()
    {
        var host = Dns.GetHostEntry(Dns.GetHostName());
        foreach (var ip in host.AddressList)
            if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                return ip.ToString();
        return "127.0.0.1";
    }

    private static string GetMacAddress()
    {
        var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up 
                && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback);
        return nic?.GetPhysicalAddress().ToString() ?? "UNKNOWN";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udpClient?.DropMulticastGroup(IPAddress.Parse(DiscoveryGroup));
        _udpClient?.Dispose();
        _cts.Dispose();
    }
}