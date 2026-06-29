using System.Collections.Concurrent;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Server.Services;

/// <summary>
/// Keeps a short rolling window of live utilisation samples per agent, in memory,
/// for the metrics dashboard sparklines. This is intentionally NOT persisted —
/// it's a live operational view (last ~10 minutes), not historical analytics.
/// If long-term retention is ever needed, this is where a time-series DB
/// (Prometheus/Timescale) would plug in.
/// </summary>
public sealed class MetricsService
{
    private const int MaxSamples = 60; // ~10 min at a 10s heartbeat

    private sealed class Window
    {
        public readonly Queue<double> Cpu = new();
        public readonly Queue<double> Ram = new();
        public readonly Queue<double> Disk = new();
        public readonly Queue<double> Net = new();
        public double? LastCpu, LastRam, LastDisk, LastDiskActive, LastNet;
        public long?   LastRamUsedMb;
        public DateTime LastUpdate;
    }

    private readonly ConcurrentDictionary<Guid, Window> _windows = new();

    public void Record(Guid agentId, double? cpu, double? ram, double? disk, long? ramUsedMb,
                       double? diskActive = null, double? netKbps = null)
    {
        var w = _windows.GetOrAdd(agentId, _ => new Window());
        lock (w)
        {
            if (cpu.HasValue)
            {
                w.Cpu.Enqueue(cpu.Value);
                while (w.Cpu.Count > MaxSamples) w.Cpu.Dequeue();
                w.LastCpu = cpu;
            }
            if (ram.HasValue)
            {
                w.Ram.Enqueue(ram.Value);
                while (w.Ram.Count > MaxSamples) w.Ram.Dequeue();
                w.LastRam = ram;
            }
            if (diskActive.HasValue)
            {
                w.Disk.Enqueue(diskActive.Value);
                while (w.Disk.Count > MaxSamples) w.Disk.Dequeue();
                w.LastDiskActive = diskActive;
            }
            if (netKbps.HasValue)
            {
                w.Net.Enqueue(netKbps.Value);
                while (w.Net.Count > MaxSamples) w.Net.Dequeue();
                w.LastNet = netKbps;
            }
            if (disk.HasValue)      w.LastDisk = disk;
            if (ramUsedMb.HasValue) w.LastRamUsedMb = ramUsedMb;
            w.LastUpdate = DateTime.UtcNow;
        }
    }

    public (double? cpu, double? ram, double? disk, long? ramUsedMb, double? diskActive, double? netKbps,
            List<double> cpuHist, List<double> ramHist, List<double> diskHist, List<double> netHist) Get(Guid agentId)
    {
        if (!_windows.TryGetValue(agentId, out var w))
            return (null, null, null, null, null, null, [], [], [], []);
        lock (w)
        {
            return (w.LastCpu, w.LastRam, w.LastDisk, w.LastRamUsedMb, w.LastDiskActive, w.LastNet,
                    w.Cpu.ToList(), w.Ram.ToList(), w.Disk.ToList(), w.Net.ToList());
        }
    }
}
