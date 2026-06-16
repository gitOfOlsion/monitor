using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Agent.Services;

/// <summary>
/// Manages a fixed "shared" folder used for file push/pull from the dashboard.
/// Windows: C:\Users\Public\Documents\PeripheralMonitorShared
///          (visible to every local user via "Public Documents" in File Explorer)
/// Linux:   /var/lib/peripheral-monitor/shared
///          (a symlink "PeripheralMonitorShared" is created in each real user's
///           home directory pointing here, so it shows up in their file manager)
///
/// All operations validate that the resulting path stays inside this folder —
/// filenames must not contain path separators or ".." segments.
/// </summary>
public class FileShareService
{
    private readonly ILogger<FileShareService> _log;
    public string SharedFolder { get; }

    private const long MaxFileSizeBytes = 50 * 1024 * 1024; // 50 MB

    public FileShareService(ILogger<FileShareService> log)
    {
        _log = log;
        SharedFolder = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "PeripheralMonitorShared")
            : "/var/lib/peripheral-monitor/shared";

        try
        {
            Directory.CreateDirectory(SharedFolder);
            _log.LogInformation("Shared folder ready: {Path}", SharedFolder);

            if (!OperatingSystem.IsWindows())
                LinkIntoUserHomes();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to create shared folder at {Path}", SharedFolder);
        }
    }

    /// <summary>On Linux, symlink the shared folder into every real user's home directory for visibility.</summary>
    private void LinkIntoUserHomes()
    {
        try
        {
            const string passwd = "/etc/passwd";
            if (!File.Exists(passwd)) return;

            foreach (var line in File.ReadAllLines(passwd))
            {
                var parts = line.Split(':');
                if (parts.Length < 7) continue;

                var uid  = int.TryParse(parts[2], out var u) ? u : -1;
                var home = parts[5];
                if (uid < 1000 || string.IsNullOrEmpty(home) || !Directory.Exists(home)) continue; // skip system accounts

                var link = Path.Combine(home, "PeripheralMonitorShared");
                if (Directory.Exists(link) || File.Exists(link)) continue;

                try
                {
                    // symlink: target <- link
                    System.IO.File.CreateSymbolicLink(link, SharedFolder);
                    _log.LogInformation("Linked shared folder into {Home}", home);
                }
                catch (Exception ex) { _log.LogDebug(ex, "Could not symlink into {Home}", home); }
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "LinkIntoUserHomes failed"); }
    }

    /// <summary>Validates a filename is safe (no path traversal, no directory separators) and resolves it to a full path inside the shared folder.</summary>
    public bool TryResolvePath(string fileName, out string fullPath, out string? error)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(fileName))
        {
            error = "File name is required";
            return false;
        }

        if (fileName.Contains('/') || fileName.Contains('\\') ||
            fileName.Contains("..") || Path.IsPathRooted(fileName))
        {
            error = "File name must not contain path separators";
            return false;
        }

        fullPath = Path.Combine(SharedFolder, fileName);
        error = null;
        return true;
    }

    public async Task<AgentCommandResult> SaveFileAsync(FilePushRequest req)
    {
        if (!TryResolvePath(req.FileName, out var path, out var err))
            return new AgentCommandResult(req.CommandId, false, err);

        try
        {
            var bytes = Convert.FromBase64String(req.Base64Content);
            if (bytes.LongLength > MaxFileSizeBytes)
                return new AgentCommandResult(req.CommandId, false, $"File exceeds {MaxFileSizeBytes / (1024 * 1024)}MB limit");

            await File.WriteAllBytesAsync(path, bytes);
            _log.LogInformation("Received file via push: {Path} ({Size} bytes)", path, bytes.LongLength);
            return new AgentCommandResult(req.CommandId, true, $"Saved {req.FileName} ({bytes.LongLength:N0} bytes)");
        }
        catch (FormatException)
        {
            return new AgentCommandResult(req.CommandId, false, "Invalid file content encoding");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save pushed file {Name}", req.FileName);
            return new AgentCommandResult(req.CommandId, false, ex.Message);
        }
    }

    public async Task<FilePullResult> ReadFileAsync(FilePullRequest req)
    {
        if (!TryResolvePath(req.FileName, out var path, out var err))
            return new FilePullResult(req.CommandId, false, err, null, null);

        try
        {
            if (!File.Exists(path))
                return new FilePullResult(req.CommandId, false, "File not found", null, null);

            var info = new FileInfo(path);
            if (info.Length > MaxFileSizeBytes)
                return new FilePullResult(req.CommandId, false, $"File exceeds {MaxFileSizeBytes / (1024 * 1024)}MB limit", null, null);

            var bytes = await File.ReadAllBytesAsync(path);
            _log.LogInformation("Sending file via pull: {Path} ({Size} bytes)", path, bytes.LongLength);
            return new FilePullResult(req.CommandId, true, null, req.FileName, Convert.ToBase64String(bytes));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to read file {Name}", req.FileName);
            return new FilePullResult(req.CommandId, false, ex.Message, null, null);
        }
    }

    public SharedFolderListing ListFiles(string commandId)
    {
        try
        {
            var files = new DirectoryInfo(SharedFolder).GetFiles()
                .Select(f => new SharedFileInfo(f.Name, f.Length, f.LastWriteTimeUtc))
                .OrderByDescending(f => f.ModifiedAt)
                .ToList();
            return new SharedFolderListing(commandId, true, null, SharedFolder, files);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to list shared folder");
            return new SharedFolderListing(commandId, false, ex.Message, SharedFolder, null);
        }
    }
}
