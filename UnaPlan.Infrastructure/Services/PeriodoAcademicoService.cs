using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UnaPlan.Core.Entities;
using UnaPlan.Infrastructure.Data;

namespace UnaPlan.Infrastructure.Services;

public class ResultadoLimpieza
{
    public int TrabajosBorrados { get; set; }
    public int EvaluacionesBorradas { get; set; }
    public int CalendariosBorrados { get; set; }
    public int EstudiantesReseteados { get; set; }
}

public class ResultadoCargaPeriodo
{
    public ResultadoLimpieza Limpieza { get; set; } = new();
    public int CalendarioId { get; set; }
    public int EvaluacionesGuardadas { get; set; }
    public List<string> CodigosOmitidos { get; set; } = new();
    public int EvaluacionesOmitidas { get; set; }
}

public enum MotivoFinConvocatoria
{
    Completada,      // No queda nadie pendiente
    TopeDiario,      // Se usó el cupo de las últimas 24 h; sigue sola cuando se libere
    GuardianDiario,  // El guardián global de EmailService (todos los correos) cortó
    CuotaGmail,      // Gmail respondió que la cuota está agotada
    ErroresSeguidos, // Demasiados fallos consecutivos: algo anda mal, se detiene
    NadaPendiente,   // No había estudiantes pendientes
    YaEnCurso        // Otra convocatoria se está enviando en este momento
}

public class ResultadoConvocatoria
{
    public MotivoFinConvocatoria Motivo { get; set; }
    public int PendientesAntes { get; set; }
    public int Exitosos { get; set; }
    public int Fallidos { get; set; }
    public int Restantes { get; set; }
    public int EnviadosUltimas24h { get; set; }
    public int LimiteDiario { get; set; }
    /// <summary>Cuándo se libera cupo otra vez (UTC), si quedó gente pendiente por el tope.</summary>
    public DateTime? ProximoCupoUtc { get; set; }
    public string? Detalle { get; set; }
}

public class EstadoConvocatoria
{
    public int Pendientes { get; set; }
    public int EnviadosUltimas24h { get; set; }
    public int LimiteDiario { get; set; }
    public int CupoDisponible => Math.Max(0, LimiteDiario - EnviadosUltimas24h);
    public DateTime? ProximoCupoUtc { get; set; }
    public bool EnCurso { get; set; }
}

// ========================================================================
// Punto único del ciclo de periodo académico. Lo usan los endpoints de
// /api/calendarios/*, /api/admin/crm/convocatoria-semestre, el panel de Telegram
// y el ConvocatoriaWorkerService (reanudación diaria).
// ========================================================================
public class PeriodoAcademicoService
{
    // Estáticos porque el servicio es Scoped: el candado debe ser uno para toda la app
    private static readonly SemaphoreSlim CandadoReinicio = new(1, 1);
    private static readonly SemaphoreSlim CandadoConvocatoria = new(1, 1);

    // Estudiantes cuyo envío falló (correo inválido, etc.): no se reintentan durante 24 h
    // para que un correo malo no se reintente cada hora.
    private static readonly ConcurrentDictionary<int, DateTime> FallosRecientes = new();

    // Marca en memoria de "se pidió una convocatoria y aún queda gente". Cubre el caso
    // borde en que el primer lote no logra enviar ni uno (la marca en BD aún no existe).
    private static volatile bool _convocatoriaSolicitada;

    private const int LimiteDiarioPorDefecto = 350; // Deja ~100 de los 450 del guardián para alertas y planes
    private const int MaxFallosSeguidos = 5;

    private readonly AppDbContext _db;
    private readonly PdfExtractorService _extractor;
    private readonly EmailService _emailService;
    private readonly ILogger<PeriodoAcademicoService> _logger;
    private readonly int _limiteDiarioConvocatoria;

    public PeriodoAcademicoService(AppDbContext db, PdfExtractorService extractor, EmailService emailService,
                                   ILogger<PeriodoAcademicoService> logger, IConfiguration config)
    {
        _db = db;
        _extractor = extractor;
        _emailService = emailService;
        _logger = logger;

        var limite = config.GetValue<int?>("Crm:LimiteDiarioConvocatoria") ?? LimiteDiarioPorDefecto;
        _limiteDiarioConvocatoria = limite > 0 ? limite : LimiteDiarioPorDefecto;
    }

    // ---------------- 1. EXTRAER (no toca la base de datos) ----------------
    public CalendarioProcesado ExtraerCalendario(Stream pdf, string nombreArchivo)
    {
        var calendario = _extractor.ProcesarCalendarioPdf(pdf, nombreArchivo);
        calendario.NombreArchivo = nombreArchivo;
        return calendario;
    }

    /// <summary>Códigos del calendario que no existen en PlanesDeCurso (romperían la FK).</summary>
    public async Task<List<string>> ObtenerCodigosFueraDeCatalogoAsync(CalendarioProcesado calendario)
    {
        var codigos = calendario.Evaluaciones
            .Where(e => !string.IsNullOrWhiteSpace(e.CodigoMateria))
            .Select(e => e.CodigoMateria!)
            .Distinct()
            .ToList();

        var existentes = await _db.PlanesDeCurso
            .Where(p => codigos.Contains(p.CodigoMateria))
            .Select(p => p.CodigoMateria)
            .ToListAsync();

        return codigos.Except(existentes).OrderBy(c => c).ToList();
    }

    public Task<int> ContarEstudiantesAsync() => _db.EstudiantesSuscritos.CountAsync();

    // ---------------- 2. LIMPIAR (endpoint clear-all) ----------------
    public async Task<ResultadoLimpieza> ReiniciarSemestreAsync()
    {
        if (!await CandadoReinicio.WaitAsync(0))
            throw new InvalidOperationException("Ya hay un reinicio de semestre en curso.");

        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            var resultado = await LimpiarSinTransaccionAsync();
            await tx.CommitAsync();
            return resultado;
        }
        finally
        {
            CandadoReinicio.Release();
        }
    }

    // ---------------- 3. GUARDAR (endpoint confirm) ----------------
    public async Task<int> GuardarCalendarioAsync(CalendarioProcesado calendario)
    {
        calendario.FechaProcesamiento = DateTime.UtcNow;
        _db.Calendarios.Add(calendario);
        await _db.SaveChangesAsync();
        return calendario.Id;
    }

    // ---------------- 4. LIMPIAR + GUARDAR ATÓMICO (panel de Telegram) ----------------
    public async Task<ResultadoCargaPeriodo> ReiniciarYCargarAsync(CalendarioProcesado calendario)
    {
        if (!await CandadoReinicio.WaitAsync(0))
            throw new InvalidOperationException("Ya hay un reinicio de semestre en curso.");

        try
        {
            var resultado = new ResultadoCargaPeriodo();

            // Solo guardamos evaluaciones cuyo código exista en el catálogo (FK a PlanesDeCurso)
            var fuera = await ObtenerCodigosFueraDeCatalogoAsync(calendario);
            var validas = calendario.Evaluaciones
                .Where(e => e.CodigoMateria != null && !fuera.Contains(e.CodigoMateria))
                .ToList();

            if (validas.Count == 0)
                throw new InvalidOperationException("Ninguna evaluación del PDF coincide con el catálogo. No se borró nada.");

            resultado.CodigosOmitidos = fuera;
            resultado.EvaluacionesOmitidas = calendario.Evaluaciones.Count - validas.Count;

            // Entidades nuevas: no reutilizamos las del preview por si EF ya las rastreó en otro intento
            var nuevo = new CalendarioProcesado
            {
                NombreArchivo = calendario.NombreArchivo,
                FechaProcesamiento = DateTime.UtcNow,
                DatosJsonRaw = string.IsNullOrWhiteSpace(calendario.DatosJsonRaw) ? "{}" : calendario.DatosJsonRaw,
                Evaluaciones = validas.Select(e => new MateriaEvaluacion
                {
                    Tipo = e.Tipo,
                    Semana = e.Semana,
                    FechaEntrega = e.FechaEntrega,
                    NombreMateria = e.NombreMateria,
                    CodigoMateria = e.CodigoMateria
                }).ToList()
            };

            // Todo o nada: si el guardado falla, el semestre anterior queda intacto
            await using var tx = await _db.Database.BeginTransactionAsync();
            resultado.Limpieza = await LimpiarSinTransaccionAsync();

            _db.Calendarios.Add(nuevo);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            resultado.CalendarioId = nuevo.Id;
            resultado.EvaluacionesGuardadas = nuevo.Evaluaciones.Count;

            _logger.LogInformation($"[Periodo] Nuevo periodo cargado: {nuevo.NombreArchivo} ({resultado.EvaluacionesGuardadas} evaluaciones).");
            return resultado;
        }
        finally
        {
            CandadoReinicio.Release();
        }
    }

    private async Task<ResultadoLimpieza> LimpiarSinTransaccionAsync()
    {
        var r = new ResultadoLimpieza();

        // 1. Historial de PDFs enviados (vital para que el scraper funcione en el nuevo semestre)
        r.TrabajosBorrados = await _db.TrabajosPublicados.ExecuteDeleteAsync();

        // 2. SOFT-RESET CRM: se conservan los estudiantes; se vacían sus materias, la advertencia
        //    y la marca de convocatoria (todos quedan pendientes de la convocatoria del nuevo periodo)
        r.EstudiantesReseteados = await _db.EstudiantesSuscritos.ExecuteUpdateAsync(setters => setters
            .SetProperty(e => e.MateriasInscritas, new List<string>())
            .SetProperty(e => e.FechaAdvertencia, (DateTime?)null)
            .SetProperty(e => e.FechaConvocatoria, (DateTime?)null));

        // 3. Evaluaciones explícitas (no dependemos de un ON DELETE CASCADE en la BD)
        r.EvaluacionesBorradas = await _db.Evaluaciones.ExecuteDeleteAsync();

        // 4. Calendarios
        r.CalendariosBorrados = await _db.Calendarios.ExecuteDeleteAsync();

        // Un periodo nuevo empieza sin historial de fallos de convocatoria
        FallosRecientes.Clear();
        _convocatoriaSolicitada = false;

        return r;
    }

    // ---------------- 5. CONVOCATORIA MASIVA POR LOTES ----------------
    // Pendiente = materias vacías y sin FechaConvocatoria. Cada envío exitoso se marca en BD
    // al instante, así que si se corta (tope, Gmail, reinicio de Render) nadie la recibe dos veces.
    private IQueryable<EstudiantesSuscritos> ConsultaPendientes() =>
        _db.EstudiantesSuscritos.Where(e => e.MateriasInscritas.Count == 0 && e.FechaConvocatoria == null);

    public async Task<ResultadoConvocatoria> EnviarConvocatoriaAsync()
    {
        var r = new ResultadoConvocatoria { LimiteDiario = _limiteDiarioConvocatoria };

        if (!await CandadoConvocatoria.WaitAsync(0))
        {
            r.Motivo = MotivoFinConvocatoria.YaEnCurso;
            r.Detalle = "Ya hay una convocatoria enviándose en este momento.";
            return r;
        }

        try
        {
            _convocatoriaSolicitada = true;

            // Ventana móvil de 24 h (como cuenta Gmail). Sale de la BD: sobrevive a reinicios.
            var ahora = DateTime.UtcNow;
            var hace24h = ahora.AddHours(-24);
            r.EnviadosUltimas24h = await _db.EstudiantesSuscritos.CountAsync(e => e.FechaConvocatoria >= hace24h);
            var cupo = Math.Max(0, r.LimiteDiario - r.EnviadosUltimas24h);

            var pendientes = await ConsultaPendientes().OrderBy(e => e.Id).ToListAsync();
            r.PendientesAntes = pendientes.Count;

            if (pendientes.Count == 0)
            {
                _convocatoriaSolicitada = false;
                r.Motivo = MotivoFinConvocatoria.NadaPendiente;
                return r;
            }

            // Los que fallaron hace menos de 24 h se saltan (no gastar cupo en correos malos)
            var aEnviar = pendientes
                .Where(e => !(FallosRecientes.TryGetValue(e.Id, out var fallo) && fallo > hace24h))
                .Take(cupo)
                .ToList();

            MotivoFinConvocatoria? corte = cupo == 0 ? MotivoFinConvocatoria.TopeDiario : null;
            if (cupo > 0 && aEnviar.Count == 0)
            {
                corte = MotivoFinConvocatoria.ErroresSeguidos;
                r.Detalle = "Los pendientes que quedan fallaron hace menos de 24 h; se reintentarán después.";
            }
            int fallosSeguidos = 0;

            foreach (var estudiante in aEnviar)
            {
                try
                {
                    await _emailService.EnviarConvocatoriaNuevoSemestreAsync(estudiante);
                }
                catch (InvalidOperationException ex)
                {
                    corte = MotivoFinConvocatoria.GuardianDiario; // Límite global de EmailService
                    r.Detalle = ex.Message;
                    break;
                }
                catch (Exception ex) when (EmailService.EsCuotaGmailAgotada(ex))
                {
                    corte = MotivoFinConvocatoria.CuotaGmail;
                    r.Detalle = ex.Message;
                    break;
                }
                catch (Exception ex)
                {
                    r.Fallidos++;
                    fallosSeguidos++;
                    FallosRecientes[estudiante.Id] = DateTime.UtcNow;
                    _logger.LogError($"[CRM] Falló la convocatoria a {estudiante.Correo}: {ex.GetBaseException().Message}");

                    if (fallosSeguidos >= MaxFallosSeguidos)
                    {
                        corte = MotivoFinConvocatoria.ErroresSeguidos;
                        r.Detalle = ex.GetBaseException().Message;
                        break;
                    }
                    continue;
                }

                // Marca inmediata: el correo ya salió
                estudiante.FechaConvocatoria = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                FallosRecientes.TryRemove(estudiante.Id, out _);
                r.Exitosos++;
                r.EnviadosUltimas24h++;
                fallosSeguidos = 0;

                await Task.Delay(200); // Pequeño respiro para no saturar Google
            }

            r.Restantes = await ConsultaPendientes().CountAsync();

            if (r.Restantes == 0)
            {
                _convocatoriaSolicitada = false;
                r.Motivo = MotivoFinConvocatoria.Completada;
            }
            else
            {
                r.Motivo = corte ?? MotivoFinConvocatoria.TopeDiario;
                r.ProximoCupoUtc = await CalcularProximoCupoAsync();
            }

            _logger.LogInformation($"[CRM] Convocatoria: {r.Exitosos} enviados, {r.Fallidos} fallidos, {r.Restantes} pendientes ({r.Motivo}).");
            return r;
        }
        finally
        {
            CandadoConvocatoria.Release();
        }
    }

    public async Task<EstadoConvocatoria> ObtenerEstadoConvocatoriaAsync()
    {
        var hace24h = DateTime.UtcNow.AddHours(-24);
        var estado = new EstadoConvocatoria
        {
            Pendientes = await ConsultaPendientes().CountAsync(),
            EnviadosUltimas24h = await _db.EstudiantesSuscritos.CountAsync(e => e.FechaConvocatoria >= hace24h),
            LimiteDiario = _limiteDiarioConvocatoria,
            EnCurso = CandadoConvocatoria.CurrentCount == 0
        };

        if (estado.CupoDisponible == 0)
            estado.ProximoCupoUtc = await CalcularProximoCupoAsync();

        return estado;
    }

    /// <summary>
    /// ¿Hay una convocatoria del periodo actual a medio enviar? Solo así la reanuda el worker:
    /// un clear-all sin convocatoria, o estudiantes viejos con materias vacías, no disparan envíos.
    /// </summary>
    public async Task<bool> HayConvocatoriaEnCursoAsync()
    {
        if (!await ConsultaPendientes().AnyAsync()) return false;
        if (_convocatoriaSolicitada) return true;

        // Iniciada en este periodo = alguien la recibió después de cargar el calendario actual
        var inicioPeriodo = await _db.Calendarios.MaxAsync(c => (DateTime?)c.FechaProcesamiento);

        return inicioPeriodo == null
            ? await _db.EstudiantesSuscritos.AnyAsync(e => e.FechaConvocatoria != null)
            : await _db.EstudiantesSuscritos.AnyAsync(e => e.FechaConvocatoria >= inicioPeriodo);
    }

    // El cupo se libera 24 h después del envío más antiguo dentro de la ventana
    private async Task<DateTime?> CalcularProximoCupoAsync()
    {
        var hace24h = DateTime.UtcNow.AddHours(-24);
        var masAntiguo = await _db.EstudiantesSuscritos
            .Where(e => e.FechaConvocatoria >= hace24h)
            .MinAsync(e => e.FechaConvocatoria);

        return masAntiguo?.AddHours(24);
    }
}
