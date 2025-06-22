using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Diagnostics;
using MailKit.Net.Smtp;
using MimeKit;
using System.Net;
using System.Text;
using System.Web;
using System.Reactive.Concurrency;
using Microsoft.ApplicationInsights.DataContracts;

/// <summary>
/// Namespace que contiene la lógica de orquestación para el envío masivo de correos electrónicos utilizando Azure Durable Functions.
/// </summary>
namespace EmailOrchestrationApp
{
    /// <summary>
    /// Clase estática que orquesta el flujo de trabajo para el envío masivo de correos electrónicos.
    /// Utiliza Azure Durable Functions para gestionar la orquestación, actividades y monitoreo con Application Insights.
    /// </summary>
    public static class EmailWorkflowOrchestrator
    {
        // Comentado para evitar uso directo de HttpClient, se maneja por instancia en cada método.
        // private static readonly HttpClient _httpClient = new HttpClient();

        /// <summary>
        /// Instancia de configuración que carga variables de entorno para el funcionamiento de la aplicación.
        /// </summary>
        private static readonly IConfiguration _configuration;

        /// <summary>
        /// Cliente de telemetría para registrar eventos y dependencias en Application Insights.
        /// </summary>
        private static readonly TelemetryClient _telemetryClient;

        /// <summary>
        /// Lista de configuraciones de servidores desde donde se obtienen los correos pendientes.
        /// </summary>
        private static readonly List<ServerConfiguration> _serverConfigurations;

        /// <summary>
        /// Constructor estático que inicializa la configuración, el cliente de telemetría y las configuraciones de servidores.
        /// Se ejecuta una sola vez al cargar la clase.
        /// </summary>
        static EmailWorkflowOrchestrator()
        {
            // Construye la configuración a partir de variables de entorno, común en entornos de producción.
            var builder = new ConfigurationBuilder()
                .AddEnvironmentVariables();
            _configuration = builder.Build();

            // Configura el cliente de telemetría con la configuración predeterminada.
            var telemetryConfig = TelemetryConfiguration.CreateDefault();
            _telemetryClient = new TelemetryClient(telemetryConfig);
            _telemetryClient.TrackEvent("EmailWorkflowOrchestratorStarted");

            // Obtiene la configuración de servidores desde una sección JSON en las variables de entorno.
            var serversJson = _configuration.GetSection("Servers").Value;
            Console.WriteLine(serversJson); // Registro para depuración.

            try
            {
                if (!string.IsNullOrEmpty(serversJson))
                {
                    _serverConfigurations = JsonConvert.DeserializeObject<List<ServerConfiguration>>(serversJson);
                }
                else
                {
                    _serverConfigurations = new List<ServerConfiguration>();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deserializing Servers: {ex.Message}");
            }

            // Asegura que _serverConfigurations no sea null.
            if (_serverConfigurations == null) _serverConfigurations = new List<ServerConfiguration>();

            _telemetryClient.TrackEvent("EmailWorkflowOrchestratorCompleted");
        }

        /// <summary>
        /// Función desencadenada por HTTP que inicia la orquestación de envío de correos.
        /// </summary>
        /// <param name="req">Solicitud HTTP que activa la función.</param>
        /// <param name="client">Cliente de Durable Task para gestionar instancias de orquestación.</param>
        /// <param name="executionContext">Contexto de ejecución de la función para logging.</param>
        /// <returns>Respuesta HTTP con el estado de la orquestación.</returns>
        [Function("HttpTriggerFunction")]
        public static async Task<HttpResponseData> HttpTriggerFunction(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req,
            [DurableClient] DurableTaskClient client,
            FunctionContext executionContext)
        {
            var logger = executionContext.GetLogger("HttpTriggerFunction");
            logger.LogInformation($"Iniciando la orquestación mediante HTTP en: {DateTime.Now}");

            // Genera un ID único para la instancia de orquestación.
            string instanceId = "SendEmailOrchestratorInstance";

            // Verifica el estado de la orquestación existente.
            var status = await client.GetInstancesAsync(instanceId, true);
            if (status == null || status.RuntimeStatus == OrchestrationRuntimeStatus.Completed ||
                status.RuntimeStatus == OrchestrationRuntimeStatus.Failed ||
                status.RuntimeStatus == OrchestrationRuntimeStatus.Terminated)
            {
                // Inicia una nueva orquestación si no hay una activa.
                var options = new StartOrchestrationOptions
                {
                    InstanceId = instanceId
                };
                await client.ScheduleNewOrchestrationInstanceAsync(nameof(SendEmailOrchestrator), options);
                logger.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);
            }
            else
            {
                logger.LogInformation("La orquestación ya está en ejecución con ID = '{instanceId}'.", instanceId);
            }

            logger.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);

            return await client.CreateCheckStatusResponseAsync(req, instanceId);
        }

        /// <summary>
        /// Orquestador principal que coordina el envío masivo de correos electrónicos.
        /// </summary>
        /// <param name="context">Contexto de orquestación para gestionar tareas.</param>
        /// <returns>Lista de mensajes indicando el resultado de la orquestación.</returns>
        [Function("SendEmailOrchestrator")]
        public static async Task<List<string>> SendEmailOrchestrator(
            [OrchestrationTrigger] TaskOrchestrationContext context)
        {
            var logger = context.CreateReplaySafeLogger("SendEmailOrchestrator");
            string instanceId = context.InstanceId;
            _telemetryClient.TrackEvent("OrchestratorStarted", new Dictionary<string, string> { { "InstanceId", instanceId } });

            // Obtiene la lista de correos pendientes desde una actividad.
            var emails = await context.CallActivityAsync<List<Email>>(nameof(GetEmailsToSend), null);

            if (emails == null || !emails.Any() || emails.All(e => e.ToEmail == null))
            {
                logger.LogInformation("No hay correos electrónicos para enviar.");
                return new List<string> { "No hay correos electrónicos para enviar" };
            }

            var stopwatch = Stopwatch.StartNew();
            // Agrupa correos por cliente y limita el paralelismo.
            var distinctClients = emails.GroupBy(e => e.SystemSerial).Select(g => g.Key);
            int maxParallelClients = int.Parse(_configuration["maxParallelClients"] ?? "5"); // Límite configurable
            stopwatch.Stop();
            _telemetryClient.TrackDependency("GetEmailsToSend", "QtyEmailsToProcess", emails.GroupBy(e => e.SystemSerial).Take(maxParallelClients).Count().ToString(), DateTime.UtcNow, stopwatch.Elapsed, true);

            // Lanza suborquestaciones en paralelo para cada cliente.
            var parallelTasks = distinctClients.Take(maxParallelClients).Select(client =>
                context.CallSubOrchestratorAsync(nameof(SendEmailsToClientOrchestrator), new SendEmailsToClientInput
                {
                    ClientName = client,
                    Emails = emails,
                    InstanceId = instanceId
                })).ToList();

            await Task.WhenAll(parallelTasks);

            _telemetryClient.TrackEvent("OrchestratorCompleted");
            return new List<string> { "Orquestación completada" };
        }

        /// <summary>
        /// Suborquestador que maneja el envío de correos para un cliente específico.
        /// </summary>
        /// <param name="context">Contexto de orquestación para gestionar tareas.</param>
        [Function("SendEmailsToClientOrchestrator")]
        public static async Task SendEmailsToClientOrchestrator(
            [OrchestrationTrigger] TaskOrchestrationContext context)
        {
            var input = context.GetInput<SendEmailsToClientInput>();
            string clientName = input.ClientName;
            List<Email> emails = input.Emails;
            string instanceId = input.InstanceId;

            var logger = context.CreateReplaySafeLogger("SendEmailsToClientOrchestrator");
            _telemetryClient.TrackEvent("ClientOrchestratorStarted", new Dictionary<string, string>
            {
                { "ClientName", clientName },
                { "InstanceId", instanceId }
            });

            // Filtra correos para el cliente y limita la cantidad.
            int qtyEmails = int.Parse(_configuration["QtyEmails"] ?? "5");
            var clientEmails = emails.Where(e => e.SystemSerial == clientName).Take(qtyEmails).ToList();

            if (clientEmails == null || !clientEmails.Any())
            {
                logger.LogInformation($"No hay correos para el cliente: {clientName}");
                return;
            }

            // Elimina duplicados basados en el ID del correo.
            clientEmails = clientEmails.GroupBy(e => e.ID).Select(g => g.First()).ToList();

            int batchSize = int.Parse(_configuration["batchSize"] ?? "5"); // Tamaño del lote configurable
            for (int i = 0; i < clientEmails.Count; i += batchSize)
            {
                var batch = clientEmails.Skip(i).Take(batchSize).ToList();
                var sendEmailTasks = batch.Select(email => context.CallActivityAsync(nameof(SendEmailHandler), new SendEmailHandlerInput
                {
                    Email = email,
                    InstanceId = instanceId
                })).ToList();
                await Task.WhenAll(sendEmailTasks); // Espera la finalización del lote.
            }

            _telemetryClient.TrackEvent("ClientOrchestratorCompleted", new Dictionary<string, string>
            {
                { "ClientName", clientName },
                { "InstanceId", instanceId }
            });
        }

        /// <summary>
        /// Actividad que obtiene la lista de correos electrónicos pendientes desde múltiples servidores.
        /// </summary>
        /// <param name="instanceId">Identificador único de la instancia de orquestación.</param>
        /// <param name="executionContext">Contexto de ejecución para logging.</param>
        /// <returns>Lista de objetos Email con los correos pendientes.</returns>
        [Function("GetEmailsToSend")]
        public static async Task<List<Email>> GetEmailsToSend([ActivityTrigger] string instanceId, FunctionContext executionContext)
        {
            var logger = executionContext.GetLogger("GetEmailsToSend");
            logger.LogInformation("Obteniendo la lista de correos pendientes.");
            var allEmails = new List<Email>();
            var stopwatch = Stopwatch.StartNew();
            var emails = new List<Email>();

            foreach (var serverConfig in _serverConfigurations)
            {
                emails = new List<Email>();
                string getEmailsToSendUrl = serverConfig.GetEmailsToSendEndpoint;
                string subDomain = serverConfig.GetSubDomain;
                string url = $"{getEmailsToSendUrl}&SubDomain={subDomain}";

                stopwatch = Stopwatch.StartNew();
                using (var httpClient = new HttpClient())
                {
                    var response = await httpClient.PostAsync(url, null);
                    stopwatch.Stop();

                    var dependency = new DependencyTelemetry
                    {
                        Type = "HTTP",
                        Name = "GetEmailsToSend",
                        Target = url,
                        Data = url,
                        Timestamp = DateTimeOffset.UtcNow - stopwatch.Elapsed,
                        Duration = stopwatch.Elapsed,
                        Success = response.IsSuccessStatusCode,
                        ResultCode = ((int)response.StatusCode).ToString()
                    };

                    dependency.Properties.Add("InstanceId", instanceId);
                    _telemetryClient.TrackDependency(dependency);

                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        var emailData = JsonConvert.DeserializeObject<List<Dictionary<string, List<Email>>>>(content);

                        foreach (var dictionary in emailData)
                        {
                            foreach (var kvp in dictionary)
                            {
                                emails.AddRange(kvp.Value);
                            }
                        }
                        if (emails != null && emails.Count > 0)
                            allEmails.AddRange(emails.Where(e => e.ToEmail != null && !string.IsNullOrEmpty(e.ToEmail)).ToList());
                    }
                    else
                    {
                        logger.LogError($"Error al obtener correos del servidor {subDomain}");
                    }

                    var emailIds = emails.Select(e => e.ID).ToHashSet();
                    allEmails.Where(email => emailIds.Contains(email.ID))
                            .ToList()
                            .ForEach(email =>
                            {
                                email.GetEmailsToSendEndpoint = serverConfig.GetEmailsToSendEndpoint;
                                email.UpdateEmailSendStatusEndpoint = serverConfig.UpdateEmailSendStatusEndpoint;
                                email.GetEmailBodyEndpoint = serverConfig.GetEmailBodyEndpoint;
                                email.GetSubDomain = serverConfig.GetSubDomain;
                            });

                    allEmails = allEmails.GroupBy(e => e.ID).Select(g => g.First()).ToList();
                    _telemetryClient.TrackEvent("QtyEmailsObtained", new Dictionary<string, string>
                    {
                        { "Count", allEmails.Count.ToString() },
                        { "InstanceId", instanceId }
                    });
                }
            }

            stopwatch = Stopwatch.StartNew();
            stopwatch.Stop();
            _telemetryClient.TrackDependency("GetEmailsToSend", "QtyEmailsObtained", allEmails.Count().ToString(), DateTime.UtcNow, stopwatch.Elapsed, true);
            return allEmails;
        }

        /// <summary>
        /// Actividad que procesa y envía un correo electrónico individual.
        /// </summary>
        /// <param name="input">Datos de entrada que incluyen el correo y el ID de instancia.</param>
        /// <param name="executionContext">Contexto de ejecución para logging.</param>
        [Function("SendEmailHandler")]
        public static async Task SendEmailHandler([ActivityTrigger] SendEmailHandlerInput input, FunctionContext executionContext)
        {
            var logger = executionContext.GetLogger("SendEmailHandler");
            var email = input.Email;
            string instanceId = input.InstanceId ?? executionContext.InvocationId; // Fallback a InvocationId si no hay InstanceId
            logger.LogInformation($"Procesando envío de correo para: {email.ToEmail}");

            if (email == null)
            {
                logger.LogWarning("El correo electrónico es nulo. Se omitirá el envío.");
                return;
            }

            SendEmailResponse emailSent;
            string decodedMessage;
            var stopwatch = new Stopwatch();

            try
            {
                stopwatch.Start();
                string emailBodyUrlWithParams = $"{email.GetEmailBodyEndpoint}&Serial={email.SystemSerial}&IDEmail={email.ID}&SubDomain={email.GetSubDomain}";
                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("Accept-Charset", "utf-8");
                    var emailBodyResponse = await httpClient.PostAsync(emailBodyUrlWithParams, null);
                    stopwatch.Stop();

                    var dependency = new DependencyTelemetry
                    {
                        Type = "HTTP",
                        Name = "GetEmailBody",
                        Data = emailBodyUrlWithParams,
                        Target = new Uri(emailBodyUrlWithParams).Host,
                        Timestamp = DateTimeOffset.UtcNow - stopwatch.Elapsed,
                        Duration = stopwatch.Elapsed,
                        Success = emailBodyResponse.IsSuccessStatusCode,
                        ResultCode = ((int)emailBodyResponse.StatusCode).ToString()
                    };

                    dependency.Properties.Add("EmailID", email.ID);
                    dependency.Properties.Add("InstanceId", instanceId);
                    _telemetryClient.TrackDependency(dependency);

                    emailBodyResponse.EnsureSuccessStatusCode();
                    var emailBodyContent = await emailBodyResponse.Content.ReadAsStringAsync();
                    var emailBody = JsonConvert.DeserializeObject<EmailBodyResponse>(emailBodyContent);
                    decodedMessage = URLDecode(emailBody.Message);

                    if (!email.IsApiCall())
                    {
                        stopwatch.Restart();
                        emailSent = await SendEmailAsync(email, decodedMessage, logger, instanceId);
                        stopwatch.Stop();
                    }
                    else
                    {
                        stopwatch.Restart();
                        emailSent = await SendBrevoEmailAsync(email, decodedMessage, logger, instanceId);
                        stopwatch.Stop();
                    }

                    var sendDependency = new DependencyTelemetry
                    {
                        Type = email.IsApiCall() ? "BrevoAPI" : "SMTP",
                        Name = "EmailSend",
                        Target = email.ToEmail,
                        Data = email.ToEmail,
                        Timestamp = DateTimeOffset.UtcNow - stopwatch.Elapsed,
                        Duration = stopwatch.Elapsed,
                        Success = !emailSent.Error,
                        ResultCode = emailSent.Error ? "Error" : "Success"
                    };
                    sendDependency.Properties.Add("EmailID", email.ID);
                    sendDependency.Properties.Add("InstanceId", instanceId);
                    _telemetryClient.TrackDependency(sendDependency);

                    await ReportEmailStatusAsync(email, emailSent, logger, instanceId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Error procesando el envío de correo a {email.ToEmail}: {ex.Message}");
                var exceptionTelemetry = new ExceptionTelemetry(ex)
                {
                    SeverityLevel = SeverityLevel.Error,
                    Timestamp = DateTimeOffset.UtcNow
                };
                exceptionTelemetry.Properties.Add("EmailID", email.ID);
                exceptionTelemetry.Properties.Add("ToEmail", email.ToEmail);
                exceptionTelemetry.Properties.Add("InstanceId", instanceId);
                exceptionTelemetry.Properties.Add("Function", "SendEmailHandler");
                _telemetryClient.TrackException(exceptionTelemetry);
            }
        }

        /// <summary>
        /// Decodifica una cadena URL-encoded a su forma legible.
        /// </summary>
        /// <param name="str">Cadena codificada a decodificar.</param>
        /// <returns>Cadena decodificada con reemplazo de "3E" por ">".</returns>
        public static string URLDecode(string str)
        {
            if (!string.IsNullOrEmpty(str))
            {
                StringBuilder result = new StringBuilder();
                for (int i = 0; i < str.Length; i++)
                {
                    char currentChar = str[i];
                    if (currentChar == '+')
                    {
                        result.Append(' ');
                    }
                    else if (currentChar == '%' && i + 2 < str.Length)
                    {
                        string hexValue = str.Substring(i + 1, 2);
                        if (int.TryParse(hexValue, System.Globalization.NumberStyles.HexNumber, null, out int decodedChar))
                        {
                            result.Append((char)decodedChar);
                            i += 2;
                        }
                    }
                    else
                    {
                        result.Append(currentChar);
                    }
                }
                return result.ToString().Replace("3E", ">");
            }
            return str;
        }

        /// <summary>
        /// Envía un correo electrónico utilizando un cliente SMTP.
        /// </summary>
        /// <param name="email">Objeto Email con los detalles del mensaje.</param>
        /// <param name="emailBody">Cuerpo del correo a enviar.</param>
        /// <param name="log">Instancia de logger para registrar eventos.</param>
        /// <param name="instanceId">Identificador de la instancia de orquestación.</param>
        /// <returns>Respuesta con el estado del envío.</returns>
        private static async Task<SendEmailResponse> SendEmailAsync(Email email, string emailBody, ILogger log, string instanceId)
        {
            SmtpClient smtpClient = new SmtpClient
            {
                Timeout = int.Parse(_configuration["timeoutSmtp"] ?? "5000")
            };
            SendEmailResponse response = new SendEmailResponse { Error = false, ErrorMessage = string.Empty };

            try
            {
                _telemetryClient.TrackEvent("SendEmail_Start", new Dictionary<string, string>
                {
                    { "EmailID", email.ID },
                    { "ToEmail", email.ToEmail },
                    { "FromEmail", email.FromEmail },
                    { "Subject", email.Subject },
                    { "InstanceId", instanceId }
                });

                var message = new MimeMessage();
                message.From.Add(new MailboxAddress(email.FromName, email.FromEmail));
                message.To.Add(new MailboxAddress("", email.ToEmail));
                message.Subject = email.Subject;
                message.ReplyTo.Add(new MailboxAddress(email.FromName, email.ReplyToEmail));
                var bodyBuilder = new BodyBuilder { HtmlBody = emailBody };
                message.Body = bodyBuilder.ToMessageBody();

                if (email.eSSLConnection == "1" && email.eStartTLS == "1")
                {
                    await smtpClient.ConnectAsync(email.eMailServer, int.Parse(email.eMailServerPort), MailKit.Security.SecureSocketOptions.StartTls);
                    _telemetryClient.TrackEvent("SMTP_Connected_With_StartTLS", new Dictionary<string, string>
                    {
                        { "Server", email.eMailServer },
                        { "Port", email.eMailServerPort },
                        { "InstanceId", instanceId }
                    });
                }
                else if (email.eSSLConnection == "1" && email.eStartTLS == "0")
                {
                    await smtpClient.ConnectAsync(email.eMailServer, int.Parse(email.eMailServerPort), MailKit.Security.SecureSocketOptions.SslOnConnect);
                    _telemetryClient.TrackEvent("SMTP_Connected_With_SSL", new Dictionary<string, string>
                    {
                        { "Server", email.eMailServer },
                        { "Port", email.eMailServerPort },
                        { "InstanceId", instanceId }
                    });
                }
                else
                {
                    await smtpClient.ConnectAsync(email.eMailServer, int.Parse(email.eMailServerPort), MailKit.Security.SecureSocketOptions.None);
                    _telemetryClient.TrackEvent("SMTP_Connected_Without_SSL", new Dictionary<string, string>
                    {
                        { "Server", email.eMailServer },
                        { "Port", email.eMailServerPort },
                        { "InstanceId", instanceId }
                    });
                }

                smtpClient.Authenticate(email.eMailServerUsername, email.eMailServerPassword);
                _telemetryClient.TrackEvent("SMTP_Authenticated", new Dictionary<string, string>
                {
                    { "Username", email.eMailServerUsername },
                    { "InstanceId", instanceId }
                });

                int retryCount = 3;
                for (int i = 0; i < retryCount; i++)
                {
                    try
                    {
                        await Task.Delay(int.Parse(_configuration["EmailSendDelayMilliseconds"] ?? "5000"));
                        await smtpClient.SendAsync(message);
                        log.LogInformation($"Correo enviado a {email.ToEmail}");
                        _telemetryClient.TrackEvent("Email_Sent", new Dictionary<string, string>
                        {
                            { "ToEmail", email.ToEmail },
                            { "Attempt", (i + 1).ToString() },
                            { "EmailID", email.ID },
                            { "InstanceId", instanceId }
                        });
                        break;
                    }
                    catch (Exception ex) when (i < retryCount - 1)
                    {
                        log.LogWarning($"Error en el intento {i + 1} de enviar correo a {email.ToEmail}: {ex.Message}");
                        _telemetryClient.TrackException(ex, new Dictionary<string, string>
                        {
                            { "ToEmail", email.ToEmail },
                            { "Attempt", (i + 1).ToString() },
                            { "EmailID", email.ID },
                            { "InstanceId", instanceId }
                        });
                        await Task.Delay(int.Parse(_configuration["delaySmtpException"] ?? "2000"));
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogError($"Error enviando correo a {email.ToEmail}: {ex.Message}");
                response.Error = true;
                response.ErrorMessage = ex.Message;
                var exceptionTelemetry = new ExceptionTelemetry(ex)
                {
                    SeverityLevel = SeverityLevel.Error,
                    Timestamp = DateTimeOffset.UtcNow
                };
                exceptionTelemetry.Properties.Add("EmailID", email.ID);
                exceptionTelemetry.Properties.Add("ToEmail", email.ToEmail);
                exceptionTelemetry.Properties.Add("FromEmail", email.FromEmail);
                exceptionTelemetry.Properties.Add("Subject", email.Subject);
                exceptionTelemetry.Properties.Add("InstanceId", instanceId);
                exceptionTelemetry.Properties.Add("Function", "SendEmailAsync");
                _telemetryClient.TrackException(exceptionTelemetry);
            }
            finally
            {
                if (smtpClient.IsConnected)
                {
                    await smtpClient.DisconnectAsync(true);
                }
                smtpClient.Dispose();
                _telemetryClient.TrackEvent("SendEmail_Completed", new Dictionary<string, string>
                {
                    { "ToEmail", email.ToEmail },
                    { "Status", response.Error ? "Failed" : "Success" },
                    { "ErrorMessage", response.ErrorMessage },
                    { "EmailID", email.ID },
                    { "InstanceId", instanceId }
                });
            }

            return response;
        }

        /// <summary>
        /// Envía un correo electrónico utilizando la API de Brevo.
        /// </summary>
        /// <param name="email">Objeto Email con los detalles del mensaje.</param>
        /// <param name="emailBody">Cuerpo del correo a enviar.</param>
        /// <param name="log">Instancia de logger para registrar eventos.</param>
        /// <param name="instanceId">Identificador de la instancia de orquestación.</param>
        /// <returns>Respuesta con el estado del envío.</returns>
        private static async Task<SendEmailResponse> SendBrevoEmailAsync(Email email, string emailBody, ILogger log, string instanceId)
        {
            var response = new SendEmailResponse { Error = false, ErrorMessage = string.Empty };
            try
            {
                var emailPayload = new
                {
                    sender = new { email = email.FromEmail, name = email.FromName },
                    to = new List<object> { new { email = email.ToEmail } },
                    subject = email.Subject,
                    htmlContent = emailBody,
                    replyTo = new { email = email.ReplyToEmail, name = email.FromName }
                };

                var jsonPayload = JsonConvert.SerializeObject(emailPayload);
                var requestContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                using (var _httpClient = new HttpClient())
                {
                    if (_httpClient.DefaultRequestHeaders.Contains("api-key"))
                    {
                        _httpClient.DefaultRequestHeaders.Remove("api-key");
                    }
                    _httpClient.DefaultRequestHeaders.Add("api-key", _configuration["BrevoAPIKey"]);
                    await Task.Delay(int.Parse(_configuration["EmailSendDelayMilliseconds"] ?? "5000"));
                    var result = await _httpClient.PostAsync(_configuration["BrevoApiUrl"], requestContent);

                    if (result.IsSuccessStatusCode)
                    {
                        log.LogInformation($"Correo enviado exitosamente a {email.ToEmail}");
                        _telemetryClient.TrackEvent("Email_Sent", new Dictionary<string, string>
                        {
                            { "ToEmail", email.ToEmail },
                            { "EmailID", email.ID },
                            { "InstanceId", instanceId }
                        });
                    }
                    else
                    {
                        var resultContent = await result.Content.ReadAsStringAsync();
                        log.LogError($"Error enviando correo con Brevo: {resultContent}");
                        response.Error = true;
                        response.ErrorMessage = resultContent;
                        _telemetryClient.TrackEvent("Email_Send_Failed", new Dictionary<string, string>
                        {
                            { "ToEmail", email.ToEmail },
                            { "ErrorMessage", resultContent },
                            { "EmailID", email.ID },
                            { "InstanceId", instanceId }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, $"Excepción al enviar correo con Brevo: {ex.Message}");
                response.Error = true;
                response.ErrorMessage = ex.Message;
                var exceptionTelemetry = new ExceptionTelemetry(ex)
                {
                    SeverityLevel = SeverityLevel.Error,
                    Timestamp = DateTimeOffset.UtcNow
                };
                exceptionTelemetry.Properties.Add("EmailID", email.ID);
                exceptionTelemetry.Properties.Add("ToEmail", email.ToEmail);
                exceptionTelemetry.Properties.Add("InstanceId", instanceId);
                exceptionTelemetry.Properties.Add("Function", "SendBrevoEmailAsync");
                _telemetryClient.TrackException(exceptionTelemetry);
            }
            return response;
        }

        /// <summary>
        /// Reporta el estado del envío de un correo a un endpoint externo.
        /// </summary>
        /// <param name="email">Objeto Email con los detalles del mensaje.</param>
        /// <param name="emailSent">Respuesta del envío para determinar el estado.</param>
        /// <param name="log">Instancia de logger para registrar eventos.</param>
        /// <param name="instanceId">Identificador de la instancia de orquestación.</param>
        private static async Task ReportEmailStatusAsync(Email email, SendEmailResponse emailSent, ILogger log, string instanceId)
        {
            try
            {
                string updateEmailStatusUrl = email.UpdateEmailSendStatusEndpoint;
                string url = $"{updateEmailStatusUrl}&Serial={email.SystemSerial}&IDEmail={email.ID}&SentEmail={(!emailSent.Error ? 1 : 0)}&SubDomain={email.GetSubDomain}";

                var stopwatch = Stopwatch.StartNew();
                using (var _httpClient = new HttpClient())
                {
                    var boundary = "0";
                    var formData = new MultipartFormDataContent(boundary);
                    formData.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("multipart/form-data");
                    formData.Headers.ContentType.Parameters.Add(new System.Net.Http.Headers.NameValueHeaderValue("boundary", boundary));

                    var stringContent = new StringContent(emailSent.ErrorMessage, Encoding.UTF8);
                    stringContent.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
                    {
                        Name = "\"ErrorMessage\""
                    };
                    formData.Add(stringContent);

                    var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = formData
                    };
                    request.Headers.ExpectContinue = false;

                    var response = await _httpClient.SendAsync(request);
                    var responseContent = await response.Content.ReadAsStringAsync();
                    stopwatch.Stop();

                    var dependency = new DependencyTelemetry
                    {
                        Type = "HTTP",
                        Name = "UpdateEmailStatus",
                        Data = url,
                        Target = new Uri(url).Host,
                        Timestamp = DateTimeOffset.UtcNow - stopwatch.Elapsed,
                        Duration = stopwatch.Elapsed,
                        Success = response.IsSuccessStatusCode,
                        ResultCode = ((int)response.StatusCode).ToString()
                    };
                    dependency.Properties.Add("EmailID", email.ID);
                    dependency.Properties.Add("InstanceId", instanceId);
                    dependency.Properties.Add("ResponseBackend", responseContent);
                    Debug.WriteLine(responseContent);
                    _telemetryClient.TrackDependency(dependency);

                    response.EnsureSuccessStatusCode();
                    var content = await response.Content.ReadAsStringAsync();
                    var emailStatus = JsonConvert.DeserializeObject<EmailStatusResponse>(content);
                    if (emailStatus != null)
                    {
                        log.LogInformation($"Estado del correo enviado: ID: {emailStatus.ID}, Serial: {emailStatus.Serial}, Enviado: {emailStatus.Sent}, Mensaje de Error: {emailStatus.ErrorMessage}");
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, $"Error reportando el estado del envío de correo ID {email.ID} a {email.ToEmail}: {ex.Message}");
                var exceptionTelemetry = new ExceptionTelemetry(ex)
                {
                    SeverityLevel = SeverityLevel.Error,
                    Timestamp = DateTimeOffset.UtcNow
                };
                exceptionTelemetry.Properties.Add("EmailID", email.ID);
                exceptionTelemetry.Properties.Add("ToEmail", email.ToEmail);
                exceptionTelemetry.Properties.Add("InstanceId", instanceId);
                exceptionTelemetry.Properties.Add("Function", "ReportEmailStatusAsync");
                _telemetryClient.TrackException(exceptionTelemetry);
            }
        }
    }

    /// <summary>
    /// Clase que representa un correo electrónico con sus propiedades relevantes.
    /// </summary>
    public class Email
    {
        /// <summary>
        /// Identificador único del correo.
        /// </summary>
        public string ID { get; set; }

        /// <summary>
        /// Número de serie del sistema asociado al correo.
        /// </summary>
        public string SystemSerial { get; set; }

        /// <summary>
        /// Nombre del remitente.
        /// </summary>
        public string FromName { get; set; }

        /// <summary>
        /// Dirección de correo del remitente.
        /// </summary>
        public string FromEmail { get; set; }

        /// <summary>
        /// Dirección de correo para respuestas.
        /// </summary>
        public string ReplyToEmail { get; set; }

        /// <summary>
        /// Asunto del correo.
        /// </summary>
        public string Subject { get; set; }

        /// <summary>
        /// Servidor de correo SMTP.
        /// </summary>
        public string eMailServer { get; set; }

        /// <summary>
        /// Nombre de usuario para autenticación en el servidor SMTP.
        /// </summary>
        public string eMailServerUsername { get; set; }

        /// <summary>
        /// Contraseña para autenticación en el servidor SMTP.
        /// </summary>
        public string eMailServerPassword { get; set; }

        /// <summary>
        /// Puerto del servidor SMTP.
        /// </summary>
        public string eMailServerPort { get; set; }

        /// <summary>
        /// Indicador de conexión SSL (1 para activado, 0 para desactivado).
        /// </summary>
        public string eSSLConnection { get; set; }

        /// <summary>
        /// Indicador de uso de StartTLS (1 para activado, 0 para desactivado).
        /// </summary>
        public string eStartTLS { get; set; }

        /// <summary>
        /// Dirección de correo del destinatario.
        /// </summary>
        public string ToEmail { get; set; }

        /// <summary>
        /// Indicador de uso de API en lugar de SMTP (1 para API, 0 para SMTP).
        /// </summary>
        public string ApiCall { get; set; }

        /// <summary>
        /// Endpoint para obtener la lista de correos pendientes.
        /// </summary>
        public string GetEmailsToSendEndpoint { get; set; }

        /// <summary>
        /// Endpoint para obtener el cuerpo del correo.
        /// </summary>
        public string GetEmailBodyEndpoint { get; set; }

        /// <summary>
        /// Endpoint para actualizar el estado del envío.
        /// </summary>
        public string UpdateEmailSendStatusEndpoint { get; set; }

        /// <summary>
        /// Subdominio asociado al servidor.
        /// </summary>
        public string GetSubDomain { get; set; }

        /// <summary>
        /// Determina si el correo debe enviarse mediante API en lugar de SMTP.
        /// </summary>
        /// <returns>True si ApiCall es "1", false de lo contrario.</returns>
        public bool IsApiCall()
        {
            return ApiCall == "1";
        }
    }

    /// <summary>
    /// Clase que agrupa una lista de correos electrónicos por cliente.
    /// </summary>
    public class ClientEmails
    {
        /// <summary>
        /// Lista de correos electrónicos asociados a un cliente.
        /// </summary>
        public List<Email> Emails { get; set; }
    }

    /// <summary>
    /// Clase que representa la respuesta con el cuerpo de un correo electrónico.
    /// </summary>
    public class EmailBodyResponse
    {
        /// <summary>
        /// Identificador único del correo.
        /// </summary>
        public string ID { get; set; }

        /// <summary>
        /// Número de serie asociado al correo.
        /// </summary>
        public string Serial { get; set; }

        /// <summary>
        /// Contenido del cuerpo del correo.
        /// </summary>
        public string Message { get; set; }
    }

    /// <summary>
    /// Clase que representa la respuesta al envío de un correo electrónico.
    /// </summary>
    public class SendEmailResponse
    {
        /// <summary>
        /// Indica si ocurrió un error durante el envío.
        /// </summary>
        public bool Error { get; set; }

        /// <summary>
        /// Mensaje de error si el envío falló.
        /// </summary>
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Clase que representa el estado actualizado de un correo electrónico.
    /// </summary>
    public class EmailStatusResponse
    {
        /// <summary>
        /// Identificador único del correo.
        /// </summary>
        public string ID { get; set; }

        /// <summary>
        /// Número de serie asociado al correo.
        /// </summary>
        public string Serial { get; set; }

        /// <summary>
        /// Indicador de envío exitoso (1 para enviado, 0 para fallido).
        /// </summary>
        public int Sent { get; set; }

        /// <summary>
        /// Mensaje de error si el envío falló.
        /// </summary>
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Clase que contiene los datos de entrada para la suborquestación por cliente.
    /// </summary>
    public class SendEmailsToClientInput
    {
        /// <summary>
        /// Nombre o identificador del cliente.
        /// </summary>
        public string ClientName { get; set; }

        /// <summary>
        /// Lista de correos electrónicos a procesar.
        /// </summary>
        public List<Email> Emails { get; set; }

        /// <summary>
        /// Identificador único de la instancia de orquestación.
        /// </summary>
        public string InstanceId { get; set; }
    }

    /// <summary>
    /// Clase que contiene los datos de entrada para el manejador de envío de correos.
    /// </summary>
    public class SendEmailHandlerInput
    {
        /// <summary>
        /// Objeto Email con los detalles del mensaje a enviar.
        /// </summary>
        public Email Email { get; set; }

        /// <summary>
        /// Identificador único de la instancia de orquestación.
        /// </summary>
        public string InstanceId { get; set; }
    }

    /// <summary>
    /// Clase que representa la configuración de un servidor externo.
    /// </summary>
    public class ServerConfiguration
    {
        /// <summary>
        /// Endpoint para obtener la lista de correos pendientes.
        /// </summary>
        public string GetEmailsToSendEndpoint { get; set; }

        /// <summary>
        /// Endpoint para obtener el cuerpo del correo.
        /// </summary>
        public string GetEmailBodyEndpoint { get; set; }

        /// <summary>
        /// Endpoint para actualizar el estado del envío.
        /// </summary>
        public string UpdateEmailSendStatusEndpoint { get; set; }

        /// <summary>
        /// Subdominio asociado al servidor.
        /// </summary>
        public string GetSubDomain { get; set; }
    }
}