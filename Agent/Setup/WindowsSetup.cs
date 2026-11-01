using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Win32;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Agent.Setup;

/// <summary>Parsed setup command line.</summary>
internal sealed record SetupOptions(string Verb, bool Quiet, bool DryRun, bool AllowLanRdp, bool Purge);

/// <summary>
/// Self-installer for Windows. The same PeripheralMonitor.Agent.exe is both the service
/// and its own setup program: the server appends the institution's config to the exe
/// (see <see cref="InstallerConfigTrailer"/>), the user double-clicks it, and it installs
/// itself as the service.
///
///   PeripheralMonitorAgent-Setup.exe                     install (UAC prompt, shows progress)
///   PeripheralMonitorAgent-Setup.exe install --quiet     unattended install (GPO / Intune / scripts)
///   PeripheralMonitorAgent-Setup.exe install --dry-run   show what would happen, change nothing
///   PeripheralMonitor.Agent.exe uninstall [--purge]      remove (also in Apps &amp; features)
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsSetup
{
    private const string ServiceName   = "PeripheralMonitorAgent";
    private const string DisplayName   = "Peripheral Monitor Agent";
    private const string ExeName       = "PeripheralMonitor.Agent.exe";
    private const string LegacyAccount = "PeripheralMonitorSvc";
    private const string Publisher     = "Peripheral Monitor";
    private const string UninstallKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PeripheralMonitorAgent";
    private const string TsKey         = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    private const string RdpTcpKey     = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";

    private const int ExitOk = 0, ExitFailed = 1, ExitNoConfig = 2, ExitNotElevated = 3;

    private static readonly string InstallDir = @"C:\PeripheralMonitor";
    private static string InstalledExe => Path.Combine(InstallDir, ExeName);
    private static string LocalConfig  => Path.Combine(InstallDir, "appsettings.local.json");
    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PeripheralMonitor");
    private static string SelfPath => Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine the path of this program.");

    // ── Entry ────────────────────────────────────────────────────────────

    /// <summary>
    /// Decides whether this launch is a setup run. Explicit verbs always are. With no
    /// arguments, a double-clicked exe that carries an embedded config (and is not the
    /// running service or the already-installed copy) is treated as "install". Every
    /// other launch runs the agent normally, so service and dev behaviour are unchanged.
    /// </summary>
    public static bool TryParse(string[] args, out SetupOptions options)
    {
        var flags = args.Select(a => a.ToLowerInvariant()).ToHashSet();
        var verb  = flags.Contains("install") ? "install"
                  : flags.Contains("uninstall") ? "uninstall"
                  : null;

        if (verb == null && args.Length == 0 && !WindowsServiceHelpers.IsWindowsService()
            && !SamePath(SelfPath, InstalledExe)
            && InstallerConfigTrailer.TryRead(SelfPath) != null)
            verb = "install";

        options = new SetupOptions(verb ?? "",
            Quiet:       flags.Contains("--quiet") || flags.Contains("/quiet"),
            DryRun:      flags.Contains("--dry-run"),
            AllowLanRdp: flags.Contains("--allow-lan-rdp"),
            Purge:       flags.Contains("--purge"));
        return verb != null;
    }

    public static int Run(SetupOptions o)
    {
        Console.Title = $"{DisplayName} Setup";
        try
        {
            if (!o.DryRun && !IsElevated())
                return RelaunchElevated(o);

            var code = o.Verb == "uninstall" ? Uninstall(o) : Install(o);
            Pause(o);
            return code;
        }
        catch (Exception ex)
        {
            Fail($"Setup failed: {ex.Message}");
            Pause(o);
            return ExitFailed;
        }
    }

    // ── Install ──────────────────────────────────────────────────────────

    private static int Install(SetupOptions o)
    {
        Banner(o.DryRun ? "Install (dry run - nothing will be changed)" : "Install");

        var embedded = InstallerConfigTrailer.TryRead(SelfPath);
        var upgrade  = File.Exists(LocalConfig);
        if (embedded == null && !upgrade)
        {
            Fail("This installer has no institution configuration.");
            Info("Download the Windows installer from the dashboard: Institutions → Download Agent.");
            return ExitNoConfig;
        }

        if (embedded != null)
        {
            Info($"Server:      {embedded.SignalRUrl}");
            Info($"Institution: token {Mask(embedded.JwtToken)}");
            if (!embedded.SignalRUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                Warn("Server URL is not HTTPS - agent traffic would not be encrypted.");
        }
        else Info("No embedded config - upgrading binary and keeping the existing configuration.");
        Info($"Install to:  {InstallDir}");
        if (IsWindowsHome())
            Warn("Windows Home edition: it has no Remote Desktop server, so dashboard remote desktop will not work. Monitoring works normally.");

        if (o.DryRun)
        {
            Step("Would stop the existing service, copy the agent, write the config, lock folder permissions,");
            Info("register the service as LocalSystem, enable RDP for the agent tunnel only, add an");
            Info($"Apps & features entry and start the service.{(o.AllowLanRdp ? " Would ALSO open RDP to the LAN." : "")}");
            Ok("Dry run complete");
            return ExitOk;
        }

        Step("Stopping existing agent (if running)...");
        StopService();
        KillStrayAgents();

        Step($"Installing files to {InstallDir}...");
        Directory.CreateDirectory(InstallDir);
        Directory.CreateDirectory(Path.Combine(InstallDir, "logs"));
        Directory.CreateDirectory(DataDir);
        // Lock BEFORE writing the token so it is never readable by other users, not even briefly.
        LockFolder(InstallDir);
        LockFolder(DataDir);
        if (!SamePath(SelfPath, InstalledExe))
            CopyWithRetry(SelfPath, InstalledExe);
        Ok("Agent binary installed");

        if (embedded != null)
        {
            WriteLocalConfig(embedded);
            Ok("Configuration written (readable by SYSTEM and Administrators only)");
        }

        Step("Removing legacy service account (if present)...");
        RemoveLegacyAccount();

        Step("Registering Windows service...");
        RegisterService();
        Ok($"Service '{ServiceName}' registered (LocalSystem, automatic start, restart on failure)");

        Step("Configuring Remote Desktop for the agent tunnel...");
        ConfigureRdp(o.AllowLanRdp);

        Step("Registering in Apps & features...");
        RegisterUninstallEntry();
        Ok("Uninstall entry added");

        Step("Starting service...");
        StartService();

        Console.WriteLine();
        Ok("Installation complete. The device will appear in the dashboard (approve it if it shows as Pending).");
        Info($"Logs: {Path.Combine(InstallDir, "logs")}");
        return ExitOk;
    }

    // ── Uninstall ────────────────────────────────────────────────────────

    private static int Uninstall(SetupOptions o)
    {
        Banner("Uninstall");
        if (!o.Quiet)
        {
            Console.Write("    Remove the Peripheral Monitor Agent from this computer? [y/N] ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Info("Cancelled.");
                return ExitOk;
            }
        }

        Step("Stopping and removing the service...");
        StopService();
        KillStrayAgents();
        Sc("delete", ServiceName);
        Ok("Service removed");

        Step("Restoring Remote Desktop settings...");
        RestoreRdp();

        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        Ok("Uninstall entry removed");

        Step("Removing files...");
        if (o.Purge && Directory.Exists(DataDir))
        {
            Directory.Delete(DataDir, recursive: true);
            Ok($"Event history deleted ({DataDir})");
        }
        else Info($"Event history kept in {DataDir} (use --purge to delete it).");

        // This exe usually runs from the install folder and can't delete itself, so a
        // hidden helper waits for this process to exit and then removes the folder.
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                 $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1; " +
                 $"Remove-Item -LiteralPath '{InstallDir}' -Recurse -Force -ErrorAction SilentlyContinue" })
            psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
        Ok($"{InstallDir} will be deleted when this window closes");
        return ExitOk;
    }

    // ── Steps ────────────────────────────────────────────────────────────

    private static void StopService()
    {
        using var sc = OpenService();
        if (sc == null) { Info("No existing service"); return; }
        if (sc.Status is ServiceControllerStatus.Stopped) { Info("Service already stopped"); return; }
        try
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            Ok("Service stopped");
        }
        catch (Exception ex) when (ex is System.ServiceProcess.TimeoutException or InvalidOperationException)
        {
            Warn($"Service did not stop cleanly ({ex.Message}); the process will be ended.");
        }
    }

    private static void KillStrayAgents()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId) continue;
                try { p.Kill(); p.WaitForExit(5000); } catch { /* already gone */ }
            }
        }
    }

    private static void CopyWithRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { File.Copy(from, to, overwrite: true); return; }
            catch (IOException) when (attempt < 10) { Thread.Sleep(1000); }
        }
    }

    private static void WriteLocalConfig(InstallerConfig cfg)
    {
        // Merge, so settings an admin added by hand survive a reinstall.
        JsonObject obj;
        try { obj = File.Exists(LocalConfig) ? JsonNode.Parse(File.ReadAllText(LocalConfig)) as JsonObject ?? new() : new(); }
        catch (JsonException) { obj = new(); }
        obj["SignalRUrl"] = cfg.SignalRUrl;
        obj["JwtToken"]   = cfg.JwtToken;
        File.WriteAllText(LocalConfig, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// SYSTEM + Administrators only. A folder under C:\ inherits "Authenticated Users:
    /// Modify" from the drive root, which would let any user replace the exe that runs
    /// as SYSTEM. Reset explicit rules first (older installers made them explicit).
    /// </summary>
    private static void LockFolder(string path)
    {
        Exec("icacls.exe", path, "/setowner", "*S-1-5-32-544", "/T", "/C", "/Q");
        Exec("icacls.exe", path, "/reset", "/T", "/C", "/Q");
        var code = Exec("icacls.exe", path, "/inheritance:r", "/grant:r",
                       "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "/Q");
        if (code != 0) throw new InvalidOperationException($"Could not secure {path} (icacls exit {code}).");
    }

    private static void RemoveLegacyAccount()
    {
        // Older script versions created this account and never used it.
        if (Exec("net.exe", "user", LegacyAccount) != 0) { Info("None found"); return; }
        if (Exec("net.exe", "user", LegacyAccount, "/delete") == 0) Ok($"Removed unused account '{LegacyAccount}'");
        else Warn($"Could not remove account '{LegacyAccount}'");
    }

    private static void RegisterService()
    {
        var binPath = $"\"{InstalledExe}\"";
        using (var existing = OpenService())
        {
            var code = existing == null
                ? Sc("create", ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", DisplayName)
                : Sc("config", ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", DisplayName);
            if (code != 0) throw new InvalidOperationException($"sc.exe could not register the service (exit {code}).");
        }
        Sc("description", ServiceName, "Monitors peripheral devices and reports to the central dashboard");
        Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/30000");
    }

    private static void ConfigureRdp(bool allowLan)
    {
        using var uninstall = Registry.LocalMachine.CreateSubKey(UninstallKey);
        using var ts  = Registry.LocalMachine.CreateSubKey(TsKey);
        using var rdp = Registry.LocalMachine.CreateSubKey(RdpTcpKey);

        // Remember the machine's original values once, so uninstall can put them back.
        if (uninstall.GetValue("PrevDenyTSConnections") == null)
            uninstall.SetValue("PrevDenyTSConnections", ts.GetValue("fDenyTSConnections") ?? 1, RegistryValueKind.DWord);
        if (uninstall.GetValue("PrevUserAuthentication") == null)
            uninstall.SetValue("PrevUserAuthentication", rdp.GetValue("UserAuthentication") ?? 1, RegistryValueKind.DWord);

        ts.SetValue("fDenyTSConnections", 0, RegistryValueKind.DWord);
        // NLA optional: the server connects in TLS mode (GuacamoleTokenBuilder, security=tls).
        // Safe here because RDP is NOT opened in the firewall (see below), so only the
        // agent tunnel on 127.0.0.1 can reach it.
        rdp.SetValue("UserAuthentication", 0, RegistryValueKind.DWord);
        Ok("RDP enabled (TLS, NLA optional)");

        if (allowLan)
        {
            SetRdpFirewall(enabled: true);
            uninstall.SetValue("OpenedLanRdp", 1, RegistryValueKind.DWord);
            Warn("RDP opened to the LAN (--allow-lan-rdp): port 3389 is reachable from the network.");
        }
        else
        {
            Ok("Firewall left closed for RDP - only the agent tunnel (localhost) can reach it");
            Info($"If an older installer opened it, close it with: Set-NetFirewallRule -Group '{RdpFirewallGroup}' -Enabled False");
        }
    }

    private static void RestoreRdp()
    {
        using var uninstall = Registry.LocalMachine.OpenSubKey(UninstallKey);
        if (uninstall == null) { Info("Nothing to restore"); return; }
        using var ts  = Registry.LocalMachine.CreateSubKey(TsKey);
        using var rdp = Registry.LocalMachine.CreateSubKey(RdpTcpKey);
        if (uninstall.GetValue("PrevDenyTSConnections") is int deny)
            ts.SetValue("fDenyTSConnections", deny, RegistryValueKind.DWord);
        if (uninstall.GetValue("PrevUserAuthentication") is int nla)
            rdp.SetValue("UserAuthentication", nla, RegistryValueKind.DWord);
        if (uninstall.GetValue("OpenedLanRdp") is 1)
            SetRdpFirewall(enabled: false);
        Ok("Remote Desktop settings restored to their values before install");
    }

    private static void RegisterUninstallEntry()
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        key.SetValue("DisplayName", DisplayName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" uninstall");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" uninstall --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    private static void StartService()
    {
        using var sc = OpenService() ?? throw new InvalidOperationException("Service was not registered.");
        try
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            Ok("Service is running");
        }
        catch (Exception ex) when (ex is System.ServiceProcess.TimeoutException or InvalidOperationException)
        {
            Warn($"Service did not start ({ex.Message}). Check the logs in {Path.Combine(InstallDir, "logs")}.");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static ServiceController? OpenService()
    {
        var sc = new ServiceController(ServiceName);
        try { _ = sc.Status; return sc; }               // throws if the service doesn't exist
        catch (InvalidOperationException) { sc.Dispose(); return null; }
    }

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int RelaunchElevated(SetupOptions o)
    {
        var psi = new ProcessStartInfo(SelfPath) { UseShellExecute = true, Verb = "runas" };
        psi.ArgumentList.Add(o.Verb);
        if (o.Quiet)       psi.ArgumentList.Add("--quiet");
        if (o.AllowLanRdp) psi.ArgumentList.Add("--allow-lan-rdp");
        if (o.Purge)       psi.ArgumentList.Add("--purge");
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return ExitFailed;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // user clicked "No" on UAC
        {
            Fail("Administrator rights are required. Run the installer again and choose Yes.");
            Pause(o);
            return ExitNotElevated;
        }
    }

    private static bool IsWindowsHome()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var edition = key?.GetValue("EditionID") as string ?? "";
        return edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase); // Core = Home
    }

    private static int Sc(params string[] args) => Exec("sc.exe", args);

    // Built-in "Remote Desktop" firewall group, by resource id: the display name is
    // translated on non-English Windows, the id is not.
    private const string RdpFirewallGroup = "@FirewallAPI.dll,-28752";

    private static void SetRdpFirewall(bool enabled) =>
        Exec(@"WindowsPowerShell\v1.0\powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
             $"Set-NetFirewallRule -Group '{RdpFirewallGroup}' -Enabled {(enabled ? "True" : "False")}");

    /// <summary>Runs a system tool from System32 with properly quoted arguments; returns its exit code.</summary>
    private static int Exec(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, tool))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {tool}.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        _ = stdout.Result; _ = stderr.Result;
        return p.ExitCode;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string Mask(string token) => token.Length <= 8 ? "****" : $"{token[..4]}…{token[^4..]}";

    private static void Pause(SetupOptions o)
    {
        if (o.Quiet || Console.IsInputRedirected) return;
        Console.WriteLine();
        Console.Write("Press Enter to close...");
        Console.ReadLine();
    }

    private static void Banner(string what)
    {
        Console.WriteLine();
        Console.WriteLine("======================================================");
        Console.WriteLine($"  {DisplayName} - {what}");
        Console.WriteLine("======================================================");
    }

    private static void Step(string m) => Write($"\n[*] {m}", ConsoleColor.Cyan);
    private static void Ok(string m)   => Write($"    [OK] {m}", ConsoleColor.Green);
    private static void Warn(string m) => Write($"    [WARN] {m}", ConsoleColor.Yellow);
    private static void Fail(string m) => Write($"    [FAIL] {m}", ConsoleColor.Red);
    private static void Info(string m) => Write($"    {m}", ConsoleColor.Gray);

    private static void Write(string m, ConsoleColor c)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = c;
        Console.WriteLine(m);
        Console.ForegroundColor = prev;
    }
}
