using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeripheralMonitor.Server.Services;

namespace PeripheralMonitor.Server.Controllers;

static class Caller
{
    public static Guid?   InstId(ClaimsPrincipal u) => Guid.TryParse(u.FindFirstValue("institution_id"), out var id) ? id : null;
    public static bool    IsSuperAdmin(ClaimsPrincipal u) => u.IsInRole("SuperAdmin");
    public static string  Name(ClaimsPrincipal u) => u.FindFirstValue(ClaimTypes.Name) ?? "unknown";
}

[ApiController, Route("api/agents"), Authorize]
public class AgentsController : ControllerBase
{
    private readonly AgentService _svc;
    public AgentsController(AgentService svc) => _svc = svc;

    [HttpGet]
    public async Task<IActionResult> GetAll()
        => Ok(await _svc.GetAgentsAsync(Caller.IsSuperAdmin(User) ? null : Caller.InstId(User)));

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id)
    {
        var (ok, token) = await _svc.ApproveAsync(id, Caller.Name(User), Caller.IsSuperAdmin(User) ? null : Caller.InstId(User));
        return ok ? Ok(new { token }) : NotFound();
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id)
        => await _svc.RejectAsync(id, Caller.IsSuperAdmin(User) ? null : Caller.InstId(User)) ? Ok() : NotFound();

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
        => await _svc.DeleteAsync(id, Caller.IsSuperAdmin(User) ? null : Caller.InstId(User)) ? Ok() : NotFound();
}

[ApiController, Route("api/events"), Authorize]
public class EventsController : ControllerBase
{
    private readonly AgentService _svc;
    public EventsController(AgentService svc) => _svc = svc;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int count = 200, [FromQuery] Guid? agentId = null)
        => Ok(await _svc.GetEventsAsync(count, Caller.IsSuperAdmin(User) ? null : Caller.InstId(User), agentId));
}

[ApiController, Route("api/institutions"), Authorize(Roles = "SuperAdmin")]
public class InstitutionsController : ControllerBase
{
    private readonly AgentService _svc;
    public InstitutionsController(AgentService svc) => _svc = svc;

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _svc.GetInstitutionsAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateInstRequest r)
        => Ok(await _svc.CreateInstitutionAsync(r.Name, r.Type, r.ContactEmail));

    [HttpGet("{id:guid}/provision-token")]
    public async Task<IActionResult> ProvisionToken(Guid id, [FromQuery] Guid? templateId)
        => Ok(new { token = await _svc.GenerateProvisionTokenAsync(id, templateId) });
}
public record CreateInstRequest(string Name, string Type, string ContactEmail);

[ApiController, Route("api/templates"), Authorize(Roles = "SuperAdmin")]
public class AgentTemplatesController : ControllerBase
{
    private readonly AgentService _svc;
    public AgentTemplatesController(AgentService svc) => _svc = svc;

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _svc.GetTemplatesAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTplRequest r)
        => Ok(await _svc.CreateTemplateAsync(r.Name, r.Description));
}
public record CreateTplRequest(string Name, string Description);

[ApiController, Route("api/categories"), Authorize]
public class CategoriesController : ControllerBase
{
    private readonly AgentService _svc;
    public CategoriesController(AgentService svc) => _svc = svc;

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _svc.GetCategoriesAsync());
}
