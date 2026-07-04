using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Web.Controllers;

[Route("auth")]
public class AuthController : Controller
{
    private readonly IHttpClientFactory      _httpFactory;
    private readonly IConfiguration          _config;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IHttpClientFactory httpFactory, IConfiguration config, ILogger<AuthController> logger)
    {
        _httpFactory = httpFactory;
        _config      = config;
        _logger      = logger;
    }

    // POST /auth/login — validate against Server's user database
    [HttpPost("login")]
    public async Task<IActionResult> Login(
        [FromForm] string  username,
        [FromForm] string  password,
        [FromForm] string? returnUrl)
    {
        var serverUrl = _config["ServerApiUrl"] ?? "http://server:5000";
        var http = _httpFactory.CreateClient();
        http.BaseAddress = new Uri(serverUrl);

        ValidateLoginResponse? result = null;
        try
        {
            var resp = await http.PostAsJsonAsync("/api/auth/validate",
                new ValidateLoginRequest(username, password));
            if (resp.IsSuccessStatusCode)
                result = await resp.Content.ReadFromJsonAsync<ValidateLoginResponse>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login validation call failed");
            return Redirect("/login?showError=true");
        }

        if (result is not { Success: true })
        {
            _logger.LogWarning("Failed login: {User} from {IP}",
                username, HttpContext.Connection.RemoteIpAddress);
            return Redirect("/login?showError=true");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name,           result.UserName ?? username),
            new(ClaimTypes.Role,           result.Role ?? "Viewer"),
            new("user_id",                 result.UserId ?? ""),
        };
        if (result.InstitutionId.HasValue)
            claims.Add(new Claim("institution_id", result.InstitutionId.Value.ToString()));
        if (!string.IsNullOrEmpty(result.InstitutionName))
            claims.Add(new Claim("institution_name", result.InstitutionName));

        await HttpContext.SignInAsync("Cookies",
            new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies")),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });

        _logger.LogInformation("Login: {User} ({Role})", result.UserName, result.Role);
        var safe = string.IsNullOrEmpty(returnUrl) || !Url.IsLocalUrl(returnUrl) || returnUrl == "/" ? "/dashboard" : returnUrl;
        return LocalRedirect(safe);
    }

    [HttpGet("logout")]
    public async Task<IActionResult> Logout()
    {
        var user = HttpContext.User.Identity?.Name ?? "unknown";
        var role = HttpContext.User.FindFirstValue(ClaimTypes.Role) ?? "?";
        var instRaw = HttpContext.User.FindFirstValue("institution_id");
        Guid? inst = Guid.TryParse(instRaw, out var g) ? g : null;

        // Fire-and-forget audit
        try
        {
            var serverUrl = _config["ServerApiUrl"] ?? "http://server:5000";
            var http = _httpFactory.CreateClient();
            http.BaseAddress = new Uri(serverUrl);
            await http.PostAsJsonAsync("/api/audit",
                new CreateAuditRequest(user, role, inst, "auth.logout", user, null,
                    HttpContext.Connection.RemoteIpAddress?.ToString()));
        }
        catch { /* best effort */ }

        await HttpContext.SignOutAsync("Cookies");
        return Redirect("/");
    }
}
