using System.IO.Compression;
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

    public byte[] BuildPackage(string platform, Guid institutionId, string serverUrl)
    {
        var platformDir = Path.Combine(PackageBase, platform);
        if (!Directory.Exists(platformDir))
            throw new InvalidOperationException($"Agent package not found for platform '{platform}'. Expected at: {platformDir}");

        // Generate JWT token for this institution
        var secret = _config["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret not configured");
        var token  = JwtHelper.GenerateAgentToken("__PROVISIONED__", "__PENDING__", institutionId, secret);

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

        // 2. Add appsettings.local.json with pre-filled token
        var settingsJson = $$"""
            {
              "SignalRUrl": "{{serverUrl}}/monitor",
              "JwtToken": "{{token}}",
              "Seq": { "Url": "{{serverUrl.Replace("https://", "http://").Replace("http://", "http://seq:")}}" }
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
        using var fileStream  = File.OpenRead(sourcePath);
        fileStream.CopyTo(entryStream);
    }
}
