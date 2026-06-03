using System.Net.Http.Json;
using PeripheralMonitor.Server.Data;

namespace PeripheralMonitor.Web.Services;

public class ServerApiClient
{
    private readonly HttpClient _http;
    public ServerApiClient(HttpClient http) => _http = http;

    public Task<List<AgentEntity>?>        GetAgentsAsync()        => _http.GetFromJsonAsync<List<AgentEntity>>("/api/agents");
    public Task<List<InstitutionEntity>?>  GetInstitutionsAsync()  => _http.GetFromJsonAsync<List<InstitutionEntity>>("/api/institutions");
    public Task<List<AgentTemplateEntity>?> GetTemplatesAsync()    => _http.GetFromJsonAsync<List<AgentTemplateEntity>>("/api/templates");
    public Task<List<DeviceCategoryEntity>?> GetCategoriesAsync()  => _http.GetFromJsonAsync<List<DeviceCategoryEntity>>("/api/categories");

    public Task<List<DeviceEventEntity>?> GetEventsAsync(int count = 200, Guid? agentId = null)
    {
        var url = $"/api/events?count={count}";
        if (agentId.HasValue) url += $"&agentId={agentId}";
        return _http.GetFromJsonAsync<List<DeviceEventEntity>>(url);
    }

    public async Task<bool> ApproveAsync(Guid id)   => (await _http.PostAsync($"/api/agents/{id}/approve", null)).IsSuccessStatusCode;
    public async Task<bool> RejectAsync(Guid id)    => (await _http.PostAsync($"/api/agents/{id}/reject",  null)).IsSuccessStatusCode;
    public async Task<bool> DeleteAsync(Guid id)    => (await _http.DeleteAsync($"/api/agents/{id}")).IsSuccessStatusCode;

    public async Task<InstitutionEntity?> CreateInstitutionAsync(string name, string type, string email)
    {
        var r = await _http.PostAsJsonAsync("/api/institutions", new { name, type, contactEmail = email });
        return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<InstitutionEntity>() : null;
    }

    public async Task<string?> GetProvisionTokenAsync(Guid institutionId, Guid? templateId = null)
    {
        var url = $"/api/institutions/{institutionId}/provision-token";
        if (templateId.HasValue) url += $"?templateId={templateId}";
        var r = await _http.GetFromJsonAsync<ProvisionTokenResponse>(url);
        return r?.Token;
    }

    public async Task<AgentTemplateEntity?> CreateTemplateAsync(string name, string description)
    {
        var r = await _http.PostAsJsonAsync("/api/templates", new { name, description });
        return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<AgentTemplateEntity>() : null;
    }

    private record ProvisionTokenResponse(string Token);
}
