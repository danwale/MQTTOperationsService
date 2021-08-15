using System;
using System.Collections.Generic;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Security.Authentication;
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

        private readonly IDictionary<string, Operation> _operations = new Dictionary<string, Operation>();

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
                _logger.LogInformation($"MQTT Disconnected, attempting a reconnect in {mqttSettings.ReconnectDelayMs}ms.");
                _operations.Clear(); //clear the operations each time we restart the connection
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

            _ = _mqttClient.UseApplicationMessageReceivedHandler(async args =>
              {
                  string topic = args.ApplicationMessage.Topic;
                  var operation = _operations[topic];
                  _logger.LogInformation($"{operation.Name} triggered by topic {topic}");
                  if (args.ApplicationMessage.Payload != null && args.ApplicationMessage.Payload.Length > 0)
                  {
                      string payload = Encoding.UTF8.GetString(args.ApplicationMessage.Payload);
                      Dictionary<string, string> payloadParams = JsonSerializer.Deserialize<Dictionary<string, string>>(payload);
                      if (payloadParams.ContainsKey("UserID") && payloadParams.ContainsKey("DateTime"))
                      {
                          _logger.LogInformation($"Payload came from UserID: {payloadParams["UserID"]} at {payloadParams["DateTime"]}");
                          if (operation.MessageExpiryIntervalSecs != -1)
                          {
                              if (DateTime.TryParse(payloadParams["DateTime"], out DateTime dateTime))
                              {
                                  if (DateTime.Now.Subtract(dateTime).TotalSeconds < operation.MessageExpiryIntervalSecs)
                                  {
                                      _logger.LogDebug("The message was determined to have a valid expiry");
                                      await ProcessPayload(operation, payloadParams, args.ApplicationMessage.ResponseTopic ?? null);
                                  }
                                  else
                                  {
                                      _logger.LogError("The message was determined to have expired and no operation was completed");
                                  }
                              }
                              else
                              {
                                  _logger.LogError("Failed to parse the DateTime in the payload so couldn't work out if the message had expired, no operation completed");
                              }
                          }
                          else
                          {
                              _logger.LogInformation("The MessageExpiryIntervalSecs on the configured operation is -1 which is no message expiry, processing operation regardless");
                              await ProcessPayload(operation, payloadParams, args.ApplicationMessage.ResponseTopic ?? null);
                          }
                      }
                      else
                      {
                          _logger.LogError("The payload must contain a 'UserID' identifying who sent the payload and a 'DateTime' for the time it was sent");
                      }
                  }
                  else
                  {
                      _logger.LogError("All MQTT trigger messages must contain a payload with a minimum set of properties (UserID and DateTime) and optionally parameters.");
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
                    SslProtocol = SslProtocols.Tls12,
                    AllowUntrustedCertificates = false,
                    IgnoreCertificateChainErrors = false,
                    IgnoreCertificateRevocationErrors = true,
                    CertificateValidationHandler = (MqttClientCertificateValidationCallbackContext c) =>
                    {
                        _logger.LogDebug("Certificate--> issuer: " + c.Certificate.Issuer + " subject: " + c.Certificate.Subject);
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


        private async Task ProcessPayload(Operation operation, Dictionary<string, string> payloadParams, string responseTopic = null)
        {
            try
            {
                PowerShell powerShell = PowerShell.Create();
                InitialSessionState initial = InitialSessionState.CreateDefault();
                initial.AuthorizationManager = new AuthorizationManager("Microsoft.PowerShell");
                using (Runspace runspace = RunspaceFactory.CreateRunspace(initial))
                {
                    runspace.Open();
                    runspace.SessionStateProxy.Path.SetLocation(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
                    powerShell.Runspace = runspace;
                    powerShell.AddCommand(operation.Command);
                    foreach (var param in operation.Parameters)
                    {
                        powerShell.AddParameter(param.Name, payloadParams[param.Name]);
                    }
                    var result = powerShell.Invoke();

                    if (powerShell.Streams.Error.Count > 0)
                    {
                        _logger.LogError("An error occurred while running the script.");
                        var sb = new StringBuilder();
                        foreach (var errorStream in powerShell.Streams.Error)
                        {
                            sb.AppendLine(errorStream.Exception.Message);
                        }
                        _logger.LogError($"Error Details: {sb.ToString()}");
                        if (operation.CaptureOutput)
                        {
                            MqttApplicationMessage responseMessage = null;
                            responseTopic = responseTopic ?? operation.Topics.Response;
                            if (!string.IsNullOrWhiteSpace(responseTopic))
                            {
                                responseMessage = new MqttApplicationMessage()
                                {
                                    Payload = UTF8Encoding.UTF8.GetBytes(sb.ToString()),
                                    QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce,
                                    Topic = responseTopic ?? operation.Topics.Response,
                                    Retain = false
                                };
                                await _mqttClient.PublishAsync(responseMessage);
                            }
                            else
                            {
                                _logger.LogWarning($"Operation {operation.Name} is configured to send a response, an error occurered which should be sent, but the response topic could not be determined");
                            }
                        }
                    }
                    else
                    {
                        if (operation.CaptureOutput)
                        {
                            MqttApplicationMessage responseMessage = null;
                            if (powerShell.Streams.Information.Count > 0)
                            {
                                var sb = new StringBuilder();
                                foreach (var infoStream in powerShell.Streams.Information)
                                {
                                    sb.AppendLine(infoStream.MessageData.ToString());
                                }
                                _logger.LogInformation($"Script Output: {sb.ToString()}");
                                responseTopic = responseTopic ?? operation.Topics.Response;
                                if (!string.IsNullOrWhiteSpace(responseTopic))
                                {
                                    responseMessage = new MqttApplicationMessage()
                                    {
                                        Payload = UTF8Encoding.UTF8.GetBytes(sb.ToString()),
                                        QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce,
                                        Topic = responseTopic ?? operation.Topics.Response,
                                        Retain = false
                                    };
                                }
                                else
                                {
                                    _logger.LogWarning($"Operation {operation.Name} is configured to send a response but the response topic could not be determined");
                                }
                            }
                            else
                            {
                                _logger.LogInformation("No script output detected, sending 'Executed Successfully' as a response payload");
                                responseTopic = responseTopic ?? operation.Topics.Response;
                                if (!string.IsNullOrWhiteSpace(responseTopic))
                                {
                                    responseMessage = new MqttApplicationMessage()
                                    {
                                        Payload = UTF8Encoding.UTF8.GetBytes("Executed Successfully"),
                                        QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce,
                                        Topic = responseTopic ?? operation.Topics.Response,
                                        Retain = false
                                    };
                                }
                                else
                                {
                                    _logger.LogWarning($"Operation {operation.Name} is configured to send a response but the response topic could not be determined");
                                }
                            }
                            await _mqttClient.PublishAsync(responseMessage);
                        }
                    }

                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error excecuting script for {operation.Name}, Exception: {ex.Message}");
            }
        }
    }
}
