using PeripheralMonitor.Agent;
using PeripheralMonitor.Agent.Services;
using Serilog;
using Serilog.Events;

var exeDir = AppContext.BaseDirectory;
Log.Logger = new LoggerConfiguration().MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console().CreateBootstrapLogger();

try
{
    var host = Host.CreateDefaultBuilder(args)
        .UseContentRoot(exeDir)
        .ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.SetBasePath(exeDir);
            cfg.AddJsonFile("appsettings.json",       optional: true, reloadOnChange: true);
            cfg.AddJsonFile("appsettings.local.json",  optional: true, reloadOnChange: true);
            cfg.AddEnvironmentVariables(prefix: "PM_");
        })
        .UseSerilog((ctx, svc, cfg) =>
            cfg.ReadFrom.Configuration(ctx.Configuration).ReadFrom.Services(svc)
               .Enrich.FromLogContext().Enrich.WithMachineName()
               .Enrich.WithProperty("Application", "Agent")
               .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
               .WriteTo.File(Path.Combine(exeDir, "logs", "agent-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
               .WriteTo.Seq(ctx.Configuration["Seq:Url"] ?? "http://localhost:5341"))
        .ConfigureServices((_, svc) =>
        {
            svc.AddSingleton<DiscoveryService>();
            svc.AddSingleton<IDeviceMonitor>(sp =>
                OperatingSystem.IsWindows()
                    ? (IDeviceMonitor)new WindowsDeviceMonitor(sp.GetRequiredService<ILogger<WindowsDeviceMonitor>>())
                    : new LinuxDeviceMonitor(sp.GetRequiredService<ILogger<LinuxDeviceMonitor>>()));
            svc.AddHostedService<AgentWorker>();
        })
        .Build();

    await host.RunAsync();
    return 0;
}
catch (Exception ex) { Log.Fatal(ex, "Agent terminated"); return 1; }
finally { await Log.CloseAndFlushAsync(); }
