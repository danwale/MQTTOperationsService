using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MQTTOperationsService.Configuration;

namespace MQTTOperationsService
{
    public interface IMQTTWorker
    {
        Task<bool> Initialise(CancellationToken ct);

        Task ProcessPayload(Operation operation, Dictionary<string, string> payloadParams, string responseTopic = null);
    }
}
