using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PeripheralMonitor.Agent;
using PeripheralMonitor.Agent.Services;
using System.IO;

// ✅ Critical: Set content root to executable directory (fixes Windows Service config loading)
//var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) 
             //?? Environment.CurrentDirectory;
var exeDir = AppContext.BaseDirectory;  // ✅ More reliable way to get executable directory

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService()
    .ConfigureHostConfiguration(config =>
    {
        config.SetBasePath(exeDir);  // ✅ Force config to load from exe folder
    })
    .ConfigureAppConfiguration((context, config) =>
    {
        config.SetBasePath(exeDir);  // ✅ Redundant but safe
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        config.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);  // ✅ JWT token file
        config.AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        services.AddHostedService<AgentWorker>();
        services.AddSingleton<DiscoveryService>();
    })
    .Build();

await host.RunAsync();