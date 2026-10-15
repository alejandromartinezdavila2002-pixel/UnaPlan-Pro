using Microsoft.EntityFrameworkCore;
using UnaPlan.Infrastructure.Data;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UnaPlan.Infrastructure.Services;

public class TpMonitorWorkerService : BackgroundService
{
    private readonly ILogger<TpMonitorWorkerService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private int _ultimoDiaRevisado = -1;
    private bool _hayTpsPendientes = false;
    private bool _yaSeValidoHoy = false;
    private int _ultimoMinutoEscaneado = -1;

    public TpMonitorWorkerService(ILogger<TpMonitorWorkerService> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startVeTime = DateTime.UtcNow.AddHours(-4);
        if (startVeTime.DayOfWeek == DayOfWeek.Saturday)
        {
            // LogWarning a propósito, para que llegue al Telegram
            _logger.LogWarning("🟢 Micro-Worker TP Activado (Modo Sabatino: 6 AM - 2 PM).");
        }
        else
        {
            _logger.LogInformation($"💤 Micro-Worker TP Iniciado en {startVeTime.DayOfWeek}. Hibernando hasta el sábado...");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var veTime = DateTime.UtcNow.AddHours(-4);

                // Si cambió el día, reseteamos todo (sin importar la hora)
                if (veTime.Day != _ultimoDiaRevisado)
                {
                    _ultimoDiaRevisado = veTime.Day;
                    _yaSeValidoHoy = false;
                    _hayTpsPendientes = false;
                    _ultimoMinutoEscaneado = -1;
                }

                // Si no es sábado no hay nada que hacer, así que dormimos el worker 1 hora
                if (veTime.DayOfWeek != DayOfWeek.Saturday)
                {
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                    continue;
                }

                // A partir de las 6:00 AM, validamos si quedan TPs pendientes
                if (veTime.Hour >= 6 && !_yaSeValidoHoy)
                {
                    await ValidarTpsPendientesGlobalesAsync(stoppingToken);
                    _yaSeValidoHoy = true;
                }

                // Patrullaje de 6:00 AM a 2:00 PM
                if (_hayTpsPendientes && veTime.Hour >= 6)
                {
                    // Mismo corte de las 14:00 (2:00 PM) que usa el TSP
                    if (veTime.Hour >= 14)
                    {
                        _logger.LogWarning("[TP] ⚠️ Límite de las 2:00 PM alcanzado. Abortando búsqueda de TPs.");
                        _hayTpsPendientes = false;
                    }
                    else
                    {
                        // Escanear en los minutos exactos (00, 15, 30, 45)
                        if (veTime.Minute % 15 == 0 && veTime.Minute != _ultimoMinutoEscaneado)
                        {
                            _ultimoMinutoEscaneado = veTime.Minute;
                            _logger.LogInformation($"[TP] Escaneando a las {veTime:HH:mm}...");

                            using var scope = _scopeFactory.CreateScope();
                            var scraper = scope.ServiceProvider.GetRequiredService<CatalogoScraperService>();

                            await scraper.EscanearTpPendientesAsync();
                            await VerificarSiQuedanTpsPendientesAsync(stoppingToken);
                        }
                    }
                }

                // Espera normal de 30 segundos durante los sábados
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error crítico en el vigilante de TP.");
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
        }
    }

    private async Task ValidarTpsPendientesGlobalesAsync(CancellationToken token)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cantidadPendientes = await dbContext.Evaluaciones
            .Where(e => e.Tipo.StartsWith("TP"))
            .Where(e => !dbContext.TrabajosPublicados.Any(t => t.MateriaEvaluacionId == e.Id))
            .CountAsync(token);

        if (cantidadPendientes > 0)
        {
            _hayTpsPendientes = true;
            _logger.LogInformation($"[TP] ✅ Pendientes encontrados: {cantidadPendientes}. Patrullaje activo hasta las 2:00 PM.");
        }
        else
        {
            _hayTpsPendientes = false;
        }

        // Actualizamos el día revisado por seguridad (aunque ya se hace arriba)
        _ultimoDiaRevisado = DateTime.UtcNow.AddHours(-4).Day;
    }

    private async Task VerificarSiQuedanTpsPendientesAsync(CancellationToken token)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        bool quedanPendientes = await dbContext.Evaluaciones
            .Where(e => e.Tipo.StartsWith("TP"))
            .AnyAsync(e => !dbContext.TrabajosPublicados.Any(t => t.MateriaEvaluacionId == e.Id), token);

        if (!quedanPendientes)
        {
            _logger.LogInformation("[TP] 🏆 Semestre al día. Vigilante TP apagado hasta el próximo sábado.");
            _hayTpsPendientes = false;
        }
    }
}