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
