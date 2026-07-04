using PeripheralMonitor.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration().MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, svc, cfg) =>
        cfg.ReadFrom.Configuration(ctx.Configuration).ReadFrom.Services(svc)
           .Enrich.FromLogContext().Enrich.WithMachineName().Enrich.WithProperty("Application", "Web")
           .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
           .WriteTo.Seq(ctx.Configuration["Seq:Url"] ?? "http://seq:5341"));

    builder.Services.AddRazorComponents().AddInteractiveServerComponents();
    builder.Services.AddControllers();
    builder.Services.AddHttpContextAccessor();

    // Behind the Caddy reverse proxy, TLS is terminated at Caddy and requests reach
    // this container over HTTP. Honour X-Forwarded-Proto/-For so the app knows the
    // original request was HTTPS (correct scheme for redirects + Secure cookies).
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // Only Caddy (on the internal Docker network) fronts this app; clear the
        // default localhost-only restriction so the forwarded headers are honoured.
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
    });

    builder.Services.AddAuthentication("Cookies")
        .AddCookie("Cookies", o =>
        {
            o.LoginPath = "/login";
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            // Cookie is Secure whenever the request is HTTPS (always, in production
            // behind Caddy), but still works over plain HTTP in local/dev.
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.Cookie.SameSite = SameSiteMode.Lax;
        });
    builder.Services.AddAuthorization();
    builder.Services.AddCascadingAuthenticationState();

    var serverUrl = builder.Configuration["ServerApiUrl"] ?? "http://server:5000";
    builder.Services.AddHttpClient<ServerApiClient>(c => c.BaseAddress = new Uri(serverUrl));
    builder.Services.AddScoped<RealtimeService>();
    builder.Services.AddScoped<TimeZoneService>();

    var app = builder.Build();

    // Must run before auth/redirect middleware so the correct scheme is applied.
    app.UseForwardedHeaders();
    app.UseSerilogRequestLogging();
    app.UseStaticFiles();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();
    app.MapControllers();
    app.MapRazorComponents<PeripheralMonitor.Web.Components.App>().AddInteractiveServerRenderMode();

    await app.RunAsync();
}
catch (Exception ex) { Log.Fatal(ex, "Web terminated"); return 1; }
finally { await Log.CloseAndFlushAsync(); }
return 0;
