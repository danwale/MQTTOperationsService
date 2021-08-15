namespace MQTTOperationsService.Configuration
{
    public class MQTTSettings
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

        public string ClientID
        {
            get; set;
        }

        public int ReconnectDelayMs
        {
            get; set;
        }
    }
}
