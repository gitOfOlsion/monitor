using System.Text.Json;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Agent.Services;

/// <summary>
/// Durable, restart-safe event storage backed by append-only JSONL files in a
/// location that survives agent reinstall (%ProgramData% on Windows,
/// /var/lib/peripheralmonitor on Linux).
///
/// Two files:
///   outbox.jsonl  — events not yet confirmed delivered to the server. Drained
///                   (rewritten without delivered lines) after each successful flush.
///   history.jsonl — every event ever recorded. Append-only, never truncated.
///                   Kept for permanent local history even across reinstalls.
///
/// Each event carries a client-generated EventId so the server can dedup if the
/// outbox is replayed (e.g. after a crash before the outbox was trimmed, or a
/// reinstall that re-reads a leftover outbox).
/// </summary>
public sealed class EventStore
{
    private readonly string _dir;
    private readonly string _outboxPath;
    private readonly string _historyPath;
    private readonly object _lock = new();
    private readonly Microsoft.Extensions.Logging.ILogger _log;

    public EventStore(Microsoft.Extensions.Logging.ILogger log)
    {
        _log = log;
        _dir = ResolveDataDir();
        Directory.CreateDirectory(_dir);
        _outboxPath  = Path.Combine(_dir, "outbox.jsonl");
        _historyPath = Path.Combine(_dir, "history.jsonl");
        _log.LogInformation("EventStore data dir: {Dir}", _dir);
    }

    /// <summary>Persistent data directory that survives reinstall.</summary>
    private static string ResolveDataDir()
    {
        if (OperatingSystem.IsWindows())
        {
            // C:\ProgramData\PeripheralMonitor  — survives app reinstall
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "PeripheralMonitor");
        }
        // Linux: standard persistent state location
        return "/var/lib/peripheralmonitor";
    }

    /// <summary>
    /// Record a brand-new event: append to BOTH history (permanent) and outbox
    /// (pending delivery). Returns the payload (with EventId assigned).
    /// </summary>
    public DeviceChangedPayload Record(DeviceChangedPayload payload)
    {
        if (payload.EventId == Guid.Empty)
            payload = payload with { EventId = Guid.NewGuid() };

        var line = JsonSerializer.Serialize(payload);
        lock (_lock)
        {
            try { File.AppendAllText(_historyPath, line + "\n"); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to append to history"); }

            try { File.AppendAllText(_outboxPath, line + "\n"); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to append to outbox"); }
        }
        return payload;
    }

    /// <summary>Read all currently-pending (undelivered) events from the outbox.</summary>
    public List<DeviceChangedPayload> GetPending()
    {
        lock (_lock)
        {
            var list = new List<DeviceChangedPayload>();
            if (!File.Exists(_outboxPath)) return list;
            try
            {
                foreach (var line in File.ReadAllLines(_outboxPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var p = JsonSerializer.Deserialize<DeviceChangedPayload>(line);
                        if (p != null) list.Add(p);
                    }
                    catch { /* skip malformed line */ }
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to read outbox"); }
            return list;
        }
    }

    /// <summary>
    /// Remove the given delivered EventIds from the outbox by rewriting it
    /// without those lines. History is untouched.
    /// </summary>
    public void MarkDelivered(IEnumerable<Guid> deliveredIds)
    {
        var delivered = new HashSet<Guid>(deliveredIds);
        if (delivered.Count == 0) return;

        lock (_lock)
        {
            if (!File.Exists(_outboxPath)) return;
            try
            {
                var keep = new List<string>();
                foreach (var line in File.ReadAllLines(_outboxPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var p = JsonSerializer.Deserialize<DeviceChangedPayload>(line);
                        if (p != null && !delivered.Contains(p.EventId))
                            keep.Add(line);
                    }
                    catch { /* drop malformed */ }
                }
                // Atomic-ish rewrite: write to temp then replace
                var tmp = _outboxPath + ".tmp";
                File.WriteAllLines(tmp, keep);
                File.Move(tmp, _outboxPath, overwrite: true);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to trim outbox"); }
        }
    }

    public int PendingCount()
    {
        lock (_lock)
        {
            if (!File.Exists(_outboxPath)) return 0;
            try { return File.ReadAllLines(_outboxPath).Count(l => !string.IsNullOrWhiteSpace(l)); }
            catch { return 0; }
        }
    }
}
