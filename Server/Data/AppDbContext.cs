using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace PeripheralMonitor.Server.Data;

// ── Custom Identity user ──────────────────────────────────────────────────
public class AppUser : IdentityUser
{
    public Guid?    InstitutionId { get; set; }
    public string   Role          { get; set; } = "Viewer";
    public DateTime CreatedAt     { get; set; } = DateTime.UtcNow;
    public InstitutionEntity? Institution { get; set; }
}

// ── Domain entities ───────────────────────────────────────────────────────
public class InstitutionEntity
{
    public Guid     Id           { get; set; } = Guid.NewGuid();
    public string   Name         { get; set; } = "";
    public string   Slug         { get; set; } = "";
    public string   Type         { get; set; } = "";
    public string   ContactEmail { get; set; } = "";
    public string?  LogoUrl      { get; set; }
    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;

    public ICollection<AppUser>     Users  { get; set; } = [];
    public ICollection<AgentEntity> Agents { get; set; } = [];
}

/// <summary>
/// Global agent configuration template — NOT institution-scoped.
/// Created by SuperAdmin; referenced by agents at provision time.
/// </summary>
public class AgentTemplateEntity
{
    public Guid     Id          { get; set; } = Guid.NewGuid();
    public string   Name        { get; set; } = "";
    public string   Description { get; set; } = "";
    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;

    public ICollection<AgentEntity> Agents { get; set; } = [];
}

public class AgentEntity
{
    public Guid      Id            { get; set; } = Guid.NewGuid();
    public Guid      InstitutionId { get; set; }
    public Guid?     TemplateId    { get; set; }
    public string    MachineName   { get; set; } = "";
    public string    IPAddress     { get; set; } = "";
    public string    MacAddress    { get; set; } = "";
    public string    Platform      { get; set; } = "Unknown";
    public string    Status        { get; set; } = "Pending";
    public string?   JwtToken      { get; set; }
    public int       DeviceCount   { get; set; }
    public DateTime  FirstSeen     { get; set; } = DateTime.UtcNow;
    public DateTime? LastHeartbeat { get; set; }
    public string?   ApprovedBy    { get; set; }
    public DateTime? ApprovedAt    { get; set; }
    public string?   RustDeskId    { get; set; }

    public InstitutionEntity    Institution { get; set; } = null!;
    public AgentTemplateEntity? Template    { get; set; }
    public ICollection<DeviceEventEntity> DeviceEvents { get; set; } = [];
}

public class DeviceCategoryEntity
{
    public int    Id          { get; set; }
    public string Name        { get; set; } = "";
    public string Icon        { get; set; } = "🔧";
    public string Description { get; set; } = "";
    public ICollection<DeviceEventEntity> DeviceEvents { get; set; } = [];
}

public class DeviceEventEntity
{
    public long      Id           { get; set; }
    public Guid      AgentId      { get; set; }
    public int?      CategoryId   { get; set; }
    public string    MachineName  { get; set; } = "";
    public string    EventType    { get; set; } = "";
    public string    DeviceId     { get; set; } = "";
    public string    FriendlyName { get; set; } = "";
    public string    Category     { get; set; } = "";
    public string    Status       { get; set; } = "";
    public DateTime  Timestamp    { get; set; }

    public AgentEntity           Agent          { get; set; } = null!;
    public DeviceCategoryEntity? DeviceCategory { get; set; }
}

// ── Audit log ───────────────────────────────────────────────────────────
public class AuditLogEntity
{
    public long     Id            { get; set; }
    public DateTime Timestamp     { get; set; } = DateTime.UtcNow;
    public string   Actor         { get; set; } = "";   // username who did it
    public string   ActorRole     { get; set; } = "";   // their role at the time
    public Guid?    InstitutionId { get; set; }         // scope (null = global/superadmin)
    public string   Action        { get; set; } = "";   // e.g. "agent.approve", "user.create", "login"
    public string   Target        { get; set; } = "";   // what was acted on (machine name, username, etc.)
    public string?  Details       { get; set; }         // optional JSON / free text
    public string?  IpAddress     { get; set; }
}

// ── DbContext ─────────────────────────────────────────────────────────────
public class AppDbContext : IdentityDbContext<AppUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> o) : base(o) { }

    public DbSet<InstitutionEntity>    Institutions    => Set<InstitutionEntity>();
    public DbSet<AgentTemplateEntity>  AgentTemplates  => Set<AgentTemplateEntity>();
    public DbSet<AgentEntity>          Agents          => Set<AgentEntity>();
    public DbSet<DeviceCategoryEntity> DeviceCategories=> Set<DeviceCategoryEntity>();
    public DbSet<DeviceEventEntity>    DeviceEvents    => Set<DeviceEventEntity>();
    public DbSet<AuditLogEntity>       AuditLogs       => Set<AuditLogEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Rename Identity tables to clean snake_case
        b.Entity<AppUser>().ToTable("users");
        b.Entity<IdentityRole>().ToTable("roles");
        b.Entity<IdentityUserRole<string>>().ToTable("user_roles");
        b.Entity<IdentityUserClaim<string>>().ToTable("user_claims");
        b.Entity<IdentityUserLogin<string>>().ToTable("user_logins");
        b.Entity<IdentityUserToken<string>>().ToTable("user_tokens");
        b.Entity<IdentityRoleClaim<string>>().ToTable("role_claims");

        b.Entity<AppUser>(e =>
            e.HasOne(u => u.Institution).WithMany(i => i.Users)
             .HasForeignKey(u => u.InstitutionId).IsRequired(false).OnDelete(DeleteBehavior.SetNull));

        b.Entity<InstitutionEntity>(e =>
        {
            e.ToTable("institutions");
            e.HasKey(i => i.Id);
            e.HasIndex(i => i.Slug).IsUnique();
        });

        b.Entity<AgentTemplateEntity>(e =>   // standalone — no institution FK
        {
            e.ToTable("agent_templates");
            e.HasKey(t => t.Id);
        });

        b.Entity<AgentEntity>(e =>
        {
            e.ToTable("agents");
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.InstitutionId, a.MacAddress }).IsUnique();
            e.HasIndex(a => a.Status);
            e.HasOne(a => a.Institution).WithMany(i => i.Agents)
             .HasForeignKey(a => a.InstitutionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.Template).WithMany(t => t.Agents)
             .HasForeignKey(a => a.TemplateId).IsRequired(false).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<DeviceCategoryEntity>(e =>
        {
            e.ToTable("device_categories");
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.Name).IsUnique();
            e.HasData(
                new { Id=1,  Name="USB Device",      Icon="🔌", Description="Generic USB device" },
                new { Id=2,  Name="Keyboard",         Icon="⌨️", Description="Keyboard input device" },
                new { Id=3,  Name="Mouse",            Icon="🖱️", Description="Pointing device" },
                new { Id=4,  Name="HID Device",       Icon="🎮", Description="Human interface device" },
                new { Id=5,  Name="Storage",          Icon="💾", Description="Mass storage" },
                new { Id=6,  Name="Optical Drive",    Icon="💿", Description="CD/DVD drive" },
                new { Id=7,  Name="Display",          Icon="🖥️", Description="Monitor or display" },
                new { Id=8,  Name="Audio",            Icon="🔊", Description="Audio device" },
                new { Id=9,  Name="Bluetooth",        Icon="📶", Description="Bluetooth device" },
                new { Id=10, Name="Network",          Icon="🌐", Description="Network interface" },
                new { Id=11, Name="Camera",           Icon="📷", Description="Camera or scanner" },
                new { Id=12, Name="Printer",          Icon="🖨️", Description="Printer" },
                new { Id=13, Name="Serial/COM Port",  Icon="🔌", Description="Serial or COM port" },
                new { Id=14, Name="Input Device",     Icon="🕹️", Description="Generic input" },
                new { Id=15, Name="Other",            Icon="🔧", Description="Uncategorised" }
            );
        });

        b.Entity<DeviceEventEntity>(e =>
        {
            e.ToTable("device_events");
            e.HasKey(de => de.Id);
            e.HasIndex(de => new { de.AgentId, de.Timestamp });
            e.HasOne(de => de.Agent).WithMany(a => a.DeviceEvents)
             .HasForeignKey(de => de.AgentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(de => de.DeviceCategory).WithMany(c => c.DeviceEvents)
             .HasForeignKey(de => de.CategoryId).IsRequired(false).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<AuditLogEntity>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.Timestamp);
            e.HasIndex(a => a.InstitutionId);
            e.HasIndex(a => a.Actor);
        });
    }
}
