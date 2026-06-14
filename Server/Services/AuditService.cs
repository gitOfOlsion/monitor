using Microsoft.EntityFrameworkCore;
using PeripheralMonitor.Server.Data;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Server.Services;

public class AuditService
{
    private readonly IDbContextFactory<AppDbContext> _db;

    public AuditService(IDbContextFactory<AppDbContext> db) => _db = db;

    public async Task LogAsync(CreateAuditRequest req)
    {
        await using var db = await _db.CreateDbContextAsync();
        db.AuditLogs.Add(new AuditLogEntity
        {
            Timestamp     = DateTime.UtcNow,
            Actor         = req.Actor,
            ActorRole     = req.ActorRole,
            InstitutionId = req.InstitutionId,
            Action        = req.Action,
            Target        = req.Target,
            Details       = req.Details,
            IpAddress     = req.IpAddress
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Convenience overload for internal callers.</summary>
    public async Task LogAsync(string actor, string actorRole, Guid? institutionId,
                               string action, string target, string? details = null, string? ip = null)
        => await LogAsync(new CreateAuditRequest(actor, actorRole, institutionId, action, target, details, ip));

    public async Task<List<AuditLogDto>> GetLogsAsync(int count = 500, Guid? scopeInstitutionId = null)
    {
        await using var db = await _db.CreateDbContextAsync();
        var q = db.AuditLogs.AsQueryable();

        // Institution admins see only their institution's logs (plus null = system-wide they don't see)
        if (scopeInstitutionId.HasValue)
            q = q.Where(a => a.InstitutionId == scopeInstitutionId.Value);

        return await q
            .OrderByDescending(a => a.Timestamp)
            .Take(count)
            .Select(a => new AuditLogDto
            {
                Id            = a.Id,
                Timestamp     = a.Timestamp,
                Actor         = a.Actor,
                ActorRole     = a.ActorRole,
                InstitutionId = a.InstitutionId,
                Action        = a.Action,
                Target        = a.Target,
                Details       = a.Details,
                IpAddress     = a.IpAddress
            })
            .ToListAsync();
    }
}
