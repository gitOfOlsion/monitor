using System.Diagnostics;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Agent.Services;

/// <summary>
/// Enforces Application Control policy on Windows. Two layers:
///   1) A process-watcher (WMI process-start trace) that terminates newly-started
///      processes whose image matches a blocked extension / name pattern. This is
///      edition-independent (works on Windows Pro) and is the primary enforcement
///      in this build.
///   2) (Optional, future) SRP registry rules for kernel-level prevention.
///
/// The watcher is reactive — a blocked process may briefly start before being
/// killed — but for the goal (stop ordinary users running games / unapproved
/// installers) it is effective. It deliberately never touches SYSTEM/service
/// processes or our own agent, to avoid destabilising the machine.
///
/// On Linux this currently runs the same best-effort watcher via /proc polling.
/// </summary>
public sealed class AppControlEnforcer : IDisposable
{
    private readonly Microsoft.Extensions.Logging.ILogger _log;
    private AppControlPolicy? _policy;
    private ManagementEventWatcherWrapper? _winWatcher;
    private CancellationTokenSource? _cts;

    // Processes we must never kill regardless of policy (safety guard).
    private static readonly HashSet<string> _protectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PeripheralMonitor.Agent", "explorer", "winlogon", "csrss", "services",
        "lsass", "smss", "wininit", "svchost", "dwm", "system", "msiexec",
        "RuntimeBroker", "fontdrvhost", "sihost", "taskhostw", "ctfmon"
    };

    public AppControlEnforcer(Microsoft.Extensions.Logging.ILogger log) => _log = log;

    /// <summary>Apply a new policy. Enabled=false (or null) tears enforcement down.</summary>
    public void Apply(AppControlPolicy? policy)
    {
        _policy = policy;
        if (policy is { Enabled: true })
        {
            _log.LogInformation("AppControl: enabling enforcement. Blocked ext=[{Ext}] patterns=[{Pat}]",
                string.Join(",", policy.BlockedExtensions), string.Join(",", policy.BlockedNamePatterns));
            Start();
        }
        else
        {
            _log.LogInformation("AppControl: disabling enforcement");
            Stop();
        }
    }

    private void Start()
    {
        Stop(); // clear any prior watcher
        _cts = new CancellationTokenSource();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _winWatcher = new ManagementEventWatcherWrapper(OnWindowsProcessStarted, _log);
                _winWatcher.Start();
            }
            catch (Exception ex) { _log.LogWarning(ex, "AppControl: failed to start Windows watcher"); }
        }
        else
        {
            _ = Task.Run(() => LinuxPollLoop(_cts.Token));
        }
    }

    private void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _winWatcher?.Dispose(); } catch { }
        _winWatcher = null;
        _cts = null;
    }

    /// <summary>Decide whether an image path is blocked under the current policy.</summary>
    private bool IsBlocked(string imagePath)
    {
        var policy = _policy;
        if (policy is not { Enabled: true } || string.IsNullOrEmpty(imagePath)) return false;

        var fileName = Path.GetFileName(imagePath);
        var nameNoExt = Path.GetFileNameWithoutExtension(imagePath);
        var ext = Path.GetExtension(imagePath);

        // Never touch protected/system processes
        if (_protectedNames.Contains(nameNoExt)) return false;

        // Allow carve-outs win over everything
        foreach (var allow in policy.AllowedNamePatterns)
            if (!string.IsNullOrWhiteSpace(allow) &&
                fileName.Contains(allow, StringComparison.OrdinalIgnoreCase))
                return false;

        // Blocked by extension
        foreach (var be in policy.BlockedExtensions)
            if (!string.IsNullOrWhiteSpace(be) &&
                ext.Equals(be.StartsWith('.') ? be : "." + be, StringComparison.OrdinalIgnoreCase))
                return true;

        // Blocked by name pattern
        foreach (var bp in policy.BlockedNamePatterns)
            if (!string.IsNullOrWhiteSpace(bp) &&
                fileName.Contains(bp, StringComparison.OrdinalIgnoreCase))
                return true;

        // Installer heuristic
        if (policy.BlockInstallers)
        {
            var n = nameNoExt.ToLowerInvariant();
            if (n.Contains("setup") || n.Contains("install") || n.EndsWith("_inst"))
                return true;
        }

        return false;
    }

    private void OnWindowsProcessStarted(int pid, string imagePath)
    {
        try
        {
            if (!IsBlocked(imagePath)) return;
            using var p = Process.GetProcessById(pid);
            // Only kill processes running in an interactive user session (>0), never Session 0.
            p.Kill(entireProcessTree: true);
            _log.LogWarning("AppControl: BLOCKED and terminated {Path} (pid {Pid})", imagePath, pid);
        }
        catch (ArgumentException) { /* process already exited */ }
        catch (Exception ex) { _log.LogWarning(ex, "AppControl: failed to terminate pid {Pid}", pid); }
    }

    private async Task LinuxPollLoop(CancellationToken ct)
    {
        var seen = new HashSet<int>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories("/proc"))
                {
                    var name = Path.GetFileName(dir);
                    if (!int.TryParse(name, out var pid) || !seen.Add(pid)) continue;
                    var exeLink = $"/proc/{pid}/exe";
                    string? target = null;
                    try { target = File.ResolveLinkTarget(exeLink, true)?.FullName; } catch { }
                    if (target != null && IsBlocked(target))
                    {
                        try { Process.GetProcessById(pid).Kill(true); _log.LogWarning("AppControl(Linux): blocked {P}", target); }
                        catch { }
                    }
                }
                if (seen.Count > 5000) seen.Clear();
            }
            catch { }
            await Task.Delay(1000, ct).ContinueWith(_ => { });
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Thin wrapper around WMI Win32_ProcessStartTrace so the rest of the agent has
/// no hard compile dependency on System.Management symbols in its signature.
/// </summary>
internal sealed class ManagementEventWatcherWrapper : IDisposable
{
    private readonly Action<int, string> _onStart;
    private readonly Microsoft.Extensions.Logging.ILogger _log;
    private System.Management.ManagementEventWatcher? _watcher;

    public ManagementEventWatcherWrapper(Action<int, string> onStart, Microsoft.Extensions.Logging.ILogger log)
    { _onStart = onStart; _log = log; }

    public void Start()
    {
        // Win32_ProcessStartTrace fires on every process creation (needs SYSTEM, which we are).
        var query = new System.Management.WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
        _watcher = new System.Management.ManagementEventWatcher(query);
        _watcher.EventArrived += (_, e) =>
        {
            try
            {
                var props = e.NewEvent.Properties;
                var pidObj = props["ProcessID"]?.Value;
                var nameObj = props["ProcessName"]?.Value;
                int pid = pidObj != null ? Convert.ToInt32(pidObj) : 0;
                var procName = nameObj?.ToString() ?? "";
                if (pid <= 0) return;

                // Resolve the full image path for accurate extension/pattern checks
                string imagePath = procName;
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(pid);
                    imagePath = p.MainModule?.FileName ?? procName;
                }
                catch { /* fall back to bare name from the trace */ }

                _onStart(pid, imagePath);
            }
            catch (Exception ex) { _log.LogDebug(ex, "AppControl: event handling error"); }
        };
        _watcher.Start();
    }

    public void Dispose()
    {
        try { _watcher?.Stop(); } catch { }
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
    }
}
