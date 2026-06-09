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
        {
            cfg.ReadFrom.Configuration(ctx.Configuration).ReadFrom.Services(svc)
               .Enrich.FromLogContext().Enrich.WithMachineName()
               .Enrich.WithProperty("Application", "Agent")
               .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
               .WriteTo.File(Path.Combine(exeDir, "logs", "agent-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14);

            // Seq is optional — only add if a valid URL is configured
            var seqUrl = ctx.Configuration["Seq:Url"];
            if (!string.IsNullOrEmpty(seqUrl) && Uri.TryCreate(seqUrl, UriKind.Absolute, out _))
                cfg.WriteTo.Seq(seqUrl);
        })
        .ConfigureServices((_, svc) =>
        {
            svc.AddSingleton<DiscoveryService>();
#if PLATFORM_WINDOWS
            svc.AddSingleton<IDeviceMonitor>(sp =>
                new WindowsDeviceMonitor(sp.GetRequiredService<ILogger<WindowsDeviceMonitor>>()));
#elif PLATFORM_LINUX
            svc.AddSingleton<IDeviceMonitor>(sp =>
                new LinuxDeviceMonitor(sp.GetRequiredService<ILogger<LinuxDeviceMonitor>>()));
#else
            // Framework-dependent build (no RID) — detect at runtime
            svc.AddSingleton<IDeviceMonitor>(sp =>
                OperatingSystem.IsWindows()
                    ? (IDeviceMonitor)new WindowsDeviceMonitor(sp.GetRequiredService<ILogger<WindowsDeviceMonitor>>())
                    : new LinuxDeviceMonitor(sp.GetRequiredService<ILogger<LinuxDeviceMonitor>>()));
#endif
            svc.AddHostedService<AgentWorker>();
        })
        .Build();

    await host.RunAsync();
    return 0;
}
catch (Exception ex) { Log.Fatal(ex, "Agent terminated"); return 1; }
finally { await Log.CloseAndFlushAsync(); }
