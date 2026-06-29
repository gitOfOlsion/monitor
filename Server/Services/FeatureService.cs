using Microsoft.EntityFrameworkCore;
using PeripheralMonitor.Server.Data;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Server.Services;

/// <summary>
/// Resolves and manages feature on/off state across the two-tier scope model:
///   agent override → institution override → catalog default.
/// All feature-gated endpoints call IsEnabledAsync before acting, so the flags
/// are enforced server-side (the UI also reflects them, but the server is the
/// source of truth).
/// </summary>
public sealed class FeatureService
{
    private readonly IDbContextFactory<AppDbContext> _db;
    private readonly ILogger<FeatureService> _log;

    public FeatureService(IDbContextFactory<AppDbContext> db, ILogger<FeatureService> log)
    { _db = db; _log = log; }

    /// <summary>
    /// Resolve whether a feature is enabled for a specific agent, walking the
    /// override chain. If the agent isn't found, falls back to the catalog default.
    /// </summary>
    public async Task<bool> IsEnabledAsync(string featureKey, Guid agentId)
    {
        var def = FeatureCatalog.Find(featureKey);
        if (def == null) return false; // unknown feature → deny by default

        await using var db = await _db.CreateDbContextAsync();

        // Agent-level override wins
        var agentFlag = await db.FeatureFlags.AsNoTracking().FirstOrDefaultAsync(
            f => f.FeatureKey == featureKey && f.Scope == "Agent" && f.ScopeId == agentId);
        if (agentFlag != null) return agentFlag.Enabled;

        // Otherwise institution-level override
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId);
        if (agent != null)
        {
            var instFlag = await db.FeatureFlags.AsNoTracking().FirstOrDefaultAsync(
                f => f.FeatureKey == featureKey && f.Scope == "Institution" && f.ScopeId == agent.InstitutionId);
            if (instFlag != null) return instFlag.Enabled;
        }

        // Fall back to the catalog default
        return def.DefaultEnabled;
    }

    /// <summary>Resolved config JSON for a feature at an agent (agent → institution → null).</summary>
    public async Task<string?> GetConfigAsync(string featureKey, Guid agentId)
    {
        await using var db = await _db.CreateDbContextAsync();

        var agentFlag = await db.FeatureFlags.AsNoTracking().FirstOrDefaultAsync(
            f => f.FeatureKey == featureKey && f.Scope == "Agent" && f.ScopeId == agentId);
        if (agentFlag?.ConfigJson != null) return agentFlag.ConfigJson;

        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId);
        if (agent != null)
        {
            var instFlag = await db.FeatureFlags.AsNoTracking().FirstOrDefaultAsync(
                f => f.FeatureKey == featureKey && f.Scope == "Institution" && f.ScopeId == agent.InstitutionId);
            if (instFlag?.ConfigJson != null) return instFlag.ConfigJson;
        }
        return null;
    }

    /// <summary>
    /// Build the full feature list for an institution (institution-level view),
    /// or for a specific agent if agentId is provided (shows agent overrides too).
    /// </summary>
    public async Task<List<FeatureStateDto>> GetStatesAsync(Guid institutionId, Guid? agentId = null)
    {
        await using var db = await _db.CreateDbContextAsync();

        var instFlags = await db.FeatureFlags.AsNoTracking()
            .Where(f => f.Scope == "Institution" && f.ScopeId == institutionId)
            .ToListAsync();

        List<FeatureFlagEntity> agentFlags = new();
        if (agentId.HasValue)
            agentFlags = await db.FeatureFlags.AsNoTracking()
                .Where(f => f.Scope == "Agent" && f.ScopeId == agentId.Value)
                .ToListAsync();

        var result = new List<FeatureStateDto>();
        foreach (var def in FeatureCatalog.All)
        {
            var inst  = instFlags.FirstOrDefault(f => f.FeatureKey == def.Key);
            var agent = agentFlags.FirstOrDefault(f => f.FeatureKey == def.Key);

            // Resolution
            bool effective = agent?.Enabled
                          ?? inst?.Enabled
                          ?? def.DefaultEnabled;

            result.Add(new FeatureStateDto
            {
                Key = def.Key, Name = def.Name, Description = def.Description,
                Icon = def.Icon, HasConfig = def.HasConfig, DefaultEnabled = def.DefaultEnabled,
                InstitutionEnabled = inst?.Enabled,
                AgentEnabled = agent?.Enabled,
                Effective = effective,
                ConfigJson = agent?.ConfigJson ?? inst?.ConfigJson
            });
        }
        return result;
    }

    /// <summary>Set or clear a feature override. Enabled=null clears the override.</summary>
    public async Task SetAsync(SetFeatureFlagRequest req, string actor)
    {
        if (FeatureCatalog.Find(req.FeatureKey) == null)
            throw new ArgumentException($"Unknown feature: {req.FeatureKey}");
        if (req.Scope != "Institution" && req.Scope != "Agent")
            throw new ArgumentException($"Invalid scope: {req.Scope}");

        await using var db = await _db.CreateDbContextAsync();
        var existing = await db.FeatureFlags.FirstOrDefaultAsync(
            f => f.FeatureKey == req.FeatureKey && f.Scope == req.Scope && f.ScopeId == req.ScopeId);

        // Clearing the override (revert to inherited/default), but keep it if there's config to preserve
        if (req.Enabled == null && req.ConfigJson == null)
        {
            if (existing != null) { db.FeatureFlags.Remove(existing); await db.SaveChangesAsync(); }
            return;
        }

        if (existing == null)
        {
            existing = new FeatureFlagEntity
            {
                FeatureKey = req.FeatureKey, Scope = req.Scope, ScopeId = req.ScopeId
            };
            db.FeatureFlags.Add(existing);
        }
        if (req.Enabled.HasValue) existing.Enabled = req.Enabled.Value;
        if (req.ConfigJson != null) existing.ConfigJson = req.ConfigJson;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.UpdatedBy = actor;
        await db.SaveChangesAsync();
        _log.LogInformation("Feature {Key} {Scope}:{Id} set enabled={En} by {Actor}",
            req.FeatureKey, req.Scope, req.ScopeId, req.Enabled, actor);
    }
}
