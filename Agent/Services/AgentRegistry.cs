using System.Collections.Concurrent;
using System.Text.Json;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Server.Services;

public class AgentRegistry
{
    private readonly ConcurrentDictionary<string, AgentRegistration> _agents = new();
    private readonly string _persistPath = "agents.json";
    private readonly SemaphoreSlim _lock = new(1,1);

    public AgentRegistry()
    {
        LoadFromFile();
        _ = PeriodicSaveAsync();
    }

    public bool TryGetAgent(string macAddress, out AgentRegistration agent) =>
        _agents.TryGetValue(macAddress, out agent);

    public void AddOrUpdate(AgentRegistration agent)
    {
        _agents.AddOrUpdate(agent.MacAddress, agent, (_, _) => agent);
        _ = SaveToFileAsync();
    }

    public IEnumerable<AgentRegistration> GetAll() => _agents.Values;

    public bool Approve(string macAddress, string approvedBy)
    {
        if (_agents.TryGetValue(macAddress, out var agent))
       {
        // ✅ Allow re-approval even if already approved
        var token = Shared.Security.JwtHelper.GenerateToken(agent.MachineName, macAddress);
        var approved = agent with
        {
            Status     = AgentStatus.Approved,
            ApprovedBy = approvedBy,
            ApprovedAt = DateTime.UtcNow,
            JwtToken   = token
        };
        _agents[macAddress] = approved;
        _ = SaveToFileAsync();
        return true;
       }
    return false;
    }

    public bool Reject(string macAddress)
    {
        if (_agents.TryRemove(macAddress, out var agent))
        {
            _ = SaveToFileAsync();
            return true;
        }
        return false;
    }

    public bool Delete(string macAddress) // Remote delete for unapproved
    {
        if (_agents.TryGetValue(macAddress, out var agent) && agent.Status != AgentStatus.Approved)
        {
            _agents.TryRemove(macAddress, out _);
            _ = SaveToFileAsync();
            return true;
        }
        return false;
    }

    private void LoadFromFile()
    {
        if (File.Exists(_persistPath))
        {
            try
            {
                var json = File.ReadAllText(_persistPath);
                var list = JsonSerializer.Deserialize<List<AgentRegistration>>(json);
                if (list != null)
                    foreach (var agent in list)
                        _agents[agent.MacAddress] = agent;
            }
            catch { /* Ignore corrupt file */ }
        }
    }

    private async Task SaveToFileAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(_agents.Values, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_persistPath, json);
        }
        finally { _lock.Release(); }
    }

    private async Task PeriodicSaveAsync()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(5));
            await SaveToFileAsync();
        }
    }
}