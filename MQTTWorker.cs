using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;
using MQTTnet.Client.Connecting;

using MQTTOperationsService.Configuration;

namespace MQTTOperationsService
{
    public class MQTTWorker : IMQTTWorker
    {
        private readonly ILogger<MQTTWorker> _logger;
        private readonly IConfiguration _configuration;

        private IDictionary<string, Operation> _operations = new Dictionary<string, Operation>();

        private static IMqttClient _mqttClient;

        public MQTTWorker(IConfiguration configuration, ILogger<MQTTWorker> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<bool> Initialise(CancellationToken ct)
        {
            MQTTSettings mqttSettings = new();
            _configuration.GetSection("MQTT").Bind(mqttSettings);
            IList<Operation> operations = new List<Operation>();
            _configuration.GetSection("Operations").Bind(operations);

            var factory = new MqttFactory();
            _mqttClient = factory.CreateMqttClient();

            _mqttClient.UseDisconnectedHandler(async e =>
            {
                _logger.LogInformation("MQTT Disconnected, attempting a reconnect...");
                if (e.Exception != null)
                {
                    _logger.LogInformation("Exception was: {0}", e.Exception.ToString());
                }

                Thread.Sleep(mqttSettings.ReconnectDelayMs); // Retrying on the same broker so using a retry delay in case it a small network outage
                bool result = await Initialise(ct);
                if (result)
                {
                    _logger.LogInformation("Successfully reconnected to {0} MQTT broker.", mqttSettings.Broker.Hostname);
                }
            });

            _mqttClient.UseConnectedHandler(async e =>
            {
                _logger.LogInformation("Connected to MQTT Broker");

                foreach (var operation in operations)
                {
                    _logger.LogDebug($"Subscribing to trigger topic {operation.Topics.Trigger}");
                    var subscribeResult = await _mqttClient.SubscribeAsync(operation.Topics.Trigger, MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce); // QoS 2 subscription
                    if (subscribeResult.Items.Count > 0 && subscribeResult.Items[0].ResultCode == MQTTnet.Client.Subscribing.MqttClientSubscribeResultCode.GrantedQoS2)
                    {
                        _logger.LogInformation($"Successfully subsribed to topic {operation.Topics.Trigger}");
                        _operations.Add(operation.Topics.Trigger, operation);
                    }
                    else
                    {
                        _logger.LogError($"Failed to subsribe to topic {operation.Topics.Trigger}");
                    }
                }
            });

            _mqttClient.UseApplicationMessageReceivedHandler(async args =>
            {
                string topic = args.ApplicationMessage.Topic;
                var operation = _operations[topic];
                _logger.LogInformation($"{operation.Name} triggered");
                string payload = Encoding.UTF8.GetString(args.ApplicationMessage.Payload);
                Dictionary<string, string>  payloadParams = JsonSerializer.Deserialize<Dictionary<string, string>>(payload);
                PowerShell ps = PowerShell.Create();
                ps.AddScript(operation.Command);
                foreach (var param in operation.Parameters)
                {
                    ps.AddParameter(param.Name, payloadParams[param.Name]);
                }
                var result = ps.Invoke();
                if (operation.CaptureOutput)
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (var r in result)
                    {
                        sb.Append(r.ToString());
                    }
                    var responseMessage = new MqttApplicationMessage()
                    {
                        Payload = UTF8Encoding.UTF8.GetBytes(sb.ToString()),
                        QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce,
                        Topic = args.ApplicationMessage.ResponseTopic ?? operation.Topics.Response,
                        Retain = false
                    };
                    await _mqttClient.PublishAsync(responseMessage);
                }
                        
            });

            var clientOptionsBuilder = new MqttClientOptionsBuilder();
            IList<X509Certificate2> certificates = new List<X509Certificate2>();
            var caCert = new X509Certificate2(mqttSettings.CAFile);
            var clientCert = new X509Certificate2(mqttSettings.ClientCert);

            certificates.Add(caCert);
            certificates.Add(clientCert);

            // Add the CA certificate to the users CA store
            using (var caCertStore = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser))
            {
                caCertStore.Open(OpenFlags.ReadWrite);
                caCertStore.Add(caCert);
            }

            // Add the client certificate to the users personal store
            using (var personalCertsStore = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser))
            {
                personalCertsStore.Open(OpenFlags.ReadWrite);
                personalCertsStore.Add(clientCert);
            }

            clientOptionsBuilder
                .WithTcpServer(mqttSettings.Broker.Hostname, mqttSettings.Broker.Port)
                .WithCleanSession(false)
                .WithClientId(mqttSettings.ClientID)
                .WithTls(new MqttClientOptionsBuilderTlsParameters()
                {
                    Certificates = certificates,
                    UseTls = true,
                    AllowUntrustedCertificates = false,
                    IgnoreCertificateChainErrors = false,
                    IgnoreCertificateRevocationErrors = true,
                    CertificateValidationHandler = (MqttClientCertificateValidationCallbackContext c) =>
                    {
                        _logger.LogInformation("Certificate--> issuer: " + c.Certificate.Issuer + " subject: " + c.Certificate.Subject);
                        return true;
                    }
                });

            // Build the MQTT Client Options
            var mqttClientOptions = clientOptionsBuilder.Build();
            MqttClientAuthenticateResult result = null;
            bool connectResult = false;
            try
            {
                result = await _mqttClient.ConnectAsync(mqttClientOptions, ct);
                if (result.ResultCode != MqttClientConnectResultCode.Success)
                {
                    // This code below will switch between the primary and secondary brokers if the connection fails to connect
                    _logger.LogError("Failure to connect to {0} broker", mqttSettings.Broker.Hostname);
                    _logger.LogError("Error Code: {0}", result.ResultCode.ToString());
                }
                else
                {
                    connectResult = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("An exception occured during the MQTT connect: {0}", ex);
            }
            return connectResult;
        }
    }
}
