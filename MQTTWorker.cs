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
using System.Security.Cryptography;
using System.Net;

namespace MQTTOperationsService
{
    public class MQTTWorker : IMQTTWorker
    {
        private readonly ILogger<MQTTWorker> Logger;
        private readonly IConfiguration _configuration;

        private readonly IDictionary<string, Operation> _operations = new Dictionary<string, Operation>();

        private static IMqttClient _mqttClient;

        public MQTTWorker(IConfiguration configuration, ILogger<MQTTWorker> logger)
        {
            _configuration = configuration;
            Logger = logger;
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
                Logger.LogInformation($"MQTT Disconnected, attempting a reconnect in {mqttSettings.ReconnectDelayMs}ms.");
                _operations.Clear(); //clear the operations each time we restart the connection
                if (e.Exception != null)
                {
                    Logger.LogInformation("Exception was: {0}", e.Exception.ToString());
                }

                Thread.Sleep(mqttSettings.ReconnectDelayMs); // Retrying on the same broker so using a retry delay in case it a small network outage
                bool result = await Initialise(ct);
                if (result)
                {
                    Logger.LogInformation("Successfully reconnected to {0} MQTT broker.", mqttSettings.Broker.Hostname);
                }
            });

            _mqttClient.UseConnectedHandler(async e =>
            {
                Logger.LogInformation("Connected to MQTT Broker");

                foreach (var operation in operations)
                {
                    Logger.LogDebug($"Subscribing to trigger topic {operation.Topics.Trigger}");
                    var subscribeResult = await _mqttClient.SubscribeAsync(operation.Topics.Trigger, MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce); // QoS 2 subscription
                    if (subscribeResult.Items.Count > 0 && subscribeResult.Items[0].ResultCode == MQTTnet.Client.Subscribing.MqttClientSubscribeResultCode.GrantedQoS2)
                    {
                        Logger.LogInformation($"Successfully subsribed to topic {operation.Topics.Trigger}");
                        _operations.Add(operation.Topics.Trigger, operation);
                    }
                    else
                    {
                        Logger.LogError($"Failed to subsribe to topic {operation.Topics.Trigger}");
                    }
                }
            });

            _ = _mqttClient.UseApplicationMessageReceivedHandler(async args =>
              {
                  string topic = args.ApplicationMessage.Topic;
                  var operation = _operations[topic];
                  Logger.LogInformation($"{operation.Name} triggered by topic {topic}");
                  if (args.ApplicationMessage.Payload != null && args.ApplicationMessage.Payload.Length > 0)
                  {
                      string payload = Encoding.UTF8.GetString(args.ApplicationMessage.Payload);
                      Dictionary<string, string> payloadParams = JsonSerializer.Deserialize<Dictionary<string, string>>(payload);
                      if (payloadParams.ContainsKey("UserID") && payloadParams.ContainsKey("DateTime"))
                      {
                          Logger.LogInformation($"Payload came from UserID: {payloadParams["UserID"]} at {payloadParams["DateTime"]}");
                          if (operation.MessageExpiryIntervalSecs != -1)
                          {
                              if (DateTime.TryParse(payloadParams["DateTime"], out DateTime dateTime))
                              {
                                  if (DateTime.Now.Subtract(dateTime).TotalSeconds < operation.MessageExpiryIntervalSecs)
                                  {
                                      Logger.LogDebug("The message was determined to have a valid expiry");
                                      await ProcessPayload(operation, payloadParams, args.ApplicationMessage.ResponseTopic ?? null);
                                  }
                                  else
                                  {
                                      Logger.LogError("The message was determined to have expired and no operation was completed");
                                  }
                              }
                              else
                              {
                                  Logger.LogError("Failed to parse the DateTime in the payload so couldn't work out if the message had expired, no operation completed");
                              }
                          }
                          else
                          {
                              Logger.LogInformation("The MessageExpiryIntervalSecs on the configured operation is -1 which is no message expiry, processing operation regardless");
                              await ProcessPayload(operation, payloadParams, args.ApplicationMessage.ResponseTopic ?? null);
                          }
                      }
                      else
                      {
                          Logger.LogError("The payload must contain a 'UserID' identifying who sent the payload and a 'DateTime' for the time it was sent");
                      }
                  }
                  else
                  {
                      Logger.LogError("All MQTT trigger messages must contain a payload with a minimum set of properties (UserID and DateTime) and optionally parameters.");
                  }
              });

            bool configuartionErrors = false;
            var clientOptionsBuilder = new MqttClientOptionsBuilder();
            clientOptionsBuilder
                .WithTcpServer(mqttSettings.Broker.Hostname, mqttSettings.Broker.Port)
                .WithCleanSession(mqttSettings.Broker.CleanSession)
                .WithClientId(mqttSettings.ClientID);

            if (mqttSettings.Broker.UseTls)
            {
                if (!string.IsNullOrWhiteSpace(mqttSettings.Broker.TlsSettings.CAFile))
                {
                    IList<X509Certificate2> certificates = new List<X509Certificate2>();
                    var caCert = new X509Certificate2(mqttSettings.Broker.TlsSettings.CAFile);
                    certificates.Add(caCert);
                    // Add the CA certificate to the users CA store
                    using (var caCertStore = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser))
                    {
                        caCertStore.Open(OpenFlags.ReadWrite);
                        caCertStore.Add(caCert);
                    }

                    // If the client certificate is not supplied this means 1-way TLS is being used
                    if (!string.IsNullOrWhiteSpace(mqttSettings.Broker.TlsSettings.ClientCert))
                    {
                        var clientCert = new X509Certificate2(mqttSettings.Broker.TlsSettings.ClientCert);
                        certificates.Add(clientCert);
                        // Add the client certificate to the users personal store
                        using (var personalCertsStore = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser))
                        {
                            personalCertsStore.Open(OpenFlags.ReadWrite);
                            personalCertsStore.Add(clientCert);
                        }
                    }
                    else
                    {
                        Logger.LogDebug("No ClientCert was configured, a 1-way TLS connection will be attempted");
                    }

                    clientOptionsBuilder.WithTls(new MqttClientOptionsBuilderTlsParameters()
                    {
                        Certificates = certificates,
                        UseTls = true,
                        SslProtocol = mqttSettings.Broker.TlsSettings.SslProtocol,
                        AllowUntrustedCertificates = false,
                        IgnoreCertificateChainErrors = false,
                        IgnoreCertificateRevocationErrors = false,
                        CertificateValidationHandler = (MqttClientCertificateValidationCallbackContext context) =>
                        {
                            Logger.LogDebug("Certificate--> issuer: " + context.Certificate.Issuer + " subject: " + context.Certificate.Subject);
                            if (mqttSettings.Broker.TlsSettings.VerifyHostname)
                            {
                                Logger.LogDebug("Connection address verification is being performed");
                                bool verified = VerifyConnectionAddress(context, mqttSettings);
                                Logger.LogDebug($"Connection address validation result: {verified}");
                                return verified;
                            }
                            else
                            {
                                Logger.LogDebug("Connection address verification is not being performed, it will ignore SAN and CN entries");
                                return true;
                            }
                        }
                    });
                }
                else
                {
                    Logger.LogError("Configured to use TLS Security but the CAFile was not specified.");
                    configuartionErrors = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(mqttSettings.Broker.Username) && !string.IsNullOrWhiteSpace(mqttSettings.Broker.Password))
            {
                clientOptionsBuilder.WithCredentials(new MqttClientCredentials()
                {
                    Username = mqttSettings.Broker.Username,
                    Password = UTF8Encoding.UTF8.GetBytes(mqttSettings.Broker.Password)
                });
            }

            bool connectResult = false;
            if (!configuartionErrors)
            {
                // Build the MQTT Client Options
                var mqttClientOptions = clientOptionsBuilder.Build();
                MqttClientAuthenticateResult result = null;
                try
                {
                    result = await _mqttClient.ConnectAsync(mqttClientOptions, ct);
                    if (result.ResultCode != MqttClientConnectResultCode.Success)
                    {
                        // This code below will switch between the primary and secondary brokers if the connection fails to connect
                        Logger.LogError("Failure to connect to {0} broker", mqttSettings.Broker.Hostname);
                        Logger.LogError("Error Code: {0}", result.ResultCode.ToString());
                    }
                    else
                    {
                        connectResult = true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError("An exception occured during the MQTT connect: {0}", ex);
                }
            }
            else
            {
                Logger.LogError("Due to configuration errors the connection could not be attempted, check the logs and the appsettings.json file.");
            }
            
            return connectResult;
        }

        private bool VerifyConnectionAddress(MqttClientCertificateValidationCallbackContext context, MQTTSettings mqttSettings)
        {
            try
            {
                X509Certificate2 serverCertificate = (X509Certificate2)context.Certificate;
                Dictionary<string, string> SANs = GetSubjectAlternativeNames(serverCertificate);
                if (SANs.Count > 0)
                {
                    // The Subject Alternative Name extension existed so use this for validation
                    foreach (string key in SANs.Keys)
                    {
                        try
                        {
                            if (key.Equals("IP Address", StringComparison.CurrentCultureIgnoreCase))
                            {
                                if (mqttSettings.Broker.Hostname.Equals(SANs[key]))
                                {
                                    Logger.LogDebug("The service was configured to connect using an IP Address that matched a SAN IP Address entry");
                                    return true;
                                }
                            }
                            else if (key.Equals("DNS", StringComparison.CurrentCultureIgnoreCase))
                            {
                                if (mqttSettings.Broker.Hostname.Equals(SANs[key]))
                                {
                                    Logger.LogDebug("The service was configured to connect using a DNS Address that matched a SAN DNS entry");
                                    return true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "An error occurred while validation the Subject Alternative Names entries on the server certificate");
                        }
                    }
                    return false; // IF the SAN extenstion existed on the certificate it must be used for connection address verification
                }
                else
                {
                    // No Subject Alternative Name extension existed so using the CN of the Subject
                    string cn = GetCertificateCN(serverCertificate);
                    if (!string.IsNullOrWhiteSpace(cn))
                    {
                        if (cn.Equals(mqttSettings.Broker.Hostname, StringComparison.CurrentCultureIgnoreCase))
                        {
                            Logger.LogDebug("Successfully matched the CN to the connection address used");
                            return true;
                        }
                        else
                        {
                            Logger.LogWarning("The address used for the connection did not match the certificate's CN in the subject, the cert had no Subject Altenative Names defined as an extension.");
                            return false;
                        }
                    }
                    else
                    {
                        Logger.LogWarning("The CN on the server certificate was not valid.");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "An error occurred verifying the server certificate matched the address used for the connection");
                return false;
            }
        }

        private string GetCertificateCN(X509Certificate2 certificate)
        {
            int posOfCN = certificate.Subject.IndexOf("CN=");
            if (posOfCN >= 0)
            {
                string cn = certificate.Subject.Substring(posOfCN + 3).Split(',')[0];
                return cn;
            }
            else
            {
                Logger.LogError("The CN was not found in the certificate subject, the certificate is invalid");
                return null;
            }
        }

        private Dictionary<string, string> GetSubjectAlternativeNames(X509Certificate2 certificate)
        {
            Dictionary<string, string> SanEntries = new Dictionary<string, string>();

            List<string> data = new List<string>();
            foreach(X509Extension extension in certificate.Extensions)
            {
                if (string.Equals(extension.Oid.FriendlyName, "Subject Alternative Name"))
                {
                    AsnEncodedData asnData = new AsnEncodedData(extension.Oid, extension.RawData);
                    data.AddRange(asnData.Format(true).Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries));
                    break;
                }
            }
            foreach (string entry in data)
            {
                if (entry.Contains("="))
                {
                    string[] keyValuePair = entry.Split("=");
                    SanEntries.Add(keyValuePair[0], keyValuePair[1]);
                }
                else
                {
                    Logger.LogError("The SAN entry was not formatted correctly, it should be DNS=Value or IP Address=Value, the = was missing");
                }
            }

            return SanEntries;
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
                        Logger.LogError("An error occurred while running the script.");
                        var sb = new StringBuilder();
                        foreach (var errorStream in powerShell.Streams.Error)
                        {
                            sb.AppendLine(errorStream.Exception.Message);
                        }
                        Logger.LogError($"Error Details: {sb.ToString()}");
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
                                Logger.LogWarning($"Operation {operation.Name} is configured to send a response, an error occurered which should be sent, but the response topic could not be determined");
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
                                Logger.LogInformation($"Script Output: {sb.ToString()}");
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
                                    Logger.LogWarning($"Operation {operation.Name} is configured to send a response but the response topic could not be determined");
                                }
                            }
                            else
                            {
                                Logger.LogInformation("No script output detected, sending 'Executed Successfully' as a response payload");
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
                                    Logger.LogWarning($"Operation {operation.Name} is configured to send a response but the response topic could not be determined");
                                }
                            }
                            await _mqttClient.PublishAsync(responseMessage);
                        }
                    }

                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error excecuting script for {operation.Name}, Exception: {ex.Message}");
            }
        }
    }
}
