namespace PeripheralMonitor.Shared.Models;

public record AgentRegistration(
    string MachineName,
    string IPAddress,
    string MacAddress,
    DateTime FirstSeen,
    AgentStatus Status, // Pending, Approved, Rejected, Offline
    string? ApprovedBy,
    DateTime? ApprovedAt,
    string? JwtToken // Issued after approval
);

public enum AgentStatus { Pending, Approved, Rejected, Offline }

public record DeviceEvent(
    string MachineName,
    string EventType, // Connected, Disconnected, Modified
    string DeviceId,
    string FriendlyName,
    string Status,
    DateTime Timestamp
);

public record DiscoveryBroadcast(
    string MachineName,
    string IPAddress,
    string MacAddress,
    DateTime Timestamp
);