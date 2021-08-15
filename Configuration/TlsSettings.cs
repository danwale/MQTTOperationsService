using System.Security.Authentication;
using System.Text.Json.Serialization;

namespace MQTTOperationsService.Configuration
{
    public class TlsSettings
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public SslProtocols SslProtocol
        {
            get; set;
        } = SslProtocols.Tls12;

        public bool VerifyHostname
        {
            get; set;
        } = true;

        public string CAFile
        {
            get; set;
        }

        public string ClientCert
        {
            get; set;
        }
    }
}
