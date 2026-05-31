using PeripheralMonitor.Agent;
using PeripheralMonitor.Agent.Services;
using Serilog;
using Serilog.Events;

var exeDir = AppContext.BaseDirectory;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var host = Host.CreateDefaultBuilder(args)
        .UseContentRoot(exeDir)
        .ConfigureHostConfiguration(c => c.SetBasePath(exeDir))
        .ConfigureAppConfiguration((_, c) =>
        {
            c.SetBasePath(exeDir);
            c.AddJsonFile("appsettings.json",       optional: true, reloadOnChange: true);
            c.AddJsonFile("appsettings.local.json",  optional: true, reloadOnChange: true);
            c.AddEnvironmentVariables(prefix: "PM_");
        })
        .UseSerilog((ctx, services, cfg) =>
            cfg.ReadFrom.Configuration(ctx.Configuration)
               .ReadFrom.Services(services)
               .Enrich.FromLogContext()
               .Enrich.WithMachineName()
               .Enrich.WithProperty("Application", "Agent")
               .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
               .WriteTo.File(Path.Combine(exeDir, "logs", "agent-.log"),
                   rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
               .WriteTo.Seq(ctx.Configuration["Seq:Url"] ?? "http://localhost:5341"))
        .ConfigureServices((ctx, services) =>
        {
            services.AddSingleton<DiscoveryService>();
            services.AddSingleton<IDeviceMonitor>(sp =>
            {
                if (OperatingSystem.IsWindows())
                    return new WindowsDeviceMonitor(sp.GetRequiredService<ILogger<WindowsDeviceMonitor>>());
                return new LinuxDeviceMonitor(sp.GetRequiredService<ILogger<LinuxDeviceMonitor>>());
            });
            services.AddHostedService<AgentWorker>();
        })
        .Build();

    await host.RunAsync();
    return 0;
}
catch (Exception ex) { Log.Fatal(ex, "Agent terminated"); return 1; }
finally { await Log.CloseAndFlushAsync(); }
