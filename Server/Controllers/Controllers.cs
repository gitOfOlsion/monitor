using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PeripheralMonitor.Server.Data;
using PeripheralMonitor.Server.Services;

namespace PeripheralMonitor.Server.Controllers;

// ════════════════════════════════════════════════════════════════════════
// INSTITUTIONS
// ════════════════════════════════════════════════════════════════════════
[ApiController, Route("api/institutions"), Authorize]
public class InstitutionsController : ControllerBase
{
    private readonly InstitutionService _svc;
    private readonly UserManager<User>  _userMgr;

    public InstitutionsController(InstitutionService svc, UserManager<User> userMgr)
    {
        _svc     = svc;
        _userMgr = userMgr;
    }

    [HttpGet]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> GetAll() =>
        Ok(await _svc.GetAllAsync());

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (!IsSuperAdmin() && GetInstitutionId() != id) return Forbid();
        var inst = await _svc.GetByIdAsync(id);
        return inst == null ? NotFound() : Ok(inst);
    }

    [HttpPost]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> Create([FromBody] CreateInstitutionRequest req)
    {
        var inst = await _svc.CreateAsync(req.Name, req.Type, req.ContactEmail, req.LogoUrl);
        return CreatedAtAction(nameof(GetById), new { id = inst.Id }, inst);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateInstitutionRequest req)
    {
        if (!IsSuperAdmin() && GetInstitutionId() != id) return Forbid();
        var inst = await _svc.UpdateAsync(id, req.Name, req.Type, req.ContactEmail, req.LogoUrl);
        return inst == null ? NotFound() : Ok(inst);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> Delete(Guid id) =>
        await _svc.DeleteAsync(id) ? Ok() : NotFound();

    // ── Templates ─────────────────────────────────────────────────────────
    [HttpGet("{id:guid}/templates")]
    public async Task<IActionResult> GetTemplates(Guid id)
    {
        if (!IsSuperAdmin() && GetInstitutionId() != id) return Forbid();
        return Ok(await _svc.GetTemplatesAsync());
    }

    [HttpPost("{id:guid}/templates")]
    public async Task<IActionResult> CreateTemplate(Guid id, [FromBody] CreateTemplateRequest req)
    {
        if (!IsSuperAdmin() && GetInstitutionId() != id) return Forbid();
        var t = await _svc.CreateTemplateAsync(id.ToString(), req.Name, req.Description, req.ExpiryDays);
        return Ok(t);
    }

    [HttpDelete("{id:guid}/templates/{templateId:guid}")]
    public async Task<IActionResult> DeleteTemplate(Guid id, Guid templateId)
    {
        if (!IsSuperAdmin() && GetInstitutionId() != id) return Forbid();
        return await _svc.DeleteTemplateAsync(templateId) ? Ok() : NotFound();
    }

    private bool IsSuperAdmin() => User.IsInRole("SuperAdmin");
    private Guid? GetInstitutionId()
    {
        var v = User.FindFirst("institution_id")?.Value;
        return Guid.TryParse(v, out var g) ? g : null;
    }
}

public record CreateInstitutionRequest(
    string  Name,
    string  Type,
    string  ContactEmail,
    string? LogoUrl = null
);

public record CreateTemplateRequest(
    string  Name,
    string? Description = null,
    int     ExpiryDays  = 365
);

// ════════════════════════════════════════════════════════════════════════
// AGENTS
// ════════════════════════════════════════════════════════════════════════
[ApiController, Route("api/agents"), Authorize]
public class AgentsController : ControllerBase
{
    private readonly AgentService _agents;

    public AgentsController(AgentService agents) => _agents = agents;

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var instId = IsSuperAdmin() ? (Guid?)null : GetInstitutionId();
        return Ok(await _agents.GetAllAsync(instId));
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, [FromQuery] string approvedBy = "Admin")
    {
        var instId = GetInstitutionId() ?? Guid.Empty;
        var (ok, token) = await _agents.ApproveAsync(id, approvedBy, instId, IsSuperAdmin());
        return ok
            ? Ok(new { message = "Approved", token })
            : NotFound(new { message = "Agent not found or access denied" });
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id)
    {
        var instId = GetInstitutionId() ?? Guid.Empty;
        return await _agents.RejectAsync(id, instId, IsSuperAdmin())
            ? Ok() : NotFound();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var instId = GetInstitutionId() ?? Guid.Empty;
        return await _agents.DeleteAsync(id, instId, IsSuperAdmin())
            ? Ok() : NotFound(new { message = "Not found or agent is approved" });
    }

    private bool IsSuperAdmin() => User.IsInRole("SuperAdmin");
    private Guid? GetInstitutionId()
    {
        var v = User.FindFirst("institution_id")?.Value;
        return Guid.TryParse(v, out var g) ? g : null;
    }
}

// ════════════════════════════════════════════════════════════════════════
// EVENTS
// ════════════════════════════════════════════════════════════════════════
[ApiController, Route("api/events"), Authorize]
public class EventsController : ControllerBase
{
    private readonly AgentService _agents;

    public EventsController(AgentService agents) => _agents = agents;

    [HttpGet]
    public async Task<IActionResult> GetRecent(
        [FromQuery] int   count   = 200,
        [FromQuery] Guid? agentId = null)
    {
        var instId = IsSuperAdmin() ? (Guid?)null : GetInstitutionId();
        return Ok(await _agents.GetRecentEventsAsync(count, instId, agentId));
    }

    private bool IsSuperAdmin() => User.IsInRole("SuperAdmin");
    private Guid? GetInstitutionId()
    {
        var v = User.FindFirst("institution_id")?.Value;
        return Guid.TryParse(v, out var g) ? g : null;
    }
}
