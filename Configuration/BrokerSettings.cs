using System.Security.Authentication;
using System.Text.Json.Serialization;

namespace MQTTOperationsService.Configuration
{
    public class BrokerSettings
    {
        public string Hostname
        { 
            get; set;
        }

        public int Port
        { 
            get; set; 
        } = 8883;

        public bool CleanSession
        {
            get; set;
        } = false;

        public bool UseTls
        {
            get; set;
        } = true;

        public TlsSettings TlsSettings
        {
            get; set;
        }

        public string Username
        {
            get; set;
        }

        public string Password
        {
            get; set;
        }
    }
}
