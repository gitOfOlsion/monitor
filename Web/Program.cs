using PeripheralMonitor.Web.Services;
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

    builder.Services.AddAuthentication("Cookies")
        .AddCookie("Cookies", o => { o.LoginPath = "/login"; o.ExpireTimeSpan = TimeSpan.FromHours(8); });
    builder.Services.AddAuthorization();
    builder.Services.AddCascadingAuthenticationState();

    var serverUrl = builder.Configuration["ServerApiUrl"] ?? "http://server:5000";
    builder.Services.AddHttpClient<ServerApiClient>(c => c.BaseAddress = new Uri(serverUrl));
    builder.Services.AddScoped<RealtimeService>();

    var app = builder.Build();

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
