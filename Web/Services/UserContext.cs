using Microsoft.AspNetCore.Identity;
using PeripheralMonitor.Server.Data;

namespace PeripheralMonitor.Web.Services;

/// <summary>Scoped service that resolves the current user's institution and role.</summary>
public class UserContext
{
    private readonly IHttpContextAccessor _httpCtx;
    private readonly UserManager<User>    _userMgr;

    private User?   _user;
    private bool    _loaded;

    public UserContext(IHttpContextAccessor httpCtx, UserManager<User> userMgr)
    {
        _httpCtx = httpCtx;
        _userMgr = userMgr;
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        var principal = _httpCtx.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated == true)
            _user = await _userMgr.GetUserAsync(principal);
    }

    public async Task<Guid?> GetInstitutionIdAsync()
    {
        await EnsureLoadedAsync();
        return _user?.InstitutionId;
    }

    public async Task<bool> IsSuperAdminAsync()
    {
        await EnsureLoadedAsync();
        if (_user == null) return false;
        var roles = await _userMgr.GetRolesAsync(_user);
        return roles.Contains("SuperAdmin");
    }

    public async Task<string> GetUsernameAsync()
    {
        await EnsureLoadedAsync();
        return _user?.UserName ?? "Unknown";
    }
}
