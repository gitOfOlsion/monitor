using Microsoft.EntityFrameworkCore;
using PeripheralMonitor.Server.Data;
using PeripheralMonitor.Shared.Security;

namespace PeripheralMonitor.Server.Services;

public class InstitutionService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IConfiguration                  _config;
    private readonly ILogger<InstitutionService>     _logger;

    public InstitutionService(
        IDbContextFactory<AppDbContext> dbFactory,
        IConfiguration                  config,
        ILogger<InstitutionService>     logger)
    {
        _dbFactory = dbFactory;
        _config    = config;
        _logger    = logger;
    }

    // ── Institutions ──────────────────────────────────────────────────────
    public async Task<List<Institution>> GetAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Institutions
            .Include(i => i.Users)
            .Include(i => i.Agents)
            .OrderBy(i => i.Name)
            .ToListAsync();
    }

    public async Task<Institution?> GetByIdAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Institutions
            .Include(i => i.Users)
            .Include(i => i.Agents)
            .FirstOrDefaultAsync(i => i.Id == id);
    }

    public async Task<Institution?> GetBySlugAsync(string slug)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Institutions.FirstOrDefaultAsync(i => i.Slug == slug);
    }

    public async Task<Institution> CreateAsync(
        string name, string type, string contactEmail, string? logoUrl = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var slug = GenerateSlug(name);
        // Ensure slug uniqueness
        var exists = await db.Institutions.AnyAsync(i => i.Slug == slug);
        if (exists) slug = $"{slug}-{Guid.NewGuid().ToString("N")[..6]}";

        var institution = new Institution
        {
            Name         = name,
            Slug         = slug,
            Type         = type,
            ContactEmail = contactEmail,
            LogoUrl      = logoUrl,
            CreatedAt    = DateTime.UtcNow
        };

        db.Institutions.Add(institution);
        await db.SaveChangesAsync();
        _logger.LogInformation("Institution created: {Name} ({Id})", name, institution.Id);
        return institution;
    }

    public async Task<Institution?> UpdateAsync(
        Guid id, string name, string type, string contactEmail, string? logoUrl)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var inst = await db.Institutions.FindAsync(id);
        if (inst == null) return null;
        inst.Name         = name;
        inst.Type         = type;
        inst.ContactEmail = contactEmail;
        inst.LogoUrl      = logoUrl;
        await db.SaveChangesAsync();
        return inst;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var inst = await db.Institutions.FindAsync(id);
        if (inst == null) return false;
        db.Institutions.Remove(inst);
        await db.SaveChangesAsync();
        return true;
    }

    // ── Agent Templates ───────────────────────────────────────────────────
    public async Task<List<AgentTemplate>> GetTemplatesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.AgentTemplates
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<AgentTemplate> CreateTemplateAsync(
        string institutionId, string name, string? description, int expiryDays = 365)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var secret   = _config["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret missing");
        var template = new AgentTemplate
        {
            Name        = name,
            Description = description,
            ExpiresAt   = DateTime.UtcNow.AddDays(expiryDays),
            CreatedAt   = DateTime.UtcNow
        };

        // Generate placeholder token (no machine/mac — those are added on approval)
        template.JwtToken = JwtHelper.GenerateTemplateToken(
            institutionId, template.Id.ToString(), secret, expiryDays);

        db.AgentTemplates.Add(template);
        await db.SaveChangesAsync();
        _logger.LogInformation("AgentTemplate created: {Name} for institution {Id}", name, institutionId);
        return template;
    }

    public async Task<bool> DeleteTemplateAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var t = await db.AgentTemplates.FindAsync(id);
        if (t == null) return false;
        db.AgentTemplates.Remove(t);
        await db.SaveChangesAsync();
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private static string GenerateSlug(string name)
        => System.Text.RegularExpressions.Regex
            .Replace(name.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-")
            .Trim('-');
}
