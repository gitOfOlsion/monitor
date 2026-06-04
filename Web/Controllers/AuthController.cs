using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace PeripheralMonitor.Web.Controllers;

[Route("auth")]
public class AuthController : Controller
{
    private readonly IConfiguration          _config;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IConfiguration config, ILogger<AuthController> logger)
    {
        _config = config;
        _logger = logger;
    }

    // POST /auth/login  (form submit from Login.razor)
    [HttpPost("login")]
    public async Task<IActionResult> Login(
        [FromForm] string  username,
        [FromForm] string  password,
        [FromForm] string? returnUrl)
    {
        var adminUser = _config["Auth:AdminUsername"] ?? "superadmin";
        var adminPass = _config["Auth:AdminPassword"] ?? "Admin@1234!";

        if (username != adminUser || password != adminPass)
        {
            _logger.LogWarning("Failed login: {User} from {IP}",
                username, HttpContext.Connection.RemoteIpAddress);
            return Redirect("/login?showError=true");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(ClaimTypes.Role, "SuperAdmin"),
        };

        await HttpContext.SignInAsync("Cookies",
            new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies")),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });

        _logger.LogInformation("Login: {User}", username);
        var safe = string.IsNullOrEmpty(returnUrl) || !Url.IsLocalUrl(returnUrl) ? "/" : returnUrl;
        return LocalRedirect(safe);
    }

    // GET /auth/logout
    [HttpGet("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync("Cookies");
        return Redirect("/login");
    }
}
