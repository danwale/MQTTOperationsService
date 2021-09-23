using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using MQTTOperationsService;

using IHost host = Host.CreateDefaultBuilder(args).
    ConfigureAppConfiguration((hostingContext, configuration) => 
    {
        configuration.Sources.Clear();

        IHostEnvironment env = hostingContext.HostingEnvironment;

        string processFile = Process.GetCurrentProcess().MainModule.FileName;
        string rootDirectory = Path.GetDirectoryName(processFile);
        if (rootDirectory.EndsWith("bin"))
        {
            rootDirectory = Directory.GetParent(rootDirectory).FullName;
        }
        Directory.SetCurrentDirectory(rootDirectory);

        configuration.SetBasePath(Path.Combine(rootDirectory, "config"))
                     .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                     .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true, reloadOnChange: true);
    })
    .UseWindowsService(options =>
    {
        options.ServiceName = "MQTT Operations Service";
    })
    .ConfigureServices((hostContext, services) =>
    {
        services.AddHostedService<WindowsBackgroundService>()
                .AddSingleton<IMQTTWorker, MQTTWorker>();
    })
    .Build();

await host.RunAsync();