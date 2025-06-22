using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Punto de entrada principal para la aplicación de Azure Functions Worker.
/// Este archivo configura y ejecuta el host que alberga las funciones definidas en el proyecto.
/// </summary>
public class Program
{
    /// <summary>
    /// Método principal que inicia la aplicación de Azure Functions.
    /// Configura el host, los servicios y ejecuta la aplicación de manera asíncrona.
    /// </summary>
    public static void Main()
    {
        var host = new HostBuilder()
            // Configura la aplicación web para Azure Functions, utilizando el modelo aislado de .NET.
            .ConfigureFunctionsWebApplication()

            // Configura los servicios inyectados en la aplicación.
            .ConfigureServices(services =>
            {
                // Configura las opciones del servidor Kestrel para permitir operaciones de E/S síncronas.
                // Esto es útil para compatibilidad con ciertas bibliotecas o patrones legacy.
                services.Configure<KestrelServerOptions>(options =>
                {
                    options.AllowSynchronousIO = true;
                });

                // Agrega el servicio de telemetría de Application Insights para el modelo de trabajador aislado.
                // Permite el monitoreo de eventos, dependencias y excepciones en tiempo real.
                // Requiere que la clave de instrumentación (APPINSIGHTS_INSTRUMENTATIONKEY) esté configurada en local.settings.json.
                services.AddApplicationInsightsTelemetryWorkerService();

                // Configura la integración de Application Insights específicamente para Azure Functions.
                // Habilita el registro automático de métricas y trazas de las funciones ejecutadas.
                services.ConfigureFunctionsApplicationInsights();
            })
            .Build();

        // Inicia el host y mantiene la aplicación en ejecución hasta que se detenga.
        host.Run();
    }
}