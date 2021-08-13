using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MQTTOperationsService
{
    public sealed class WindowsBackgroundService : BackgroundService
    {
        private readonly ILogger<WindowsBackgroundService> _logger;
        private readonly IMQTTWorker _mqttWorker;

        public WindowsBackgroundService(IMQTTWorker mqttWorker, 
            ILogger<WindowsBackgroundService> logger)
        {
            _mqttWorker = mqttWorker;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Starting MQTT Operations Service at: {time}", DateTimeOffset.Now);

                await _mqttWorker.Initialise(stoppingToken);
                WhenCancelled(stoppingToken).Wait(stoppingToken);

                _logger.LogInformation("Service stopped.");
            }
        }

        private static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }
    }
}
