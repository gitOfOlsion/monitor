using Microsoft.Extensions.Hosting;

namespace PeripheralMonitor.Server.Services;

public class DiscoveryHostedService : IHostedService
{
    private readonly DiscoveryListener _listener;
    public DiscoveryHostedService(DiscoveryListener listener) => _listener = listener;
    public Task StartAsync(CancellationToken ct) { _listener.Start(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { _listener.Dispose(); return Task.CompletedTask; }
}