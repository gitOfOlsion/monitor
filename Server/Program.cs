using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PeripheralMonitor.Server.Data;
using PeripheralMonitor.Server.Hubs;
using PeripheralMonitor.Server.Services;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog ───────────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, services, cfg) =>
        cfg.ReadFrom.Configuration(ctx.Configuration)
           .ReadFrom.Services(services)
           .Enrich.FromLogContext()
           .Enrich.WithMachineName()
           .Enrich.WithProperty("Application", "Server")
           .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
           .WriteTo.Seq(ctx.Configuration["Seq:Url"] ?? "http://seq:5341"));

    // ── Database ──────────────────────────────────────────────────────────
    builder.Services.AddDbContextFactory<AppDbContext>(opts =>
        opts.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

    // ── Identity (table = Users) ──────────────────────────────────────────
    builder.Services.AddIdentity<User, IdentityRole>(opts =>
    {
        opts.Password.RequireDigit           = true;
        opts.Password.RequiredLength         = 8;
        opts.Password.RequireNonAlphanumeric = false;
        opts.SignIn.RequireConfirmedAccount   = false;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

    // ── JWT (agents) ──────────────────────────────────────────────────────
    var jwtSecret = builder.Configuration["Jwt:Secret"]
        ?? throw new InvalidOperationException("Jwt:Secret required");
    var jwtKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

    builder.Services.AddAuthentication(opts =>
    {
        opts.DefaultAuthenticateScheme = "Smart";
        opts.DefaultChallengeScheme    = "Smart";
    })
    .AddPolicyScheme("Smart", "Smart Auth", opts =>
    {
        opts.ForwardDefaultSelector = ctx =>
        {
            var path = ctx.Request.Path;
            if (path.StartsWithSegments("/monitor") || path.StartsWithSegments("/api/agent"))
                return JwtBearerDefaults.AuthenticationScheme;
            return IdentityConstants.ApplicationScheme;
        };
    })
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = "PeripheralMonitor",
            ValidAudience            = "Agent",
            IssuerSigningKey         = jwtKey
        };
        opts.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    ctx.HttpContext.Request.Path.StartsWithSegments("/monitor"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });

    builder.Services.AddAuthorization(opts =>
    {
        opts.AddPolicy("AgentOrDashboard", p => p.RequireAuthenticatedUser());
    });

    // ── SignalR ───────────────────────────────────────────────────────────
    builder.Services.AddSignalR(opts =>
    {
        opts.EnableDetailedErrors      = builder.Environment.IsDevelopment();
        opts.MaximumReceiveMessageSize = 512 * 1024;
        opts.KeepAliveInterval         = TimeSpan.FromSeconds(15);
        opts.ClientTimeoutInterval     = TimeSpan.FromSeconds(60);
    });

    // ── CORS ──────────────────────────────────────────────────────────────
    builder.Services.AddCors(opts =>
        opts.AddPolicy("DevCors", p =>
            p.AllowAnyMethod().AllowAnyHeader()
             .SetIsOriginAllowed(_ => true).AllowCredentials()));

    // ── Services ──────────────────────────────────────────────────────────
    builder.Services.AddScoped<AgentService>();
    builder.Services.AddScoped<InstitutionService>();
    builder.Services.AddHostedService<DiscoveryHostedService>();
    builder.Services.AddHostedService<OfflineDetectorService>();
    builder.Services.AddControllers();

    var app = builder.Build();

    // ── Startup: migrate + seed ───────────────────────────────────────────
    using (var scope = app.Services.CreateScope())
    {
        var db      = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        var roleMgr  = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userMgr  = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        foreach (var role in new[] { "SuperAdmin", "InstitutionAdmin", "Viewer" })
            if (!await roleMgr.RoleExistsAsync(role))
                await roleMgr.CreateAsync(new IdentityRole(role));

        var adminPass = app.Configuration["AdminPassword"] ?? "Admin@1234!";
        if (await userMgr.FindByNameAsync("admin") == null)
        {
            var admin = new User { UserName = "admin", Email = "admin@local", CreatedAt = DateTime.UtcNow };
            await userMgr.CreateAsync(admin, adminPass);
            await userMgr.AddToRoleAsync(admin, "SuperAdmin");
        }
    }

    app.UseSerilogRequestLogging();
    if (app.Environment.IsDevelopment()) app.UseCors("DevCors");
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    app.MapHub<MonitorHub>("/monitor");
    app.MapGet("/health", () => Results.Ok(new { status = "ok", ts = DateTime.UtcNow }));

    await app.RunAsync();
}
catch (Exception ex) { Log.Fatal(ex, "Server terminated"); return 1; }
finally { await Log.CloseAndFlushAsync(); }
return 0;
