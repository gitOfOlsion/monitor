using System.Net.Http.Json;
using System.Security.Claims;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Web.Services;

// Forwards the logged-in user's identity to the Server API via headers on every call.
// Server uses these (X-Actor / X-Actor-Role / X-Institution-Id) for scoping + audit.
public class ServerApiClient
{
    private readonly HttpClient            _http;
    private readonly IHttpContextAccessor  _ctx;

    public ServerApiClient(HttpClient http, IHttpContextAccessor ctx)
    {
        _http = http;
        _ctx  = ctx;
    }

    private void AttachIdentity()
    {
        var user = _ctx.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return;

        _http.DefaultRequestHeaders.Remove("X-Actor");
        _http.DefaultRequestHeaders.Remove("X-Actor-Role");
        _http.DefaultRequestHeaders.Remove("X-Institution-Id");

        _http.DefaultRequestHeaders.Add("X-Actor", user.Identity.Name ?? "unknown");
        _http.DefaultRequestHeaders.Add("X-Actor-Role", user.FindFirstValue(ClaimTypes.Role) ?? "Viewer");
        var inst = user.FindFirstValue("institution_id");
        if (!string.IsNullOrEmpty(inst))
            _http.DefaultRequestHeaders.Add("X-Institution-Id", inst);
    }

    // ── Agents ────────────────────────────────────────────────────────────
    public Task<List<AgentDto>?> GetAgentsAsync()
    { AttachIdentity(); return _http.GetFromJsonAsync<List<AgentDto>>("/api/agents"); }

    public async Task<bool> ApproveAsync(Guid id)
    { AttachIdentity(); return (await _http.PostAsync($"/api/agents/{id}/approve", null)).IsSuccessStatusCode; }

    public async Task<bool> RejectAsync(Guid id)
    { AttachIdentity(); return (await _http.PostAsync($"/api/agents/{id}/reject", null)).IsSuccessStatusCode; }

    public async Task<bool> DeleteAsync(Guid id)
    { AttachIdentity(); return (await _http.DeleteAsync($"/api/agents/{id}")).IsSuccessStatusCode; }

    // ── Events ────────────────────────────────────────────────────────────
    public Task<List<DeviceEventDto>?> GetEventsAsync(int count = 200, Guid? agentId = null)
    {
        AttachIdentity();
        var url = $"/api/events?count={count}";
        if (agentId.HasValue) url += $"&agentId={agentId}";
        return _http.GetFromJsonAsync<List<DeviceEventDto>>(url);
    }

    // ── Institutions ──────────────────────────────────────────────────────
    public Task<List<InstitutionDto>?> GetInstitutionsAsync()
    { AttachIdentity(); return _http.GetFromJsonAsync<List<InstitutionDto>>("/api/institutions"); }

    public async Task<InstitutionDto?> CreateInstitutionAsync(string name, string type, string email)
    {
        AttachIdentity();
        var r = await _http.PostAsJsonAsync("/api/institutions", new { name, type, contactEmail = email });
        return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<InstitutionDto>() : null;
    }

    public async Task<string?> GetProvisionTokenAsync(Guid institutionId, Guid? templateId = null)
    {
        AttachIdentity();
        var url = $"/api/institutions/{institutionId}/provision-token";
        if (templateId.HasValue) url += $"?templateId={templateId}";
        var r = await _http.GetFromJsonAsync<ProvisionResponse>(url);
        return r?.Token;
    }

    // ── Users ─────────────────────────────────────────────────────────────
    public Task<List<UserDto>?> GetUsersAsync()
    { AttachIdentity(); return _http.GetFromJsonAsync<List<UserDto>>("/api/users"); }

    public async Task<(bool ok, string? error)> CreateUserAsync(CreateUserRequest req)
    {
        AttachIdentity();
        var r = await _http.PostAsJsonAsync("/api/users", req);
        if (r.IsSuccessStatusCode) return (true, null);
        var body = await r.Content.ReadFromJsonAsync<ErrorResponse>();
        return (false, body?.Error ?? "Failed to create user");
    }

    public async Task<(bool ok, string? error)> DeleteUserAsync(string id)
    {
        AttachIdentity();
        var r = await _http.DeleteAsync($"/api/users/{id}");
        if (r.IsSuccessStatusCode) return (true, null);
        var body = await r.Content.ReadFromJsonAsync<ErrorResponse>();
        return (false, body?.Error ?? "Failed to delete user");
    }

    // ── Audit ─────────────────────────────────────────────────────────────
    public Task<List<AuditLogDto>?> GetAuditLogsAsync(int count = 500)
    { AttachIdentity(); return _http.GetFromJsonAsync<List<AuditLogDto>>($"/api/audit?count={count}"); }

    // ── Templates ─────────────────────────────────────────────────────────
    public Task<List<AgentTemplateDto>?> GetTemplatesAsync()
    { AttachIdentity(); return _http.GetFromJsonAsync<List<AgentTemplateDto>>("/api/templates"); }

    public async Task<AgentTemplateDto?> CreateTemplateAsync(string name, string description)
    {
        AttachIdentity();
        var r = await _http.PostAsJsonAsync("/api/templates", new { name, description });
        return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<AgentTemplateDto>() : null;
    }

    // ── Categories ──────────────────────────────────────────────────────────
    public Task<List<DeviceCategoryDto>?> GetCategoriesAsync()
    { AttachIdentity(); return _http.GetFromJsonAsync<List<DeviceCategoryDto>>("/api/categories"); }

    private record ProvisionResponse(string Token);
    private record ErrorResponse(string Error);
}
