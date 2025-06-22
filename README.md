# AzureDurableEmailOrchestration

Este repositorio contiene una implementación de Azure Durable Functions diseñada para optimizar el envío masivo de correos electrónicos en un entorno serverless. La solución aborda problemas como colas de envío, alto consumo de CPU y escalabilidad en servidores tradicionales, ofreciendo un enfoque moderno con paralelismo y monitoreo.

## Características
- Orquestación de envíos masivos de correos usando Azure Durable Functions.
- Soporte para envío paralelo por cliente y lotes configurables.
- Integración con SMTP y APIs externas (ej. Brevo).
- Monitoreo con Application Insights.
- Escalabilidad sin necesidad de servidores físicos.

## Requisitos
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Azure Functions Core Tools](https://docs.microsoft.com/en-us/azure/azure-functions/functions-run-local)
- [Visual Studio Code](https://code.visualstudio.com/) con extensiones recomendadas:
  - C# for Visual Studio Code
  - Azure Functions

## Instalación
1. Clona el repositorio:
   ```bash
   git clone https://github.com/freddan58/AzureDurableEmailOrchestration.git
   cd AzureDurableEmailOrchestration

2. Restaura las dependencias:
   ```bash
   dotnet restore

3. Configuración para Pruebas Locales
  Antes de probar el proyecto, configura los siguientes archivos y variables:
  local.settings.json: Añade o edita este archivo en la raíz del proyecto con valores de prueba. 
  Ejemplo:
   ```json
    {
        "IsEncrypted": false,
        "Values": {
            "AzureWebJobsStorage": "UseDevelopmentStorage=true",
            "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
            "APPINSIGHTS_INSTRUMENTATIONKEY": "fake-instrumentation-key-1234567890",
            "APPLICATIONINSIGHTS_CONNECTION_STRING": "InstrumentationKey=fake-instrumentation-key-1234567890;IngestionEndpoint=https://testregion-1.in.applicationinsights.azure.com/;LiveEndpoint=https://testregion.livediagnostics.monitor.azure.com/;ApplicationId=fake-app-id-123456",
            "EmailSendDelayMilliseconds": "2000",
            "QtyEmails": "1",
            "BrevoAPIKey": "fake-brevo-api-key-1234567890abcdef",
            "BrevoApiUrl": "https://api.testbrevo.com/v3/smtp/email",
            "maxParallelClients": "1",
            "timeoutSmtp": "5000",
            "delaySmtpException": "2000"
        }
    }

- Nota: Usa el emulador de almacenamiento local (UseDevelopmentStorage=true) y claves ficticias. No subas este archivo a GitHub; agrégalo a .gitignore.
servers.json: Crea o edita este archivo en la raíz con endpoints de prueba. Ejemplo:
  ```json
  {
    "Servers": [
      {
        "GetEmailsToSendEndpoint": "https://api.testserver.com/email/v1/get-emails?limit=10",
        "GetEmailBodyEndpoint": "https://api.testserver.com/email/v1/get-body",
        "UpdateEmailSendStatusEndpoint": "https://api.testserver.com/email/v1/update-status",
        "GetSubDomain": "TestDomainX"
      }
    ]
  }

- Nota: Estas URLs son ficticias y generarán errores en pruebas reales. Reemplázalas con endpoints funcionales para simulaciones.
  launchSettings.json: Asegúrate de que el perfil EmailOrchestrationApp esté configurado con las variables de entorno anteriores. Ejemplo:

  ```json
  {
    "profiles": {
      "EmailOrchestrationApp": {
        "commandName": "Project",
        "commandLineArgs": "--port 7228",
        "environmentVariables": {
          "Servers": "[{\"GetEmailsToSendEndpoint\": \"https://api.testserver.com/email/v1/get-emails?limit=10\", \"GetEmailBodyEndpoint\": \"https://api.testserver.com/email/v1/get-body\", \"UpdateEmailSendStatusEndpoint\": \"https://api.testserver.com/email/v1/update-status\", \"GetSubDomain\": \"TestDomainX\"}]",
          "BrevoApiUrl": "https://api.testbrevo.com/v3/smtp/email",
          "BrevoAPIKey": "fake-brevo-api-key-1234567890abcdef",
          "QtyEmails": "1",
          "EmailSendDelayMilliseconds": "2000",
          "APPLICATIONINSIGHTS_CONNECTION_STRING": "InstrumentationKey=fake-instrumentation-key-1234567890;IngestionEndpoint=https://testregion-1.in.applicationinsights.azure.com/;LiveEndpoint=https://testregion.livediagnostics.monitor.azure.com/;ApplicationId=fake-app-id-123456",
          "APPINSIGHTS_INSTRUMENTATIONKEY": "fake-instrumentation-key-1234567890",
          "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
          "AzureWebJobsStorage": "UseDevelopmentStorage=true",
          "functionTimeout": "00:10:00",
          "maxParallelClients": "1",
          "timeoutSmtp": "5000",
          "delaySmtpException": "2000"
        }
      }
    }
  }

- Nota: Ajusta las variables según las necesidades de prueba.
  Prueba Local: Ejecuta la función con:

  ```bash
  func start

  o usa F5 en VS Code. Verifica que la función se inicie en http://localhost:7228 y que los logs muestren el procesamiento (los endpoints ficticios generarán errores esperados).

## Configuración para Despliegue
- Para desplegar el proyecto en Azure, sigue estos pasos:

    ### Crea Recursos en Azure:
    - Crea un Grupo de Recursos (ej. rg-test-email-app).
    - Configura una Cuenta de Almacenamiento para AzureWebJobsStorage con una cadena de conexión válida.
    - Configura una instancia de Application Insights y obtén la clave de instrumentación y cadena de conexión.
    - Crea una Aplicación de Función Azure con el nombre deseado (ej. test-email-orchestration-app).
    - Configura Variables de Entorno:
    - En la configuración de la aplicación de función en Azure, establece:
        - AzureWebJobsStorage: Cadena de conexión de la cuenta de almacenamiento.
        - FUNCTIONS_WORKER_RUNTIME: dotnet-isolated.
        - APPINSIGHTS_INSTRUMENTATIONKEY: Clave de Application Insights.
        - APPLICATIONINSIGHTS_CONNECTION_STRING: Cadena de conexión de Application Insights.
        - EmailSendDelayMilliseconds: 2000 (o valor deseado).
        - QtyEmails: 15 (o valor deseado).
        - BrevoAPIKey: Clave API de Brevo válida.
        - BrevoApiUrl: https://api.brevo.com/v3/smtp/email (o endpoint real).
        - Servers: JSON con endpoints reales (ejemplo en la sección de pruebas locales, ajustado con URLs funcionales).
        - AZURE_FUNCTIONS_ENVIRONMENT: Production.
        - maxParallelClients: 5 (o valor deseado).
        - batchSize: 15 (o valor deseado).
        - timeoutSmtp: 5000 (o valor deseado).
        - delaySmtpException: 2000 (o valor deseado).
    ### Configura la Pipeline de Despliegue:
    - Usa el archivo azure-pipelines.yml en el repositorio.
    - Actualiza las variables en la pipeline:
      - functionAppName: Nombre de tu aplicación de función.
      - resourceGroupName: Nombre de tu grupo de recursos.
      - azureServiceConnection: Nombre de tu conexión de servicio Azure en Azure DevOps.
      - zipDeployPublishUrl: URL de despliegue ZIP real (e.g., https://<tu-app>.scm.azurewebsites.net/api/zipdeploy).
      - zipDeployUserName y zipDeployPassword: Credenciales de despliegue generadas por Azure.
    - Asegúrate de que las credenciales sean secretas en Azure DevOps.
    ### Despliegue:
    - Confirma que la pipeline se ejecuta correctamente en Azure DevOps.
    - Accede a la función desplegada en https://<tu-app>.azurewebsites.net/api/HttpTriggerFunction y verifica el estado en Application Insights.

## Contribución
- Crea un fork del repositorio.
- Crea una rama para tus cambios:

  ```bash
  git checkout -b feature/nueva-funcionalidad

- Haz commit de tus cambios:
  
  ```bash
  git commit -m "Agrega nueva funcionalidad"
- Envía un pull request con una descripción clara.
## Licencia
- MIT - Libre para uso y modificación.

# Autor
- Freddy Urbano (@freddan58)
- ¡Gracias por visitar! Para más detalles, consulta los archivos de código o contacta al autor.