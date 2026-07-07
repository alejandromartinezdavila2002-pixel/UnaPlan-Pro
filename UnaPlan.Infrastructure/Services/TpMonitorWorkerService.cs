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
    private int _ultimoMinutoEscaneado = -1;

    public TpMonitorWorkerService(ILogger<TpMonitorWorkerService> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🟢 Micro-Worker TP Activado (Modo Sabatino: 6 AM - 9 AM).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var veTime = DateTime.UtcNow.AddHours(-4);

                if (veTime.Day != _ultimoDiaRevisado && veTime.Hour == 0)
                {
                    _ultimoDiaRevisado = veTime.Day;
                    _hayTpsPendientes = false;
                    _ultimoMinutoEscaneado = -1;
                }

                if (veTime.DayOfWeek == DayOfWeek.Saturday)
                {
                    // Fase 1: Pre-validación a partir de las 6:00 AM
                    if (veTime.Hour >= 6 && veTime.Day != _ultimoDiaRevisado)
                    {
                        await ValidarTpsPendientesGlobalesAsync(stoppingToken);
                    }

                    // Fase 2: Patrullaje de 06:00 AM a 09:00 AM
                    if (_hayTpsPendientes && veTime.Hour >= 6)
                    {
                        // Límite de Cordura (Hard Stop): Las 09:00 AM
                        if (veTime.Hour >= 9)
                        {
                            _logger.LogWarning("[TP] ⚠️ Límite de las 9:00 AM alcanzado. Abortando búsqueda de TPs.");
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
                }

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
            _logger.LogInformation($"[TP] ✅ Pendientes encontrados: {cantidadPendientes}. Patrullaje activo hasta las 9:00 AM.");
        }
        else
        {
            _hayTpsPendientes = false;
        }

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