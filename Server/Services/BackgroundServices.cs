using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Server.Services;

public class DiscoveryHostedService : BackgroundService
{
    private readonly AgentService _agents;
    private readonly ILogger<DiscoveryHostedService> _log;
    private const string Group = "224.0.0.251";
    private const int Port = 5001;

    public DiscoveryHostedService(AgentService agents, ILogger<DiscoveryHostedService> log)
    { _agents = agents; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        UdpClient? udp = null;
        try { udp = new UdpClient(Port); udp.JoinMulticastGroup(IPAddress.Parse(Group)); _log.LogInformation("UDP discovery active on :{Port}", Port); }
        catch (Exception ex) { _log.LogWarning(ex, "UDP discovery could not start"); return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r  = await udp.ReceiveAsync(ct);
                var bc = JsonSerializer.Deserialize<DiscoveryBroadcast>(Encoding.UTF8.GetString(r.Buffer));
                if (bc != null) await _agents.UpsertFromDiscoveryAsync(bc);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Discovery receive error"); }
        }

        try { udp.DropMulticastGroup(IPAddress.Parse(Group)); } catch { }
        udp.Dispose();
    }
}

public class OfflineDetectorService : BackgroundService
{
    private readonly AgentService _agents;
    private readonly ILogger<OfflineDetectorService> _log;

    public OfflineDetectorService(AgentService agents, ILogger<OfflineDetectorService> log)
    { _agents = agents; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try   { await _agents.MarkStaleOfflineAsync(TimeSpan.FromSeconds(60)); }
            catch (Exception ex) { _log.LogWarning(ex, "OfflineDetector error"); }
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}

/// <summary>Hourly sweep that purges demo institutions past their 30-day PurgeAt.</summary>
public class DemoPurgeService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DemoPurgeService> _log;

    public DemoPurgeService(IServiceScopeFactory scopes, ILogger<DemoPurgeService> log)
    { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Small initial delay so startup/migrations finish first
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch { }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var demo = scope.ServiceProvider.GetRequiredService<DemoService>();
                await demo.PurgeExpiredAsync();
            }
            catch (Exception ex) { _log.LogWarning(ex, "DemoPurge error"); }
            await Task.Delay(TimeSpan.FromHours(1), ct);
        }
    }
}
