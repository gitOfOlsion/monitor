using PeripheralMonitor.Server.Data;
using PeripheralMonitor.Server.Services;
using PeripheralMonitor.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, cfg) =>
        cfg.ReadFrom.Configuration(ctx.Configuration)
           .ReadFrom.Services(services)
           .Enrich.FromLogContext()
           .Enrich.WithMachineName()
           .Enrich.WithProperty("Application", "Web")
           .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
           .WriteTo.Seq(ctx.Configuration["Seq:Url"] ?? "http://seq:5341"));

    // ── Shared DB (same connection as Server — Web reads directly) ────────
    builder.Services.AddDbContextFactory<AppDbContext>(opts =>
        opts.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

    // ── Identity ──────────────────────────────────────────────────────────
    builder.Services.AddIdentity<User, IdentityRole>(opts =>
        opts.SignIn.RequireConfirmedAccount = false)
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

    builder.Services.ConfigureApplicationCookie(opts =>
    {
        opts.LoginPath  = "/login";
        opts.LogoutPath = "/logout";
        opts.ExpireTimeSpan = TimeSpan.FromHours(8);
        opts.SlidingExpiration = true;
    });

    builder.Services.AddAuthorization();
    builder.Services.AddCascadingAuthenticationState();

    // ── Blazor Server ─────────────────────────────────────────────────────
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();
    builder.Services.AddControllers();

    // ── Application Services ──────────────────────────────────────────────
    builder.Services.AddScoped<AgentService>();
    builder.Services.AddScoped<InstitutionService>();
    builder.Services.AddSingleton<RealtimeService>();

    var app = builder.Build();

    app.UseSerilogRequestLogging();
    app.UseStaticFiles();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();
    app.MapControllers();

    app.MapRazorComponents<PeripheralMonitor.Web.Components.App>()
       .AddInteractiveServerRenderMode();

    await app.RunAsync();
}
catch (Exception ex) { Log.Fatal(ex, "Web terminated"); return 1; }
finally { await Log.CloseAndFlushAsync(); }
return 0;
