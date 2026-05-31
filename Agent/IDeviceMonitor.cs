namespace PeripheralMonitor.Agent;

public interface IDeviceMonitor : IDisposable
{
    event Func<string, DeviceInfo, Task> DeviceChanged;
    Task StartAsync(CancellationToken ct = default);
    void Stop();
    Task<IReadOnlyList<DeviceInfo>> GetSnapshotAsync();
}

public record DeviceInfo(
    string DeviceId,
    string FriendlyName,
    string Category,   // matches DeviceCategory.Name in DB
    string Status
);