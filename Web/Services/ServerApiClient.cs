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

    public Task<List<AgentMetricsDto>?> GetMetricsAsync()
    { AttachIdentity(); return _http.GetFromJsonAsync<List<AgentMetricsDto>>("/api/agents/metrics"); }

    public async Task<bool> ApproveAsync(Guid id)
    { AttachIdentity(); return (await _http.PostAsync($"/api/agents/{id}/approve", null)).IsSuccessStatusCode; }

    public async Task<bool> RejectAsync(Guid id)
    { AttachIdentity(); return (await _http.PostAsync($"/api/agents/{id}/reject", null)).IsSuccessStatusCode; }

    public async Task<bool> DeleteAsync(Guid id)
    { AttachIdentity(); return (await _http.DeleteAsync($"/api/agents/{id}")).IsSuccessStatusCode; }

    /// <summary>Send a remote command (Restart, Shutdown, CancelShutdown) to an online agent.</summary>
    public async Task<(bool ok, string? message)> SendCommandAsync(Guid agentId, string type, int? delaySeconds = null)
    {
        AttachIdentity();
        var r = await _http.PostAsJsonAsync($"/api/agents/{agentId}/command", new { type, delaySeconds });
        var body = await r.Content.ReadFromJsonAsync<CommandResponse>();
        if (r.IsSuccessStatusCode) return (body?.Success ?? false, body?.Message);
        return (false, body?.Message ?? body?.Error ?? "Command failed");
    }

    private record CommandResponse(bool Success, string? Message, string? Error);

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

    public async Task<(bool ok, string? error)> UpdateUserAsync(string id, UpdateUserRequest req)
    {
        AttachIdentity();
        var r = await _http.PutAsJsonAsync($"/api/users/{id}", req);
        if (r.IsSuccessStatusCode) return (true, null);
        var body = await r.Content.ReadFromJsonAsync<ErrorResponse>();
        return (false, body?.Error ?? "Failed to update user");
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

    // ── File share (push/pull to agent's shared folder) ──────────────────────
    public async Task<(bool ok, string? folderPath, List<SharedFileDto> files, string? error)> ListSharedFilesAsync(Guid agentId)
    {
        AttachIdentity();
        var r = await _http.GetAsync($"/api/agents/{agentId}/files");
        if (!r.IsSuccessStatusCode)
        {
            var err = await r.Content.ReadFromJsonAsync<ErrorResponse>();
            return (false, null, [], err?.Error ?? "Failed to list files");
        }
        var body = await r.Content.ReadFromJsonAsync<FileListResponse>();
        return (true, body?.FolderPath, body?.Files ?? [], null);
    }

    public async Task<(bool ok, string? message)> PushFileAsync(Guid agentId, string fileName, byte[] content)
    {
        AttachIdentity();
        var base64 = Convert.ToBase64String(content);
        var r = await _http.PostAsJsonAsync($"/api/agents/{agentId}/files/push", new { fileName, base64Content = base64 });
        var body = await r.Content.ReadFromJsonAsync<CommandResponse>();
        if (r.IsSuccessStatusCode) return (body?.Success ?? false, body?.Message);
        return (false, body?.Message ?? body?.Error ?? "Push failed");
    }

    /// <summary>Returns the raw bytes of a file pulled from the agent, or null on failure.</summary>
    public async Task<(byte[]? bytes, string? error)> PullFileAsync(Guid agentId, string fileName)
    {
        AttachIdentity();
        var r = await _http.GetAsync($"/api/agents/{agentId}/files/pull?fileName={Uri.EscapeDataString(fileName)}");
        if (!r.IsSuccessStatusCode)
        {
            var err = await r.Content.ReadFromJsonAsync<ErrorResponse>();
            return (null, err?.Error ?? "Pull failed");
        }
        return (await r.Content.ReadAsByteArrayAsync(), null);
    }

    public async Task<(bool ok, string? error)> DeleteSharedFileAsync(Guid agentId, string fileName)
    {
        AttachIdentity();
        var r = await _http.DeleteAsync($"/api/agents/{agentId}/files?fileName={Uri.EscapeDataString(fileName)}");
        if (r.IsSuccessStatusCode) return (true, null);
        var err = await r.Content.ReadFromJsonAsync<ErrorResponse>();
        return (false, err?.Error ?? "Delete failed");
    }

    // ── Remote desktop (Guacamole) ───────────────────────────────────────────
    public async Task<(bool ok, string? url, string? protocol, string? error)> GetRemoteDesktopUrlAsync(Guid agentId)
    {
        AttachIdentity();
        var r = await _http.GetAsync($"/api/agents/{agentId}/remote-desktop");
        if (!r.IsSuccessStatusCode)
        {
            var err = await r.Content.ReadFromJsonAsync<ErrorResponse>();
            return (false, null, null, err?.Error ?? "Remote desktop unavailable");
        }
        var body = await r.Content.ReadFromJsonAsync<RemoteDesktopResponse>();
        return (true, body?.Url, body?.Protocol, null);
    }

    public async Task<(bool configured, string? username, string? domain)> GetRdpCredentialStatusAsync(Guid institutionId)
    {
        AttachIdentity();
        var r = await _http.GetAsync($"/api/institutions/{institutionId}/rdp-credentials");
        if (!r.IsSuccessStatusCode) return (false, null, null);
        var body = await r.Content.ReadFromJsonAsync<RdpCredentialStatusResponse>();
        return (body?.Configured ?? false, body?.Username, body?.Domain);
    }

    public async Task<bool> SetRdpCredentialsAsync(Guid institutionId, string username, string password, string? domain)
    {
        AttachIdentity();
        var r = await _http.PutAsJsonAsync($"/api/institutions/{institutionId}/rdp-credentials",
            new { username, password, domain });
        return r.IsSuccessStatusCode;
    }

    private record RemoteDesktopResponse(string Url, string Protocol);
    private record RdpCredentialStatusResponse(bool Configured, string? Username, string? Domain);

    private record FileListResponse(string? FolderPath, List<SharedFileDto> Files);

    private record ProvisionResponse(string Token);
    private record ErrorResponse(string Error);
}
