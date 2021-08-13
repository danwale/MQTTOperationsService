namespace MQTTOperationsService.Configuration
{
    public class MQTTSettings
    {
        public BrokerSettings Broker
        {
            get; set;
        }

        public string ClientID
        {
            get; set;
        }

        public string CAFile
        {
            get; set;
        }

        public string ClientCert
        {
            get; set;
        }

        public int ReconnectDelayMs
        {
            get; set;
        }
    }
}
