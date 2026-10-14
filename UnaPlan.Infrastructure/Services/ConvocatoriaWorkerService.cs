using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UnaPlan.Infrastructure.Services;

// ========================================================================
// 📣 Reanudación automática de la convocatoria de nuevo semestre.
// Cada hora revisa si hay una convocatoria del periodo actual a medio enviar
// (cortada por el tope de 350/24 h, el guardián o la cuota de Gmail) y envía
// lo que permita el cupo. Solo envía de 7:00 a 22:00 (hora de Venezuela).
// ========================================================================
public class ConvocatoriaWorkerService : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromHours(1);
    private const int HoraInicioVe = 7;
    private const int HoraFinVe = 22;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TelegramControlService _telegram;
    private readonly ILogger<ConvocatoriaWorkerService> _logger;

    public ConvocatoriaWorkerService(IServiceScopeFactory scopeFactory, TelegramControlService telegram, ILogger<ConvocatoriaWorkerService> logger)
    {
        _scopeFactory = scopeFactory;
        _telegram = telegram;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Dejamos arrancar la app (y despertar Supabase) antes del primer intento
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var veTime = DateTime.UtcNow.AddHours(-4);
                if (veTime.Hour >= HoraInicioVe && veTime.Hour < HoraFinVe)
                    await ReanudarSiHacePendienteAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Convocatoria-Worker] Error revisando la convocatoria pendiente.");
            }

            try { await Task.Delay(Intervalo, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReanudarSiHacePendienteAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var periodo = scope.ServiceProvider.GetRequiredService<PeriodoAcademicoService>();

        if (!await periodo.HayConvocatoriaEnCursoAsync()) return;

        var estado = await periodo.ObtenerEstadoConvocatoriaAsync();
        if (estado.EnCurso || estado.CupoDisponible == 0) return; // Aún no se libera cupo

        _logger.LogInformation($"[Convocatoria-Worker] Reanudando: {estado.Pendientes} pendientes, cupo {estado.CupoDisponible}.");
        var resultado = await periodo.EnviarConvocatoriaAsync();

        // Solo avisamos si hubo avance (evita un mensaje por hora cuando no se envía nada)
        if (resultado.Exitosos > 0 || resultado.Motivo == MotivoFinConvocatoria.Completada)
            await _telegram.NotificarAdminsAsync(TelegramControlService.FormatearResultadoConvocatoria(resultado, esReanudacion: true));
    }
}
