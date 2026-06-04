using System.Net.Http.Json;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Web.Services;

public class ServerApiClient
{
    private readonly HttpClient _http;

    public ServerApiClient(HttpClient http) => _http = http;

    // ── Agents ────────────────────────────────────────────────────────────
    public Task<List<AgentDto>?> GetAgentsAsync() =>
        _http.GetFromJsonAsync<List<AgentDto>>("/api/agents");

    public async Task<bool> ApproveAsync(Guid id) =>
        (await _http.PostAsync($"/api/agents/{id}/approve", null)).IsSuccessStatusCode;

    public async Task<bool> RejectAsync(Guid id) =>
        (await _http.PostAsync($"/api/agents/{id}/reject", null)).IsSuccessStatusCode;

    public async Task<bool> DeleteAsync(Guid id) =>
        (await _http.DeleteAsync($"/api/agents/{id}")).IsSuccessStatusCode;

    // ── Events ────────────────────────────────────────────────────────────
    public Task<List<DeviceEventDto>?> GetEventsAsync(int count = 200, Guid? agentId = null)
    {
        var url = $"/api/events?count={count}";
        if (agentId.HasValue) url += $"&agentId={agentId}";
        return _http.GetFromJsonAsync<List<DeviceEventDto>>(url);
    }

    // ── Institutions (SuperAdmin only) ────────────────────────────────────
    public Task<List<InstitutionDto>?> GetInstitutionsAsync() =>
        _http.GetFromJsonAsync<List<InstitutionDto>>("/api/institutions");

    public async Task<InstitutionDto?> CreateInstitutionAsync(string name, string type, string email)
    {
        var r = await _http.PostAsJsonAsync("/api/institutions", new { name, type, contactEmail = email });
        return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<InstitutionDto>() : null;
    }

    public async Task<string?> GetProvisionTokenAsync(Guid institutionId, Guid? templateId = null)
    {
        var url = $"/api/institutions/{institutionId}/provision-token";
        if (templateId.HasValue) url += $"?templateId={templateId}";
        var r = await _http.GetFromJsonAsync<ProvisionResponse>(url);
        return r?.Token;
    }

    // ── Templates (SuperAdmin only) ───────────────────────────────────────
    public Task<List<AgentTemplateDto>?> GetTemplatesAsync() =>
        _http.GetFromJsonAsync<List<AgentTemplateDto>>("/api/templates");

    public async Task<AgentTemplateDto?> CreateTemplateAsync(string name, string description)
    {
        var r = await _http.PostAsJsonAsync("/api/templates", new { name, description });
        return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<AgentTemplateDto>() : null;
    }

    // ── Categories ────────────────────────────────────────────────────────
    public Task<List<DeviceCategoryDto>?> GetCategoriesAsync() =>
        _http.GetFromJsonAsync<List<DeviceCategoryDto>>("/api/categories");

    private record ProvisionResponse(string Token);
}
