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

Log.Logger = new LoggerConfiguration().MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, svc, cfg) =>
        cfg.ReadFrom.Configuration(ctx.Configuration).ReadFrom.Services(svc)
           .Enrich.FromLogContext().Enrich.WithMachineName().Enrich.WithThreadId()
           .Enrich.WithProperty("Application", "Server")
           .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
           .WriteTo.Seq(ctx.Configuration["Seq:Url"] ?? "http://seq:5341"));

    builder.Services.AddDbContextFactory<AppDbContext>(o =>
        o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

    builder.Services.AddIdentity<AppUser, IdentityRole>(o =>
    {
        o.Password.RequireDigit = true; o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false; o.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();

    var jwtSecret = builder.Configuration["Jwt:Secret"]
        ?? throw new InvalidOperationException("Jwt:Secret required");
    var jwtKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

    builder.Services.AddAuthentication(o => { o.DefaultAuthenticateScheme = "Smart"; o.DefaultChallengeScheme = "Smart"; })
        .AddPolicyScheme("Smart", "Smart", o =>
        {
            o.ForwardDefaultSelector = ctx =>
                ctx.Request.Path.StartsWithSegments("/monitor") || ctx.Request.Path.StartsWithSegments("/api/agent")
                    ? JwtBearerDefaults.AuthenticationScheme : IdentityConstants.ApplicationScheme;
        })
        .AddJwtBearer(o =>
        {
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
                ValidateIssuerSigningKey = true, ValidIssuer = "PeripheralMonitor", ValidAudience = "Agent",
                IssuerSigningKey = jwtKey
            };
            o.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    var t = ctx.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(t) && ctx.HttpContext.Request.Path.StartsWithSegments("/monitor"))
                        ctx.Token = t;
                    return Task.CompletedTask;
                }
            };
        });

    builder.Services.AddAuthorization(o => o.AddPolicy("AgentOrDashboard", p => p.RequireAuthenticatedUser()));
    builder.Services.AddSignalR(o =>
    {
        o.EnableDetailedErrors = builder.Environment.IsDevelopment();
        o.MaximumReceiveMessageSize = 512 * 1024;
        o.KeepAliveInterval = TimeSpan.FromSeconds(15);
    });
    builder.Services.AddCors(o => o.AddPolicy("Dev", p =>
        p.AllowAnyMethod().AllowAnyHeader().SetIsOriginAllowed(_ => true).AllowCredentials()));
    builder.Services.AddScoped<AgentService>();
    builder.Services.AddSingleton<AgentPackageService>();
    builder.Services.AddHostedService<DiscoveryHostedService>();
    builder.Services.AddHostedService<OfflineDetectorService>();
    builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles);

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var db      = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        // EnsureCreated works without migration files — creates tables from model directly.
        // Switch to MigrateAsync once you generate migrations with:
        //   dotnet ef migrations add InitialCreate --project Server --startup-project Server
        await db.Database.EnsureCreatedAsync();

        foreach (var role in new[] { "SuperAdmin", "InstitutionAdmin", "Viewer" })
            if (!await roleMgr.RoleExistsAsync(role)) await roleMgr.CreateAsync(new IdentityRole(role));

        var adminPass = app.Configuration["SuperAdminPassword"] ?? "Admin@1234!";
        if (await userMgr.FindByNameAsync("superadmin") == null)
        {
            var admin = new AppUser { UserName = "superadmin", Email = "admin@local", Role = "SuperAdmin" };
            if ((await userMgr.CreateAsync(admin, adminPass)).Succeeded)
                await userMgr.AddToRoleAsync(admin, "SuperAdmin");
        }
    }

    app.UseSerilogRequestLogging();
    if (app.Environment.IsDevelopment()) app.UseCors("Dev");
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
