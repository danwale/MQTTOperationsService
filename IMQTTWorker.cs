using System.Threading;
using System.Threading.Tasks;

namespace MQTTOperationsService
{
    public interface IMQTTWorker
    {
        Task<bool> Initialise(CancellationToken ct);
    }
}
