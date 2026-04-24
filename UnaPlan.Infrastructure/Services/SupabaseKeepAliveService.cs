using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnaPlan.Infrastructure.Data;

namespace UnaPlan.Infrastructure.Services;

public class SupabaseKeepAliveService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SupabaseKeepAliveService> _logger;

    public SupabaseKeepAliveService(IServiceProvider serviceProvider, ILogger<SupabaseKeepAliveService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Servicio Keep-Alive de Base de Datos iniciado.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Creamos un "Scope" temporal porque BackgroundService es Singleton (vive siempre)
                // y AppDbContext es Scoped (vive por petición)
                using (var scope = _serviceProvider.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    // Un ping súper ligero que no consume casi nada de procesamiento
                    await dbContext.Database.ExecuteSqlRawAsync("SELECT 1", stoppingToken);

                    _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss} UTC] Ping exitoso. La base de datos sigue despierta.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error en el ping de Keep-Alive (Ignorar si la BD estaba reiniciando): {ex.Message}");
            }

            // Se duerme por 12 horas y vuelve a ejecutarse
            await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
        }
    }
}