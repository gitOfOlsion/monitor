using PeripheralMonitor.Shared.Models;
namespace PeripheralMonitor.Agent;
public interface IDeviceMonitor : IDisposable
{
    event Func<string, DeviceInfo, Task> DeviceChanged;
    Task StartAsync(CancellationToken ct = default);
    void Stop();
    Task<IReadOnlyList<DeviceInfo>> GetSnapshotAsync();
}
