using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeripheralMonitor.Server.Data;
using PeripheralMonitor.Server.Services;

namespace PeripheralMonitor.Server.Controllers;

[ApiController]
[Route("api/agents")]
[Authorize(Policy = "DashboardOnly")]
public class AgentsController : ControllerBase
{
    private readonly AgentService _agents;
    public AgentsController(AgentService agents) => _agents = agents;

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _agents.GetAllAsync());

    [HttpPost("{macAddress}/approve")]
    public async Task<IActionResult> Approve(string macAddress, [FromQuery] string approvedBy = "Admin")
    {
        var (success, token) = await _agents.ApproveAsync(macAddress, approvedBy);
        return success
            ? Ok(new { message = $"{macAddress} approved", token })
            : NotFound(new { message = $"Agent {macAddress} not found" });
    }

    [HttpPost("{macAddress}/reject")]
    public async Task<IActionResult> Reject(string macAddress)
    {
        var ok = await _agents.RejectAsync(macAddress);
        return ok ? Ok() : NotFound();
    }

    [HttpDelete("{macAddress}")]
    public async Task<IActionResult> Delete(string macAddress)
    {
        var ok = await _agents.DeleteAsync(macAddress);
        return ok ? Ok() : NotFound(new { message = "Agent not found or is approved (cannot delete approved agents)" });
    }
}

[ApiController]
[Route("api/events")]
[Authorize(Policy = "DashboardOnly")]
public class EventsController : ControllerBase
{
    private readonly AgentService _agents;
    public EventsController(AgentService agents) => _agents = agents;

    [HttpGet]
    public async Task<IActionResult> GetRecent(
        [FromQuery] int count = 200,
        [FromQuery] string? machineName = null) =>
        Ok(await _agents.GetRecentEventsAsync(count, machineName));
}
