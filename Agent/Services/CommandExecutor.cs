using System.Diagnostics;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Agent.Services;

/// <summary>
/// Executes power commands (restart / shutdown / cancel) issued by the server.
/// Windows: shutdown.exe. Linux: shutdown command (requires the service account
/// to have appropriate sudo/polkit rights — see install.sh).
/// </summary>
public class CommandExecutor
{
    private readonly ILogger<CommandExecutor> _log;

    public CommandExecutor(ILogger<CommandExecutor> log) => _log = log;

    public async Task<AgentCommandResult> ExecuteAsync(AgentCommandPayload cmd)
    {
        try
        {
            return cmd.Type switch
            {
                AgentCommandType.Restart       => await RunPowerCommand(cmd, restart: true),
                AgentCommandType.Shutdown      => await RunPowerCommand(cmd, restart: false),
                AgentCommandType.CancelShutdown => await CancelAsync(cmd),
                _ => new AgentCommandResult(cmd.CommandId, false, $"Unsupported command: {cmd.Type}")
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Command {Id} ({Type}) failed", cmd.CommandId, cmd.Type);
            return new AgentCommandResult(cmd.CommandId, false, ex.Message);
        }
    }

    private async Task<AgentCommandResult> RunPowerCommand(AgentCommandPayload cmd, bool restart)
    {
        var delay = Math.Max(cmd.DelaySeconds ?? 60, 0);
        _log.LogWarning("Executing {Type} in {Delay}s (command {Id})", cmd.Type, delay, cmd.CommandId);

        (string file, string args) = OperatingSystem.IsWindows()
            ? ("shutdown.exe", restart ? $"/r /t {delay} /c \"Remote restart initiated by Peripheral Monitor\""
                                       : $"/s /t {delay} /c \"Remote shutdown initiated by Peripheral Monitor\"")
            : ("sudo", restart ? $"shutdown -r +{Math.Max(delay / 60, 1)}" : $"shutdown -h +{Math.Max(delay / 60, 1)}");

        var exit = await RunProcessAsync(file, args);
        return exit == 0
            ? new AgentCommandResult(cmd.CommandId, true, $"{cmd.Type} scheduled in {delay}s")
            : new AgentCommandResult(cmd.CommandId, false, $"{file} exited with code {exit}");
    }

    private async Task<AgentCommandResult> CancelAsync(AgentCommandPayload cmd)
    {
        (string file, string args) = OperatingSystem.IsWindows()
            ? ("shutdown.exe", "/a")
            : ("sudo", "shutdown -c");

        var exit = await RunProcessAsync(file, args);
        // On Windows, /a returns nonzero if no shutdown was pending — treat as success either way.
        return new AgentCommandResult(cmd.CommandId, true,
            exit == 0 ? "Pending shutdown/restart cancelled" : "No pending shutdown to cancel");
    }

    private async Task<int> RunProcessAsync(string file, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}");
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(err))
                _log.LogWarning("{File} {Args} -> exit {Code}: {Err}", file, args, proc.ExitCode, err.Trim());
        }
        return proc.ExitCode;
    }
}
