namespace PeripheralMonitor.Shared.Models;

public enum AgentStatus   { Pending, Approved, Rejected, Offline }
public enum AgentPlatform { Windows, Linux, Unknown }
public enum UserRole      { SuperAdmin, InstitutionAdmin, Viewer }

public record DiscoveryBroadcast(
    string        MachineName,
    string        IPAddress,
    string        MacAddress,
    AgentPlatform Platform,
    Guid          InstitutionId,
    DateTime      Timestamp
);

public record HeartbeatPayload(
    string   MachineName,
    string   MacAddress,
    Guid     InstitutionId,
    DateTime Timestamp,
    int      DeviceCount
);

public record DeviceChangedPayload(
    string   MachineName,
    Guid     InstitutionId,
    string   EventType,
    string   DeviceId,
    string   FriendlyName,
    string   Category,
    string   Status,
    DateTime Timestamp
);

public record DeviceInfo(
    string DeviceId,
    string FriendlyName,
    string Category,
    string Status
);
