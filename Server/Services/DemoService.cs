using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PeripheralMonitor.Server.Data;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Server.Services;

/// <summary>
/// Self-service demo (trial) onboarding and lifecycle. Creates a time-limited
/// institution + its InstitutionAdmin, with all features enabled EXCEPT
/// Application Control. Demos are login-blocked after 15 days and hard-deleted
/// after 30 days by a background sweep.
/// </summary>
public sealed class DemoService
{
    public const int TrialDays = 15;
    public const int PurgeDays = 30;

    private readonly IDbContextFactory<AppDbContext> _db;
    private readonly UserManager<AppUser> _userMgr;
    private readonly FeatureService _features;
    private readonly ILogger<DemoService> _log;

    public DemoService(IDbContextFactory<AppDbContext> db, UserManager<AppUser> userMgr,
                       FeatureService features, ILogger<DemoService> log)
    { _db = db; _userMgr = userMgr; _features = features; _log = log; }

    /// <summary>Create a demo institution + admin. Returns the username on success.</summary>
    public async Task<(bool ok, string? error)> CreateDemoAsync(
        string institutionName, string username, string email, string password)
    {
        institutionName = institutionName.Trim();
        username = username.Trim();
        email = email.Trim();

        if (institutionName.Length < 2) return (false, "Institution name is too short");
        if (username.Length < 3) return (false, "Username must be at least 3 characters");
        if (password.Length < 6) return (false, "Password must be at least 6 characters");

        await using var db = await _db.CreateDbContextAsync();

        // Username must be globally unique (Identity requirement)
        if (await _userMgr.FindByNameAsync(username) != null)
            return (false, "That username is already taken");

        var now = DateTime.UtcNow;
        var slug = MakeSlug(institutionName);
        var baseSlug = slug; var n = 1;
        while (await db.Institutions.AnyAsync(x => x.Slug == slug)) slug = $"{baseSlug}-{n++}";

        var inst = new InstitutionEntity
        {
            Name = institutionName, Slug = slug, Type = "Demo", ContactEmail = email,
            IsDemo = true,
            ExpiresAt = now.AddDays(TrialDays),
            PurgeAt   = now.AddDays(PurgeDays),
        };
        db.Institutions.Add(inst);
        await db.SaveChangesAsync();

        var user = new AppUser
        {
            UserName = username, Email = email, Role = "InstitutionAdmin",
            InstitutionId = inst.Id, EmailConfirmed = true
        };
        var res = await _userMgr.CreateAsync(user, password);
        if (!res.Succeeded)
        {
            // Roll back the institution if user creation failed
            db.Institutions.Remove(inst);
            await db.SaveChangesAsync();
            return (false, string.Join("; ", res.Errors.Select(e => e.Description)));
        }
        await _userMgr.AddToRoleAsync(user, "InstitutionAdmin");

        // Enable all catalog features for the demo institution EXCEPT Application Control.
        foreach (var def in FeatureCatalog.All)
        {
            bool enable = def.Key != FeatureCatalog.AppInstall; // app-control stays OFF
            await _features.SetAsync(
                new SetFeatureFlagRequest(def.Key, "Institution", inst.Id, enable, null),
                "demo-signup");
        }

        _log.LogInformation("Demo created: institution '{Inst}' admin '{User}', expires {Exp}",
            institutionName, username, inst.ExpiresAt);
        return (true, null);
    }

    /// <summary>Is this institution a demo whose trial has expired (login should be blocked)?</summary>
    public async Task<bool> IsExpiredAsync(Guid institutionId)
    {
        await using var db = await _db.CreateDbContextAsync();
        var inst = await db.Institutions.AsNoTracking().FirstOrDefaultAsync(i => i.Id == institutionId);
        return inst is { IsDemo: true, ExpiresAt: not null } && inst.ExpiresAt < DateTime.UtcNow;
    }

    /// <summary>Background sweep: purge demo institutions past their PurgeAt.</summary>
    public async Task PurgeExpiredAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var toPurge = await db.Institutions
            .Where(i => i.IsDemo && i.PurgeAt != null && i.PurgeAt < now)
            .ToListAsync();

        foreach (var inst in toPurge)
        {
            // Remove users, agents, events, feature flags, then the institution.
            var users = await db.Users.Where(u => u.InstitutionId == inst.Id).ToListAsync();
            foreach (var u in users) await _userMgr.DeleteAsync(u);

            var agents = await db.Agents.Where(a => a.InstitutionId == inst.Id).Select(a => a.Id).ToListAsync();
            if (agents.Count > 0)
            {
                await db.DeviceEvents.Where(e => agents.Contains(e.AgentId)).ExecuteDeleteAsync();
                await db.Agents.Where(a => a.InstitutionId == inst.Id).ExecuteDeleteAsync();
            }
            await db.FeatureFlags.Where(f => f.Scope == "Institution" && f.ScopeId == inst.Id).ExecuteDeleteAsync();
            db.Institutions.Remove(inst);
            await db.SaveChangesAsync();
            _log.LogInformation("Purged expired demo institution '{Inst}' ({Id})", inst.Name, inst.Id);
        }
    }

    private static string MakeSlug(string name)
    {
        var s = new string(name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Trim('-') is { Length: > 0 } t ? t : "demo";
    }
}
