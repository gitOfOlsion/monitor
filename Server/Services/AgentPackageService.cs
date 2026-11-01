using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using PeripheralMonitor.Shared.Models;
using PeripheralMonitor.Shared.Security;

namespace PeripheralMonitor.Server.Services;

/// <summary>
/// Builds downloadable agent packages on-the-fly:
///   1. Reads pre-built agent binaries from /app/agent-packages/{platform}/
///   2. Generates appsettings.local.json with the institution's JWT token
///   3. Includes the platform-specific installer script
///   4. Returns a zip stream
/// </summary>
public class AgentPackageService
{
    private readonly IConfiguration _config;
    private readonly ILogger<AgentPackageService> _logger;
    private const string PackageBase = "/app/agent-packages";

    public AgentPackageService(IConfiguration config, ILogger<AgentPackageService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Stock Windows agent exe that doubles as its own installer.</summary>
    private static string WindowsExePath => Path.Combine(PackageBase, "windows", "PeripheralMonitor.Agent.exe");

    public bool IsWindowsInstallerAvailable() => File.Exists(WindowsExePath);

    /// <summary>
    /// One-file Windows installer: the stock agent exe followed by this institution's
    /// config (see <see cref="InstallerConfigTrailer"/>). Returns the exe path to stream
    /// and the trailer to write after it, so nothing large is buffered in memory.
    /// </summary>
    public (string ExePath, byte[] Trailer) GetWindowsInstaller(Guid institutionId, string serverUrl)
    {
        if (!IsWindowsInstallerAvailable())
            throw new InvalidOperationException($"Windows agent not found at {WindowsExePath}");
        var trailer = InstallerConfigTrailer.Build(
            new InstallerConfig($"{serverUrl.TrimEnd('/')}/monitor", ProvisionToken(institutionId)));
        return (WindowsExePath, trailer);
    }

    // ── Linux one-file installer (.run) ───────────────────────────────────────────────

    private static string LinuxDir         => Path.Combine(PackageBase, "linux");
    private static string RunHeaderPath    => Path.Combine(PackageBase, "installers", "linux-run-header.sh");
    private static string LinuxInstallPath => Path.Combine(PackageBase, "installers", "install.sh");

    private const UnixFileMode Mode755 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                       | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                       | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode Mode644 = UnixFileMode.UserRead | UnixFileMode.UserWrite
                                       | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode Mode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public bool IsLinuxInstallerAvailable() =>
        Directory.Exists(LinuxDir) && File.Exists(RunHeaderPath) && File.Exists(LinuxInstallPath);

    /// <summary>
    /// Writes a self-extracting Linux installer to <paramref name="output"/>: the shell
    /// header (linux-run-header.sh) followed by a .tar.gz of the agent, install.sh and
    /// this institution's appsettings.local.json. Streamed straight to the response.
    /// </summary>
    public async Task WriteLinuxInstallerAsync(Stream output, Guid institutionId, string serverUrl, CancellationToken ct)
    {
        if (!IsLinuxInstallerAvailable())
            throw new InvalidOperationException("Linux agent or installer scripts not found");

        // Scripts must have LF line endings or sh/bash fail - normalise in case the
        // repo was checked out on Windows with CRLF.
        var header = Lf(await File.ReadAllTextAsync(RunHeaderPath, ct)).TrimEnd() + "\n";
        if (!header.EndsWith("\n__PM_PAYLOAD_BELOW__\n", StringComparison.Ordinal))
            throw new InvalidOperationException("linux-run-header.sh must end with the __PM_PAYLOAD_BELOW__ marker line");
        await output.WriteAsync(Encoding.UTF8.GetBytes(header), ct);

        await using var gz  = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
        await using var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true);

        foreach (var file in Directory.GetFiles(LinuxDir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(LinuxDir, file).Replace('\\', '/');
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;  // debug symbols: not needed
            await using var data = File.OpenRead(file);
            var mode = name == "PeripheralMonitor.Agent" ? Mode755 : Mode644;
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { Mode = mode, DataStream = data }, ct);
        }

        var installSh = Lf(await File.ReadAllTextAsync(LinuxInstallPath, ct));
        await WriteTextEntry(tar, "install.sh", installSh, Mode755, ct);

        var settings = $$"""
            {
              "SignalRUrl": "{{serverUrl.TrimEnd('/')}}/monitor",
              "JwtToken": "{{ProvisionToken(institutionId)}}"
            }
            """;
        await WriteTextEntry(tar, "appsettings.local.json", settings, Mode600, ct);
    }

    private static async Task WriteTextEntry(TarWriter tar, string name, string text, UnixFileMode mode, CancellationToken ct)
    {
        using var data = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { Mode = mode, DataStream = data }, ct);
    }

    private static string Lf(string s) => s.Replace("\r\n", "\n");

    private string ProvisionToken(Guid institutionId)
    {
        var secret = _config["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret not configured");
        return JwtHelper.GenerateAgentToken("__PROVISIONED__", "__PENDING__", institutionId, secret);
    }

    public byte[] BuildPackage(string platform, Guid institutionId, string serverUrl)
    {
        var platformDir = Path.Combine(PackageBase, platform);
        if (!Directory.Exists(platformDir))
            throw new InvalidOperationException($"Agent package not found for platform '{platform}'. Expected at: {platformDir}");

        // Generate JWT token for this institution
        var token = ProvisionToken(institutionId);

        using var ms  = new MemoryStream();
        using var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true);

        // 1. Add all agent binary files
        foreach (var file in Directory.GetFiles(platformDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(platformDir, file);
            var entry        = zip.CreateEntry(relativePath, CompressionLevel.Optimal);

            using var entryStream = entry.Open();
            using var fileStream  = File.OpenRead(file);
            fileStream.CopyTo(entryStream);
        }

        // 2. Add appsettings.local.json with pre-filled JWT token
        var settingsJson = $$"""
            {
              "SignalRUrl": "{{serverUrl}}/monitor",
              "JwtToken": "{{token}}"
            }
            """;
        var settingsEntry = zip.CreateEntry("appsettings.local.json", CompressionLevel.Optimal);
        using (var sw = new StreamWriter(settingsEntry.Open()))
            sw.Write(settingsJson);

        // 3. Add installer scripts
        var installerDir = Path.Combine(PackageBase, "installers");
        if (platform == "windows")
        {
            AddFileToZip(zip, Path.Combine(installerDir, "install.ps1"), "install.ps1");
            AddFileToZip(zip, Path.Combine(installerDir, "install.bat"), "Install - Right-Click Run as Admin.bat");
        }
        else
        {
            AddFileToZip(zip, Path.Combine(installerDir, "install.sh"), "install.sh");
        }

        // 4. Add a README
        var readme = platform == "windows"
            ? "# Peripheral Monitor Agent — Windows\n\n1. Extract this zip to a folder\n2. Double-click 'Install - Right-Click Run as Admin.bat'\n3. If prompted by UAC, click Yes\n4. The installer handles everything automatically\n5. The agent will appear in the dashboard\n"
            : "# Peripheral Monitor Agent — Linux\n\n1. Extract this archive\n2. Run: sudo bash install.sh\n3. The installer handles everything automatically\n4. The agent will appear in the dashboard\n";
        var readmeEntry = zip.CreateEntry("README.md", CompressionLevel.Optimal);
        using (var sw = new StreamWriter(readmeEntry.Open()))
            sw.Write(readme);

        zip.Dispose(); // flush before reading
        return ms.ToArray();
    }

    public bool IsPackageAvailable(string platform) =>
        Directory.Exists(Path.Combine(PackageBase, platform));

    private static void AddFileToZip(ZipArchive zip, string sourcePath, string entryName)
    {
        if (!File.Exists(sourcePath)) return;
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        if (entryName.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
        {
            // bash refuses CRLF scripts; a Windows checkout produces CRLF
            var lf = Encoding.UTF8.GetBytes(Lf(File.ReadAllText(sourcePath)));
            entryStream.Write(lf);
            return;
        }
        using var fileStream  = File.OpenRead(sourcePath);
        fileStream.CopyTo(entryStream);
    }
}
