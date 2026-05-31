namespace PeripheralMonitor.Shared.Models;

public enum AgentStatus   { Pending, Approved, Rejected, Offline }
public enum AgentPlatform { Windows, Linux, Unknown }

// ── Discovery & hub payloads ─────────────────────────────────────────────
public record DiscoveryBroadcast(
    string        MachineName,
    string        IPAddress,
    string        MacAddress,
    AgentPlatform Platform,
    DateTime      Timestamp
);

public record HeartbeatPayload(
    string   MachineName,
    string   MacAddress,
    DateTime Timestamp,
    int      DeviceCount
);

public record DeviceChangedPayload(
    string   MachineName,
    string   EventType,
    string   DeviceId,
    string   FriendlyName,
    string   Category,
    string   Status,
    DateTime Timestamp
);
