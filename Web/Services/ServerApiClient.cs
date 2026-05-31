using System.Net.Http.Json;
using PeripheralMonitor.Server.Data;

namespace PeripheralMonitor.Web.Services;

public class ServerApiClient
{
    private readonly HttpClient _http;
    public ServerApiClient(HttpClient http) => _http = http;

    public Task<List<AgentEntity>?> GetAgentsAsync() =>
        _http.GetFromJsonAsync<List<AgentEntity>>("/api/agents");

    public Task<List<DeviceEventEntity>?> GetEventsAsync(int count = 200, string? machine = null)
    {
        var url = $"/api/events?count={count}";
        if (!string.IsNullOrEmpty(machine)) url += $"&machineName={Uri.EscapeDataString(machine)}";
        return _http.GetFromJsonAsync<List<DeviceEventEntity>>(url);
    }

    public async Task<bool> ApproveAgentAsync(string macAddress, string by = "Admin")
    {
        var r = await _http.PostAsync($"/api/agents/{macAddress}/approve?approvedBy={by}", null);
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> RejectAgentAsync(string macAddress)
    {
        var r = await _http.PostAsync($"/api/agents/{macAddress}/reject", null);
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteAgentAsync(string macAddress)
    {
        var r = await _http.DeleteAsync($"/api/agents/{macAddress}");
        return r.IsSuccessStatusCode;
    }
}
