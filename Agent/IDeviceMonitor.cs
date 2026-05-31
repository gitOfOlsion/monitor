namespace PeripheralMonitor.Agent;

// Cross-platform abstraction over device monitoring backends
public interface IDeviceMonitor : IDisposable
{
    // Fired when a device connects, disconnects, or changes
    event Func<string, DeviceInfo, Task> DeviceChanged;

    Task StartAsync(CancellationToken ct = default);
    void Stop();

    // Returns a snapshot of all currently present devices
    Task<IReadOnlyList<DeviceInfo>> GetSnapshotAsync();
}

public record DeviceInfo(
    string DeviceId,
    string FriendlyName,
    string Category,
    string Status
);
