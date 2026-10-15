using Microsoft.EntityFrameworkCore;
using UnaPlan.Infrastructure.Data;

namespace UnaPlan.Infrastructure.Services;

public enum EstadoPlanEstudiante { Ok, Invalido, NoEncontrado }

public class ResultadoPlanEstudiante
{
    public EstadoPlanEstudiante Estado { get; set; }
    public string? Error { get; set; }
    public List<string> MateriasSolicitadas { get; set; } = new();
    public List<string> MateriasNoEncontradas { get; set; } = new();
    public ResultadoSuscripcion? Suscripcion { get; set; }
}

// Lógica de /api/estudiantes/solicitar-plan. La usan el endpoint y el panel de Telegram.
public class PlanEstudianteService
{
    private readonly AppDbContext _db;
    private readonly ExcelGeneratorService _excelService;
    private readonly EmailService _emailService;
    private readonly SuscripcionService _suscripcionService;

    public PlanEstudianteService(AppDbContext db, ExcelGeneratorService excelService, EmailService emailService, SuscripcionService suscripcionService)
    {
        _db = db;
        _excelService = excelService;
        _emailService = emailService;
        _suscripcionService = suscripcionService;
    }

    public async Task<ResultadoPlanEstudiante> GenerarYEnviarAsync(string nombreCompleto, string correo, string codigosMaterias)
    {
        var r = new ResultadoPlanEstudiante();

        if (string.IsNullOrWhiteSpace(codigosMaterias))
            return Invalido(r, "Debes proporcionar al menos un código de materia.");

        if (string.IsNullOrWhiteSpace(correo))
            return Invalido(r, "Debes proporcionar un correo electrónico.");

        // El mismo parser que usa la suscripción por Notion
        var listaMaterias = SuscripcionService.ParsearMaterias(codigosMaterias);
        r.MateriasSolicitadas = listaMaterias;

        if (!listaMaterias.Any())
            return Invalido(r, "El formato de los códigos proporcionados no es válido o está vacío.");

        var materiasBd = await _db.PlanesDeCurso
            .Include(p => p.MaterialesDeApoyo)
            .Include(p => p.Evaluaciones)
            .Where(p => listaMaterias.Contains(p.CodigoMateria))
            .ToListAsync();

        if (!materiasBd.Any())
        {
            r.Estado = EstadoPlanEstudiante.NoEncontrado;
            r.Error = "No se encontraron las materias en el catálogo oficial.";
            r.MateriasNoEncontradas = listaMaterias;
            return r;
        }

        var codigosEncontrados = materiasBd.Select(m => m.CodigoMateria).ToList();
        r.MateriasNoEncontradas = listaMaterias.Except(codigosEncontrados).ToList();

        var listaParaExcel = new List<ExcelGeneratorService.MateriaPlanillaDto>();

        foreach (var materia in materiasBd)
        {
            if (materia.Evaluaciones != null && materia.Evaluaciones.Any())
            {
                foreach (var eval in materia.Evaluaciones)
                {
                    listaParaExcel.Add(new ExcelGeneratorService.MateriaPlanillaDto
                    {
                        Codigo = materia.CodigoMateria,
                        Nombre = eval.NombreMateria,
                        TipoEvaluacion = eval.Tipo,
                        FechaEntrega = eval.FechaEntrega.ToString("dd/MM/yyyy"),
                        UrlPlan = materia.UrlDocumento,
                        UrlsMateriales = materia.MaterialesDeApoyo.Select(m => m.UrlDrive).ToList()
                    });
                }
            }
            else
            {
                // Fallback
                listaParaExcel.Add(new ExcelGeneratorService.MateriaPlanillaDto
                {
                    Codigo = materia.CodigoMateria,
                    Nombre = materia.NombreMateria,
                    TipoEvaluacion = "Pendiente por definir",
                    FechaEntrega = "Pendiente",
                    UrlPlan = materia.UrlDocumento,
                    UrlsMateriales = materia.MaterialesDeApoyo.Select(m => m.UrlDrive).ToList()
                });
            }
        }

        var archivoExcelBytes = _excelService.GenerarPlanDeEvaluacionExcel(listaParaExcel);
        await _emailService.EnviarPlanPersonalizadoAsync(correo, nombreCompleto, archivoExcelBytes, r.MateriasNoEncontradas);

        // Lo suscribimos igual que si viniera de Notion, para que reciba los trabajos ya publicados
        r.Suscripcion = await _suscripcionService.SuscribirAsync(nombreCompleto, correo, listaMaterias);

        r.Estado = EstadoPlanEstudiante.Ok;
        return r;
    }

    private static ResultadoPlanEstudiante Invalido(ResultadoPlanEstudiante r, string error)
    {
        r.Estado = EstadoPlanEstudiante.Invalido;
        r.Error = error;
        return r;
    }
}
