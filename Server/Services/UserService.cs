using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PeripheralMonitor.Server.Data;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Server.Services;

public class UserService
{
    private readonly IDbContextFactory<AppDbContext> _db;
    private readonly UserManager<AppUser>            _userMgr;
    private readonly ILogger<UserService>            _log;

    public UserService(
        IDbContextFactory<AppDbContext> db,
        UserManager<AppUser>            userMgr,
        ILogger<UserService>            log)
    {
        _db      = db;
        _userMgr = userMgr;
        _log     = log;
    }

    /// <summary>Validate login credentials against the database.</summary>
    public async Task<ValidateLoginResponse> ValidateAsync(string userName, string password)
    {
        var user = await _userMgr.FindByNameAsync(userName);
        if (user == null)
            return new ValidateLoginResponse(false, null, null, null, null, null);

        if (!await _userMgr.CheckPasswordAsync(user, password))
            return new ValidateLoginResponse(false, null, null, null, null, null);

        string? instName = null;
        if (user.InstitutionId.HasValue)
        {
            await using var db = await _db.CreateDbContextAsync();
            instName = await db.Institutions
                .Where(i => i.Id == user.InstitutionId.Value)
                .Select(i => i.Name)
                .FirstOrDefaultAsync();
        }

        return new ValidateLoginResponse(
            true, user.Id, user.UserName, user.Role, user.InstitutionId, instName);
    }

    /// <summary>List users. SuperAdmin sees all; institution admin sees only their own.</summary>
    public async Task<List<UserDto>> GetUsersAsync(Guid? scopeInstitutionId)
    {
        await using var db = await _db.CreateDbContextAsync();
        var q = db.Users.AsQueryable();

        if (scopeInstitutionId.HasValue)
            q = q.Where(u => u.InstitutionId == scopeInstitutionId.Value);

        return await q
            .Select(u => new UserDto
            {
                Id              = u.Id,
                UserName        = u.UserName ?? "",
                Email           = u.Email ?? "",
                Role            = u.Role,
                InstitutionId   = u.InstitutionId,
                InstitutionName = u.Institution != null ? u.Institution.Name : null,
                CreatedAt       = u.CreatedAt
            })
            .OrderBy(u => u.UserName)
            .ToListAsync();
    }

    /// <summary>
    /// Create a user. Returns (success, error).
    /// Enforces: institution admins can only create users within their own institution,
    /// and can only create Viewer or InstitutionAdmin roles (never SuperAdmin).
    /// </summary>
    public async Task<(bool ok, string? error)> CreateUserAsync(
        CreateUserRequest req,
        string            callerRole,
        Guid?             callerInstitutionId)
    {
        // SuperAdmin role can never be created via API
        if (req.Role == "SuperAdmin")
            return (false, "Cannot create SuperAdmin users via API");

        // Determine the institution the new user belongs to
        Guid? targetInstitution;
        if (callerRole == "SuperAdmin")
        {
            // SuperAdmin must specify which institution
            if (req.InstitutionId == null)
                return (false, "InstitutionId is required");
            targetInstitution = req.InstitutionId;
        }
        else if (callerRole == "InstitutionAdmin")
        {
            // Institution admin can only create within their own institution
            targetInstitution = callerInstitutionId;
            if (req.Role != "Viewer" && req.Role != "InstitutionAdmin")
                return (false, "Institution admins can only create Viewer or InstitutionAdmin users");
        }
        else
        {
            return (false, "Insufficient privileges to create users");
        }

        await using var db = await _db.CreateDbContextAsync();
        if (!await db.Institutions.AnyAsync(i => i.Id == targetInstitution))
            return (false, "Institution not found");

        var user = new AppUser
        {
            UserName      = req.UserName,
            Email         = req.Email,
            Role          = req.Role,
            InstitutionId = targetInstitution,
            EmailConfirmed = true
        };

        var result = await _userMgr.CreateAsync(user, req.Password);
        if (!result.Succeeded)
            return (false, string.Join("; ", result.Errors.Select(e => e.Description)));

        await _userMgr.AddToRoleAsync(user, req.Role);
        _log.LogInformation("User created: {User} ({Role}) in institution {Inst}",
            req.UserName, req.Role, targetInstitution);
        return (true, null);
    }

    public async Task<(bool ok, string? error)> UpdateUserAsync(
        string id, string? email, string? newPassword, string callerRole, Guid? callerInstitutionId)
    {
        var user = await _userMgr.FindByIdAsync(id);
        if (user == null) return (false, "User not found");

        // Institution admins can only edit users within their own institution
        if (callerRole == "InstitutionAdmin" && user.InstitutionId != callerInstitutionId)
            return (false, "Cannot edit users outside your institution");
        // Nobody edits a SuperAdmin via this API
        if (user.Role == "SuperAdmin" && callerRole != "SuperAdmin")
            return (false, "Cannot edit a SuperAdmin");

        if (!string.IsNullOrWhiteSpace(email) && email != user.Email)
        {
            user.Email = email;
            var upd = await _userMgr.UpdateAsync(user);
            if (!upd.Succeeded)
                return (false, string.Join("; ", upd.Errors.Select(e => e.Description)));
        }

        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            var token = await _userMgr.GeneratePasswordResetTokenAsync(user);
            var res = await _userMgr.ResetPasswordAsync(user, token, newPassword);
            if (!res.Succeeded)
                return (false, string.Join("; ", res.Errors.Select(e => e.Description)));
        }

        _log.LogInformation("User updated: {User}", user.UserName);
        return (true, null);
    }

    public async Task<(bool ok, string? error)> DeleteUserAsync(
        string id, string callerRole, Guid? callerInstitutionId, string? callerActor = null)
    {
        var user = await _userMgr.FindByIdAsync(id);
        if (user == null) return (false, "User not found");

        // Never delete SuperAdmin
        if (user.Role == "SuperAdmin") return (false, "Cannot delete SuperAdmin");

        // An admin cannot delete their own account
        if (!string.IsNullOrEmpty(callerActor) &&
            string.Equals(user.UserName, callerActor, StringComparison.OrdinalIgnoreCase))
            return (false, "You cannot delete your own account");

        // Institution admins can only delete users in their own institution
        if (callerRole == "InstitutionAdmin" && user.InstitutionId != callerInstitutionId)
            return (false, "Cannot delete users outside your institution");

        var result = await _userMgr.DeleteAsync(user);
        if (!result.Succeeded)
            return (false, string.Join("; ", result.Errors.Select(e => e.Description)));

        _log.LogInformation("User deleted: {User}", user.UserName);
        return (true, null);
    }
}
