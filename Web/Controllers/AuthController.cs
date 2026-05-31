using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PeripheralMonitor.Server.Data;

namespace PeripheralMonitor.Web.Controllers;

[Route("api/auth")]
public class AuthController : Controller
{
    private readonly SignInManager<User> _signIn;
    private readonly ILogger<AuthController> _logger;

    public AuthController(SignInManager<User> signIn, ILogger<AuthController> logger)
    {
        _signIn = signIn;
        _logger = logger;
    }

    [HttpGet("login")]
    public async Task<IActionResult> Login(string username, string password, string returnUrl = "/")
    {
        var result = await _signIn.PasswordSignInAsync(username, password,
            isPersistent: true, lockoutOnFailure: false);

        if (result.Succeeded)
        {
            _logger.LogInformation("Login: {User}", username);
            return LocalRedirect(returnUrl);
        }

        _logger.LogWarning("Failed login: {User}", username);
        return Redirect("/login?error=invalid");
    }

    [HttpGet("logout")]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return Redirect("/login");
    }
}
