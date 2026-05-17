namespace PeripheralMonitor.Client.Wpf.Models;

public record DeviceAlert(
    string MachineName, 
    string EventType, 
    string DeviceName, 
    DateTime Timestamp
);