using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Net;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Connecting;
using MQTTnet.Client.Options;

using MQTTOperationsService.Configuration;

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

        private string GetFilePath(string path)
        {
            string result = null;
            if (File.Exists(path))
            {
                result = path;
            }
            else
            {
                string fullPath = Path.GetFullPath(path);
                result = fullPath;
            }
            Logger.LogInformation($"Filepath is: {result}");
            return result;
        }

        /// <summary>
        /// Create the MQTT client and connect using the configuration values.
        /// Setup the event handlers for MQTT disconnection/connnection and messages being delivered that the client subscribed to.
        /// </summary>
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
                    Logger.LogInformation("Successfully reconnected to {0} MQTT broker.", mqttSettings.Hostname);
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
                        Logger.LogInformation($"Successfully subsribed to topic {operation.Topics.Trigger} for operation named {operation.Name}");
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
                if (_operations.Count > 0)
                {
                    string topic = args.ApplicationMessage.Topic;
                    var operation = _operations[topic];
                    Logger.LogInformation($"{operation.Name} triggered by topic {topic}");
                    if (args.ApplicationMessage.Payload != null && args.ApplicationMessage.Payload.Length > 0)
                    {
                        Dictionary<string, string> payloadParams = new Dictionary<string, string>();
                        try
                        {
                            string payload = Encoding.UTF8.GetString(args.ApplicationMessage.Payload);
                            payloadParams = JsonSerializer.Deserialize<Dictionary<string, string>>(payload);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, $"Failed to parse the message payload sent to the trigger topic {topic}");
                        }
                        if (payloadParams.ContainsKey("UserID"))
                        {
                            Logger.LogInformation($"Payload came from UserID: {payloadParams["UserID"]}");
                            if (operation.MessageExpiryIntervalSecs != -1) // Check if there is a message expiry value for the operation
                            {
                                if (payloadParams.ContainsKey("DateTime"))
                                {
                                    // If there is a message expiry ensure that the DateTime value in the payload has not past already and that it is within
                                    // the expiry interval that was specified for the operation configured.
                                    if (DateTime.TryParse(payloadParams["DateTime"], out DateTime dateTime))
                                    {
                                        if (DateTime.Now.Subtract(dateTime).TotalSeconds < operation.MessageExpiryIntervalSecs)
                                        {
                                            Logger.LogDebug($"The message was determined to have a valid expiry with at {payloadParams["DateTime"]}");
                                            await ProcessPayload(operation, payloadParams, args.ApplicationMessage.ResponseTopic ?? null);
                                        }
                                        else
                                        {
                                            // The operation had expired, take no action
                                            Logger.LogError($"The message was determined to have expired and no operation was completed, DateTime: {payloadParams["DateTime"]}");
                                        }
                                    }
                                    else
                                    {
                                        // If the DateTime parameter in the payload can not be parsed assume that it has expired and take no action
                                        Logger.LogError($"Failed to parse the DateTime in the payload so couldn't work out if the message had expired, no operation completed, DateTime: {payloadParams["DateTime"]}");
                                    }
                                }
                                else
                                {
                                    /// The check for the DateTime parameter in the payload failed, it can't be verified so no action taken
                                    Logger.LogError("The operation is configured to have a message expiry but there was no 'DateTime' parameter in the payload, no operation completed");
                                }
                            }
                            else
                            {
                                // No message expiry is configured for this operation, allow it to execute the operation regardless of any DateTime
                                Logger.LogInformation("The MessageExpiryIntervalSecs on the configured operation is -1 which is no message expiry, processing operation regardless");
                                await ProcessPayload(operation, payloadParams, args.ApplicationMessage.ResponseTopic ?? null);
                            }
                        }
                        else
                        {
                            Logger.LogError("The payload must contain a 'UserID' identifying who sent the payload");
                        }
                    }
                    else
                    {
                        Logger.LogError("All MQTT trigger messages must contain a payload with a minimum set of properties (UserID and DateTime if the operation has a message expiry configured) and optionally parameters.");
                    }
                }
            });

            bool configuartionErrors = false;
            var clientOptionsBuilder = new MqttClientOptionsBuilder();
            clientOptionsBuilder
                .WithTcpServer(mqttSettings.Hostname, mqttSettings.Port)
                .WithCleanSession(mqttSettings.CleanSession)
                .WithClientId(mqttSettings.ClientID);

            if (mqttSettings.UseTls)
            {
                if (!string.IsNullOrWhiteSpace(mqttSettings.TlsSettings.CAFile))
                {
                    IList<X509Certificate2> certificates = new List<X509Certificate2>();
                    var path = GetFilePath(mqttSettings.TlsSettings.CAFile);
                    var caCert = new X509Certificate2(path);
                    certificates.Add(caCert);
                    // Add the CA certificate to the users CA store
                    using (var caCertStore = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser))
                    {
                        caCertStore.Open(OpenFlags.ReadWrite);
                        caCertStore.Add(caCert);
                    }

                    // If the client certificate is not supplied this means 1-way TLS is being used
                    if (!string.IsNullOrWhiteSpace(mqttSettings.TlsSettings.ClientCert))
                    {
                        var clientCertPath = GetFilePath(mqttSettings.TlsSettings.ClientCert);
                        var clientCert = new X509Certificate2(clientCertPath);
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
                        SslProtocol = mqttSettings.TlsSettings.SslProtocol,
                        AllowUntrustedCertificates = false,
                        IgnoreCertificateChainErrors = false,
                        IgnoreCertificateRevocationErrors = false,
                        CertificateValidationHandler = (MqttClientCertificateValidationCallbackContext context) =>
                        {
                            Logger.LogDebug("Certificate--> issuer: " + context.Certificate.Issuer + " subject: " + context.Certificate.Subject);
                            if (mqttSettings.TlsSettings.VerifyHostname)
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

            if (!string.IsNullOrWhiteSpace(mqttSettings.Username) && !string.IsNullOrWhiteSpace(mqttSettings.Password))
            {
                clientOptionsBuilder.WithCredentials(new MqttClientCredentials()
                {
                    Username = mqttSettings.Username,
                    Password = UTF8Encoding.UTF8.GetBytes(mqttSettings.Password)
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
                        Logger.LogError("Failure to connect to {0} broker", mqttSettings.Hostname);
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

        /// <summary>
        /// Given a client certificate validation context this method will verify that the certificate sent back by the server has the address
        /// used in establishing the connection embedded within it either in a subject alternative names extension on the certificate or if no
        /// SAN extension exists on the certificate the CN in the subject is equal to the connection address.
        /// </summary>
        private bool VerifyConnectionAddress(MqttClientCertificateValidationCallbackContext context, MQTTSettings mqttSettings)
        {
            try
            {
                X509Certificate2 serverCertificate = (X509Certificate2)context.Certificate;
                List<string> SANs = GetSubjectAlternativeNames(serverCertificate);
                if (SANs.Count > 0)
                {
                    // The Subject Alternative Name extension existed so use this for validation
                    foreach (string address in SANs)
                    {
                        try
                        {
                            if (mqttSettings.Hostname.Equals(address, StringComparison.CurrentCultureIgnoreCase))
                            {
                                Logger.LogDebug("The service was configured to connect using an address that matched a SAN entry");
                                return true;
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
                        if (cn.Equals(mqttSettings.Hostname, StringComparison.CurrentCultureIgnoreCase))
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

        /// <summary>
        /// Extracts the value of the CN in a certificate file.
        /// </summary>
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

        /// <summary>
        /// Builds a dictionary of the subject alternative names values that the certificate contains.
        /// If there is no SAN entry it will be an empty dictionary.
        /// </summary>
        private List<string> GetSubjectAlternativeNames(X509Certificate2 certificate)
        {
            List<string> SanEntries = new List<string>();

            List<string> data = new List<string>();
            foreach (X509Extension extension in certificate.Extensions)
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
                    SanEntries.Add(keyValuePair[1]);
                }
                else
                {
                    Logger.LogError("The SAN entry was not formatted correctly, it should be DNS=Value or IP Address=Value, the = was missing");
                }
            }

            return SanEntries;
        }

        /// <summary>
        /// Execute the operation related to the trigger topic for that operation.
        /// This will simply run the PowerShell script configured and optionally respond on the Response Topic with the output.
        /// </summary>
        public async Task ProcessPayload(Operation operation, Dictionary<string, string> payloadParams, string responseTopic = null)
        {
            try
            {
                PowerShell powerShell = PowerShell.Create();
                InitialSessionState initial = InitialSessionState.CreateDefault();
                initial.AuthorizationManager = new AuthorizationManager("Microsoft.PowerShell");
                using (Runspace runspace = RunspaceFactory.CreateRunspace(initial))
                {
                    runspace.Open();
                    runspace.SessionStateProxy.Path.SetLocation(Directory.GetCurrentDirectory());
                    powerShell.Runspace = runspace;
                    var commandPath = GetFilePath(operation.Command);
                    powerShell.AddCommand(commandPath);
                    string contextParam = null;
                    foreach (var param in operation.Parameters)
                    {
                        if (payloadParams.ContainsKey(param.Name))
                        {
                            powerShell.AddParameter(param.Name, payloadParams[param.Name]);
                            if (param.IsResponseContext)
                            {
                                if (!string.IsNullOrWhiteSpace(contextParam))
                                {
                                    Logger.LogWarning($"More than one parameter was configured as IsResponseContext = true on operation {operation.Name}, only the first one will be used.");
                                }
                                else
                                {
                                    contextParam = param.Name;
                                }
                            }
                        }
                        else
                        {
                            Logger.LogDebug($"The parameter {param.Name} was not included in the MQTT message payload so was not included in the operation parameters.");
                        }
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
                                if (!string.IsNullOrWhiteSpace(contextParam))
                                {
                                    responseTopic = responseTopic + "/" + payloadParams[contextParam];
                                }
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
                                Logger.LogInformation($"Script output for operation {operation.Name}: {sb.ToString()}");
                                responseTopic = responseTopic ?? operation.Topics.Response;
                                if (!string.IsNullOrWhiteSpace(responseTopic))
                                {
                                    if (!string.IsNullOrWhiteSpace(contextParam))
                                    {
                                        responseTopic = responseTopic + "/" + payloadParams[contextParam];
                                    }
                                    responseMessage = new MqttApplicationMessage()
                                    {
                                        Payload = UTF8Encoding.UTF8.GetBytes(sb.ToString()),
                                        QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce,
                                        Topic = responseTopic,
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
                                    if (!string.IsNullOrWhiteSpace(contextParam))
                                    {
                                        responseTopic = responseTopic + "/" + payloadParams[contextParam];
                                    }
                                    responseMessage = new MqttApplicationMessage()
                                    {
                                        Payload = UTF8Encoding.UTF8.GetBytes("Executed Successfully"),
                                        QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce,
                                        Topic = responseTopic,
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