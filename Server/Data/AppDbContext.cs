using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace PeripheralMonitor.Server.Data;

public class AppDbContext : IdentityDbContext<User>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Institution>   Institutions     => Set<Institution>();
    public DbSet<AgentTemplate> AgentTemplates   => Set<AgentTemplate>();
    public DbSet<Agent>         Agents           => Set<Agent>();
    public DbSet<DeviceCategory> DeviceCategories => Set<DeviceCategory>();
    public DbSet<DeviceEvent>   DeviceEvents     => Set<DeviceEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // ── Rename Identity tables ────────────────────────────────────────
        b.Entity<User>          (e => e.ToTable("Users"));
        b.Entity<IdentityRole>  (e => e.ToTable("Roles"));
        b.Entity<IdentityUserRole<string>>          (e => e.ToTable("UserRoles"));
        b.Entity<IdentityUserClaim<string>>         (e => e.ToTable("UserClaims"));
        b.Entity<IdentityUserLogin<string>>         (e => e.ToTable("UserLogins"));
        b.Entity<IdentityRoleClaim<string>>         (e => e.ToTable("RoleClaims"));
        b.Entity<IdentityUserToken<string>>         (e => e.ToTable("UserTokens"));

        // ── Institution ───────────────────────────────────────────────────
        b.Entity<Institution>(e =>
        {
            e.HasIndex(i => i.Slug).IsUnique();
            e.HasMany(i => i.Users)
             .WithOne(u => u.Institution)
             .HasForeignKey(u => u.InstitutionId)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(i => i.Agents)
             .WithOne(a => a.Institution)
             .HasForeignKey(a => a.InstitutionId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── User ──────────────────────────────────────────────────────────
        b.Entity<User>(e =>
        {
            e.HasIndex(u => u.InstitutionId);
        });

        // ── AgentTemplate (no Institution FK) ────────────────────────────
        b.Entity<AgentTemplate>(e =>
        {
            e.HasMany(t => t.Agents)
             .WithOne(a => a.Template)
             .HasForeignKey(a => a.TemplateId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // ── Agent ─────────────────────────────────────────────────────────
        b.Entity<Agent>(e =>
        {
            e.HasIndex(a => a.MacAddress);
            e.HasIndex(a => new { a.InstitutionId, a.Status });
            e.HasIndex(a => a.LastHeartbeat);
        });

        // ── DeviceCategory ────────────────────────────────────────────────
        b.Entity<DeviceCategory>(e =>
        {
            e.HasIndex(c => c.Name).IsUnique();
        });

        // ── DeviceEvent ───────────────────────────────────────────────────
        b.Entity<DeviceEvent>(e =>
        {
            e.HasIndex(de => de.Timestamp);
            e.HasIndex(de => new { de.AgentId, de.Timestamp });
            e.HasIndex(de => de.EventType);
            e.HasOne(de => de.Category)
             .WithMany(c => c.DeviceEvents)
             .HasForeignKey(de => de.CategoryId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // ── Seed DeviceCategory ───────────────────────────────────────────
        b.Entity<DeviceCategory>().HasData(
            new DeviceCategory { Id =  1, Name = "Keyboard",        Icon = "⌨️",  Description = "Input keyboards" },
            new DeviceCategory { Id =  2, Name = "Mouse",           Icon = "🖱️",  Description = "Pointing devices" },
            new DeviceCategory { Id =  3, Name = "HID Device",      Icon = "🎮",  Description = "Human interface devices" },
            new DeviceCategory { Id =  4, Name = "USB Device",      Icon = "🔌",  Description = "USB peripherals" },
            new DeviceCategory { Id =  5, Name = "Storage",         Icon = "💾",  Description = "Disks and storage" },
            new DeviceCategory { Id =  6, Name = "Optical Drive",   Icon = "💿",  Description = "CD/DVD/Blu-ray drives" },
            new DeviceCategory { Id =  7, Name = "Display",         Icon = "🖥️",  Description = "Monitors and displays" },
            new DeviceCategory { Id =  8, Name = "Audio",           Icon = "🔊",  Description = "Audio devices" },
            new DeviceCategory { Id =  9, Name = "Bluetooth",       Icon = "📶",  Description = "Bluetooth devices" },
            new DeviceCategory { Id = 10, Name = "Network",         Icon = "🌐",  Description = "Network interfaces" },
            new DeviceCategory { Id = 11, Name = "Camera",          Icon = "📷",  Description = "Cameras and scanners" },
            new DeviceCategory { Id = 12, Name = "Printer",         Icon = "🖨️",  Description = "Printers" },
            new DeviceCategory { Id = 13, Name = "Serial Port",     Icon = "🔌",  Description = "COM/LPT ports" },
            new DeviceCategory { Id = 14, Name = "Input Device",    Icon = "🕹️",  Description = "Generic input devices" },
            new DeviceCategory { Id = 15, Name = "PCI Device",      Icon = "🔧",  Description = "PCI/PCIe devices" },
            new DeviceCategory { Id = 16, Name = "Other",           Icon = "🔧",  Description = "Unclassified devices" }
        );
    }
}
