using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using PeripheralMonitor.Server;
using PeripheralMonitor.Server.Services;
using PeripheralMonitor.Shared.Models;
using PeripheralMonitor.Shared.Security;

var builder = WebApplication.CreateBuilder(args);

// 1. Register Services
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<DiscoveryListener>();
builder.Services.AddHostedService<DiscoveryHostedService>();

// 2. SignalR
builder.Services.AddSignalR();

// 3. JWT (Kept for compatibility, but we will disable enforcement below)
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => { /* config omitted for brevity */ });

// 4. CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy
            .AllowAnyMethod()
            .AllowAnyHeader()
            .SetIsOriginAllowed(_ => true)
            .AllowCredentials());
});

var app = builder.Build();
app.UseDeveloperExceptionPage();
app.UseCors("AllowAll");
app.UseRouting();
//app.UseAuthentication();
//app.UseAuthorization();

// === PUBLIC ENDPOINTS (NO PASSWORD REQUIRED) ===
app.MapGet("/", () => "Server Running");
app.MapGet("/health", () => new { status = "ok", timestamp = DateTime.UtcNow });

// ✅ THIS IS THE MISSING ENDPOINT THAT CAUSED THE 404
app.MapGet("/api/agents", (AgentRegistry registry) => 
    Results.Json(registry.GetAll()));

app.MapPost("/api/agents/{macAddress}/approve", (string macAddress, string approvedBy, AgentRegistry registry) =>
{
    var success = registry.Approve(macAddress, approvedBy);
    if (!success)
        return Results.NotFound(new { message = $"Agent {macAddress} not found or already approved" });

    // ✅ Read back the approved agent to get the generated token
    registry.TryGetAgent(macAddress, out var agent);
    return Results.Ok(new
    {
        message = $"{macAddress} approved by {approvedBy}",
        token   = agent?.JwtToken
    });
});

// ✅ ADD THIS TOO (for the Delete button)
app.MapDelete("/api/agents/{macAddress}", (string macAddress, AgentRegistry registry) =>
{
    var success = registry.Delete(macAddress);
    return success
        ? Results.Ok()
        : Results.NotFound(new { message = $"Agent {macAddress} not found" });
});


// ✅ SIGNALR HUB (NO PASSWORD REQUIRED FOR TESTING)
app.MapHub<MonitorHub>("/monitor"); 

app.Run("http://0.0.0.0:5000");