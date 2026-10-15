using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using System.Text.RegularExpressions;
using UnaPlan.Core.Entities;
using UnaPlan.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

using Telegram.Bot;
using Microsoft.Extensions.Configuration;
using System.IO; // <--- AGREGAR ESTO PARA EL MEMORYSTREAM

namespace UnaPlan.Infrastructure.Services;

public class CatalogoScraperService
{


    private readonly AppDbContext _db;
    private readonly EmailService _emailService;
    private readonly Microsoft.Extensions.Logging.ILogger<CatalogoScraperService> _logger;

    // Candado estático (el servicio es Scoped, pero el candado tiene que ser uno solo para toda la app):
    // evita que el escaneo manual y el del TpMonitorWorkerService corran a la vez y dupliquen registros/correos.
    private static readonly SemaphoreSlim _candadoEscaneoTp = new(1, 1);

    public CatalogoScraperService(AppDbContext db, EmailService emailService, Microsoft.Extensions.Logging.ILogger<CatalogoScraperService> logger)
    {
        _db = db;
        _emailService = emailService;
        _logger = logger;
    }


    // Carpetas de Drive donde están los TSP, indexadas por el primer dígito del código de materia
    // (ej. "1" cubre 100-199)
    public static readonly Dictionary<string, string> CarpetasTsp = new()
    {
        { "0", "1zSmylBd2L3lXW-8Ttgrw8p56Q4xaR6IX" }, // 000-099
        { "1", "12uQaNBdEtls5GTD7yHDSkz6syxDiAqvg" }, // 100-199
        { "2", "1zRYk1nOtQ9HHbNarwxWWtLpqEJmfxe2e" }, // 200-299
        { "3", "1sTmISd4l9dp82Hwvifi2wV1DoI9cU3fT" }, // 300-399
        { "4", "1zAuserktjbES2MlWcDaTz1bGrBHXozJD" }, // 400-499
        { "5", "13d7kkEUQUoI1br4JAj58VucXpOVgoRV8" }, // 500-599
        { "6", "1ClH7wrhffrcd0M5EDU4JNj5jDDg0lyBA" }, // 600-699
        { "7", "1vo6hFbJr7Hafqs5BSwJoq-rAoX0HbuU1" }  // 700-799
    };

    public static readonly Dictionary<string, string> CarpetasTp = new()
    {
        { "0", "1zSmylBd2L3lXW-8Ttgrw8p56Q4xaR6IX" }, // 000-099
        { "1", "1PEQRe2W8SqkknAmBZbPUgxRnxAyxf2_W" }, // 100-199
        { "2", "108shUWVobZJ_7P1ejEW5k6f7kTiseZYO" }, // 200-299
        { "3", "1PLReKygdtO_XaTr0EgDsdvldU2mdmtbp" }, // 300-399
        // No existe carpeta 400 para los TP
        { "5", "1Gs4m09mM4TNSL5iwry07lYwUSGQsnSYm" }, // 500-599
        { "6", "13Tt2gsSEgR8KwJZVwe1cM8-I9sU_Mp0w" }, // 600-699
        { "7", "1JhSL5a7eNAfVRnUzymZfwTZrG-RrjAgg" }, // 700-799
        { "8", "1-dSgGX50AXLgZvAdaOH_2FHStdiX6ZaL" }  // 800-899
    };


    // Carpetas de Drive con los planes de curso, por carrera
    public static readonly Dictionary<string, string> DrivePlanesDeCurso = new()
    {
        { "Administración de Empresas", "1JMXsaf3z5ZX26Yx0rgeKY3Rlh2VZeVfm" },
        { "Administración Riesgos y Seguros", "1kttHrUzc0jTAMhJsh8fWs3lEaR_Ixy-p" },
        { "Contaduría Pública", "1L0eGsmVMAvia5Q4Ronvo-bMCWNmk0pFm" },
        { "Educación Integral", "1k6HUPXnAVlzoijbS6bFOF3TWlOvG97EX" },
        { "Educación Dificultades de Aprendizaje", "1oFbb5r4dFpbPZCfQrzHExukdcmbK7TJx" },
        { "Educación Matemática", "1TRFYYWZOUc9boBfLseZm8bFxB3FcNcGX" },
        { "Educación Inicial", "1dsiTBrC4OyrGWJUrIwUcvXVOMgNhftPY" },
        { "Ingeniería de Sistemas", "1ur135N7IeS7hWgZm6fueWHPKrOwXjObu" },
        { "Ingeniería Industrial", "1_AoiymGgijthOjKPaGdaV2udggPKGhvS" },
        { "Matemática", "1Nn0sriM6oURhxojpmRACs8HqABVQEW9f" },
        { "TSU Educación Integral", "1-GlrvhKK7OkdLwNU0lWY7m1di4B4OMV7" },
        { "TSU Higiene y Seguridad Industrial", "1dNFgvQTl1P0PCfxBDJF9wGpqIdAOr21K" },
        { "TSU Mantenimiento de Sistemas Informáticos", "1xPGfdMBb0l8GKRV2hS-hW0XTUaol10dd" },
        { "TSU Contaduría", "1sUZVBruTaWEpcXqZl3O0P1pBqqp3UUGx" }
    };

    // Carpetas de Drive con los materiales de apoyo
    public static readonly Dictionary<string, string> DriveMaterialesApoyo = new()
    {
        { "126 Matemática", "1LQBPP8yxvKFb2-y4u4diB0OoOrTpHYVN" },
        { "236 Ingeniería de Sistemas", "1TtUBY1hH5knwVOMWBoQ26cHE9rkrNYRv" },
        { "237 TSU Sistemas Informáticos", "12wJDQRWt06bewgLOtcXbge9VylkLlr7y" },
        { "280 Ingeniería Industrial", "1wbYkf54i3jxvZEyYLhdvNM2nXonjOzCy" },
        { "281 TSU Higiene y Seguridad", "1xAJqeviuuuIuuJ91W0Pp6Acqckh051_K" },
        { "430 TSU Educación Integral", "1qpAwigDBHgwFuRnBOmi2mi-fkhl-5lQ9" },
        { "440 Lic. Educación Integral", "1W5uCgzBQ0DpPRTXqzAsS3SGyWAekKZw9" },
        { "508 Educación Matemática", "1ZlikgnTYzsavIKypyMkI3c64Ehsh5rDi" },
        { "521 Educación Dificultades de Aprendizaje", "18IA2AkIqqjekufQBc9Joa7-eoDcCnIPY" },
        { "542 Educación Preescolar", "1aRK9osbI-CNh01zkoStWP4TDPLB_NPLD" },
        { "610 Contaduría Pública", "1hiuet-prrvRsAjia9ytmAfQRcJO7Sazd" },
        { "612 Administración de Empresas", "1Z5Pvl081TbxTltnFvqoqvPjCmuNi7H-e" },
        { "613 Administración de Empresas Riesgos y Seguros", "1E8GVE_zllVSnitxBT1kk7uRgcjZnkMZB" }
        
    };

    // Autenticación contra Google Drive
    private DriveService GetDriveService()
    {
        GoogleCredential credential;

        // Ruta donde Render monta el secret file
        string rutaCredenciales = "/etc/secrets/google-credentials.json";

        // Si no está (estamos en local, en Windows), cae a la ruta del repo
        if (!File.Exists(rutaCredenciales))
        {
            rutaCredenciales = "google-credentials.json";
        }

        // FromJson está deprecado pero el JSON viene de un archivo nuestro, no de un usuario; se silencia el warning
        #pragma warning disable CS0618
                credential = GoogleCredential.FromJson(File.ReadAllText(rutaCredenciales))
                    .CreateScoped(DriveService.Scope.DriveReadonly);
        #pragma warning restore CS0618


        return new DriveService(new BaseClientService.Initializer()
        {
            HttpClientInitializer = credential,
            ApplicationName = "UnaPlanScraper"
        });
    }

    // Lee todos los archivos de una carpeta de Drive, paginando si hace falta
    private async Task<List<(string Nombre, string Url)>> LeerArchivosDeCarpetaAsync(DriveService service, string idCarpeta)
    {
        var archivosEncontrados = new List<(string, string)>();
        string? pageToken = null;

        try
        {
            do
            {
                var request = service.Files.List();
                // Archivos de esta carpeta que no estén en la papelera (incluye Drives compartidos)
                request.Q = $"'{idCarpeta}' in parents and trashed = false";
                request.IncludeItemsFromAllDrives = true;
                request.SupportsAllDrives = true;
                // Solicitamos solo el nombre, el link y el token de siguiente página
                request.Fields = "nextPageToken, files(name, webViewLink)";
                request.PageToken = pageToken;

                var result = await request.ExecuteAsync();

                if (result.Files != null)
                {
                    foreach (var file in result.Files)
                    {
                        archivosEncontrados.Add((file.Name, file.WebViewLink));
                    }
                }

                pageToken = result.NextPageToken; // Si hay más de 100 archivos, sigue buscando

            } while (pageToken != null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error leyendo la carpeta {idCarpeta}: {ex.Message}");
        }

        return archivosEncontrados;
    }

    // Recorre Drive y fusiona planes + materiales por código de materia, sin duplicar
    public async Task<List<CatalogoPreviewDto>> GenerarVistaPreviaMasivaAsync()
    {
        var materiasFusionadas = new Dictionary<string, CatalogoPreviewDto>();
        // Busca exactamente 3 números al inicio (Ej: "107")
        var regexCodigo = new Regex(@"^(\d{3})", RegexOptions.Compiled);

        var driveService = GetDriveService();

        // Planes de curso primero
        foreach (var carrera in DrivePlanesDeCurso)
        {
            var archivosEnDrive = await LeerArchivosDeCarpetaAsync(driveService, carrera.Value);

            foreach (var archivo in archivosEnDrive)

            {

                Console.WriteLine($"Analizando archivo: {archivo.Nombre}");


                var match = regexCodigo.Match(archivo.Nombre);
                if (match.Success)
                {
                    string codigoMateria = match.Groups[1].Value;

                    if (!materiasFusionadas.ContainsKey(codigoMateria))
                    {
                        materiasFusionadas[codigoMateria] = new CatalogoPreviewDto
                        {
                            CodigoMateria = codigoMateria,
                            NombreArchivoPlan = archivo.Nombre,
                            UrlPlanCurso = archivo.Url,
                        };
                    }

                    if (!materiasFusionadas[codigoMateria].CarrerasQueLaVen.Contains(carrera.Key))
                    {
                        materiasFusionadas[codigoMateria].CarrerasQueLaVen.Add(carrera.Key);
                    }
                }
            }
        }

        // Y después los materiales de apoyo
        foreach (var categoria in DriveMaterialesApoyo)
        {
            var archivosEnDrive = await LeerArchivosDeCarpetaAsync(driveService, categoria.Value);

            foreach (var archivo in archivosEnDrive)
            {
                var match = regexCodigo.Match(archivo.Nombre);
                if (match.Success)
                {
                    string codigoMateria = match.Groups[1].Value;

                    // Si encontramos un material pero esa materia no estaba en los planes, la creamos "vacía"
                    if (!materiasFusionadas.ContainsKey(codigoMateria))
                    {
                        materiasFusionadas[codigoMateria] = new CatalogoPreviewDto
                        {
                            CodigoMateria = codigoMateria,
                            NombreArchivoPlan = "Sin Plan de Curso",
                            UrlPlanCurso = ""
                        };
                    }

                    // Prevenimos agregar el mismo link dos veces
                    bool materialYaExiste = materiasFusionadas[codigoMateria].Materiales
                        .Any(m => m.UrlDrive == archivo.Url);

                    if (!materialYaExiste)
                    {
                        materiasFusionadas[codigoMateria].Materiales.Add(new MaterialApoyoPreview
                        {
                            NombreCarpeta = archivo.Nombre,
                            UrlDrive = archivo.Url
                        });
                    }
                }
            }
        }


        return materiasFusionadas.Values.OrderBy(m => m.CodigoMateria).ToList();



    }


    public async Task GuardarCatalogoEnBaseDeDatosAsync(List<CatalogoPreviewDto> catalogo)
    {
        foreach (var item in catalogo)
        {
            // ¿Ya existe el plan de curso?
            var planExistente = await _db.PlanesDeCurso
                .Include(p => p.MaterialesDeApoyo)
                .FirstOrDefaultAsync(p => p.CodigoMateria == item.CodigoMateria);

            if (planExistente != null)
            {
                // Actualizamos datos básicos si ya existe
                planExistente.NombreMateria = item.NombreArchivoPlan;
                planExistente.UrlDocumento = item.UrlPlanCurso;

                // Limpiamos los materiales viejos para poner los nuevos (evita duplicados)
                _db.MaterialesDeApoyo.RemoveRange(planExistente.MaterialesDeApoyo);
            }
            else
            {
                // Si es nuevo, creamos el Plan
                planExistente = new PlanDeCurso
                {
                    CodigoMateria = item.CodigoMateria,
                    NombreMateria = item.NombreArchivoPlan,
                    UrlDocumento = item.UrlPlanCurso
                };
                _db.PlanesDeCurso.Add(planExistente);
            }

            // Y los materiales de apoyo
            foreach (var mat in item.Materiales)
            {
                _db.MaterialesDeApoyo.Add(new MaterialApoyo
                {
                    CodigoMateria = item.CodigoMateria,
                    Titulo = mat.NombreCarpeta,
                    UrlDrive = mat.UrlDrive
                });
            }
        }

        await _db.SaveChangesAsync();
    }


    public async Task LimpiarTodoElCatalogoAsync()
    {
        // Los materiales primero (son hijos de PlanesDeCurso por FK)
        await _db.MaterialesDeApoyo.ExecuteDeleteAsync();
        await _db.PlanesDeCurso.ExecuteDeleteAsync();
    }




    // Busca los TSP que se entregan en una fecha puntual
    public async Task EscanearTspAsync(DateTime fechaObjetivo)
    {
        var driveService = GetDriveService();

        // Solo buscamos TSPs que se entregan "hoy"
        var tspHoy = await _db.Evaluaciones
            .Where(e => e.Tipo.Contains("TSP") && e.FechaEntrega.Date == fechaObjetivo.Date)
            .ToListAsync();

        if (!tspHoy.Any()) return;

        int procesadosConExito = 0;
        int alertasEnviadas = 0;
        var batchDetails = new System.Text.StringBuilder();

        foreach (var eval in tspHoy)
        {
            if (string.IsNullOrEmpty(eval.CodigoMateria)) continue;

            string prefijo = eval.CodigoMateria.Substring(0, 1);
            if (!CarpetasTsp.ContainsKey(prefijo)) continue;

            string idCarpeta = CarpetasTsp[prefijo];

            var request = driveService.Files.List();
            request.Q = $"'{idCarpeta}' in parents and name contains '{eval.CodigoMateria}' and trashed = false";
            request.Fields = "files(id, name, webViewLink)";
            request.SupportsAllDrives = true;
            request.IncludeItemsFromAllDrives = true;

            var response = await request.ExecuteAsync();
            var archivoDrive = response.Files?.FirstOrDefault();

            if (archivoDrive != null)
            {
                bool yaPublicado = await _db.TrabajosPublicados.AnyAsync(t => t.MateriaEvaluacionId == eval.Id);
                if (yaPublicado) continue;

                DateTime? fechaRealExtraida = null;

                // Abrimos el PDF en memoria con PdfPig para sacarle la fecha real
                try
                {
                    var downloadRequest = driveService.Files.Get(archivoDrive.Id);
                    using var memoryStream = new MemoryStream();
                    await downloadRequest.DownloadAsync(memoryStream);
                    memoryStream.Position = 0;

                    using var pdfDocument = UglyToad.PdfPig.PdfDocument.Open(memoryStream);
                    string textoCompleto = string.Join(" ", pdfDocument.GetPages().Select(p => p.Text));

                    var regexFecha = new Regex(@"\b(\d{2}[/-]\d{2}[/-]\d{4})\b");
                    var match = regexFecha.Match(textoCompleto);

                    if (match.Success && DateTime.TryParseExact(match.Groups[1].Value.Replace("-", "/"), "dd/MM/yyyy", null, System.Globalization.DateTimeStyles.None, out DateTime parsedDate))
                    {
                        fechaRealExtraida = DateTime.SpecifyKind(parsedDate.Date.AddHours(23).AddMinutes(59).AddSeconds(59), DateTimeKind.Utc);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error leyendo PDF de TSP {eval.CodigoMateria}: {ex.Message}");
                }

                if (fechaRealExtraida.HasValue)
                {
                    eval.FechaEntregaReal = fechaRealExtraida;
                    _db.Evaluaciones.Update(eval);
                }

                _db.TrabajosPublicados.Add(new TrabajosPublicados
                {
                    MateriaEvaluacionId = eval.Id,
                    UrlDrive = archivoDrive.WebViewLink,
                    Tipo = eval.Tipo,
                    FechaPublicacion = DateTime.UtcNow
                });


                await _db.SaveChangesAsync();
                Console.WriteLine($"¡Trabajo encontrado y guardado!: {eval.CodigoMateria}");

                // Avisamos a los suscritos a esta materia
                var estudiantesInteresados = await _db.EstudiantesSuscritos
                    .Where(e => e.MateriasInscritas.Contains(eval.CodigoMateria))
                    .ToListAsync();

                if (estudiantesInteresados.Any())
                {
                    Console.WriteLine($"Enviando {estudiantesInteresados.Count} alertas para la materia {eval.CodigoMateria}...");
                    DateTime fechaMostrar = eval.FechaEntregaReal ?? eval.FechaEntrega;

                    foreach (var estudiante in estudiantesInteresados)
                    {
                        try
                        {
                            await _emailService.EnviarNotificacionTrabajoPublicadoAsync(
                                estudiante.Correo,
                                estudiante.Nombre,
                                eval.CodigoMateria,
                                eval.Tipo,
                                fechaMostrar,
                                archivoDrive.WebViewLink
                            );
                            await Task.Delay(500); // Freno para no saturar Gmail
                            alertasEnviadas++;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error enviando correo a {estudiante.Correo}: {ex.Message}");
                        }
                    }
                }

                procesadosConExito++;
                batchDetails.AppendLine($"✅ {eval.CodigoMateria} (Alertas: {estudiantesInteresados.Count})");
            }
        }

        if (procesadosConExito > 0)
        {
            string summary = $"📋 {procesadosConExito} nuevos TSP encontrados en Drive. ({alertasEnviadas} alertas enviadas).";
            UnaPlan.Infrastructure.Logging.TelegramLoggerExtensions.LogTelegramBatch(_logger, summary, batchDetails.ToString());
        }
    }

    // Busca los TP que todavía no se han publicado
    /// <param name="esperarSiOcupado">
    /// true (worker): espera a que termine el escaneo en curso.
    /// false (manual): si ya hay uno corriendo, regresa de inmediato con EscaneoEnCurso = true.
    /// </param>
    public async Task<ResultadoEscaneoTp> EscanearTpPendientesAsync(bool esperarSiOcupado = true)
    {
        if (esperarSiOcupado)
        {
            await _candadoEscaneoTp.WaitAsync();
        }
        else if (!await _candadoEscaneoTp.WaitAsync(0))
        {
            return new ResultadoEscaneoTp { EscaneoEnCurso = true };
        }

        try
        {
            return await EscanearTpPendientesInternoAsync();
        }
        finally
        {
            _candadoEscaneoTp.Release();
        }
    }

    private async Task<ResultadoEscaneoTp> EscanearTpPendientesInternoAsync()
    {
        var resultado = new ResultadoEscaneoTp();
        var driveService = GetDriveService();

        // TP sin registro todavía en TrabajosPublicados
        var tpPendientes = await _db.Evaluaciones
            // StartsWith, no Contains: "TSP" también matchea "TP" con Contains
            .Where(e => e.Tipo.StartsWith("TP"))
            .Where(e => !_db.TrabajosPublicados.Any(t => t.MateriaEvaluacionId == e.Id))
            .ToListAsync();

        resultado.PendientesRevisados = tpPendientes.Count;
        if (!tpPendientes.Any()) return resultado;

        int procesadosConExito = 0;
        int alertasEnviadas = 0;
        var batchDetails = new System.Text.StringBuilder();

        foreach (var eval in tpPendientes)
        {
            if (string.IsNullOrEmpty(eval.CodigoMateria)) continue;

            string prefijo = eval.CodigoMateria.Substring(0, 1);
            if (!CarpetasTp.ContainsKey(prefijo))
            {
                resultado.SiguenPendientes.Add($"{eval.CodigoMateria} ({eval.Tipo}) · sin carpeta TP configurada");
                continue;
            }

            string idCarpeta = CarpetasTp[prefijo];

            var request = driveService.Files.List();
            request.Q = $"'{idCarpeta}' in parents and name contains '{eval.CodigoMateria}' and trashed = false";
            request.Fields = "files(id, name, webViewLink)";
            request.SupportsAllDrives = true;
            request.IncludeItemsFromAllDrives = true;

            var response = await request.ExecuteAsync();
            var archivoDrive = response.Files?.FirstOrDefault();

            if (archivoDrive != null)
            {
                // Como es TP, no abrimos el PDF, solo guardamos el enlace directamente
                _db.TrabajosPublicados.Add(new TrabajosPublicados
                {
                    MateriaEvaluacionId = eval.Id,
                    UrlDrive = archivoDrive.WebViewLink,
                    Tipo = eval.Tipo,
                    FechaPublicacion = DateTime.UtcNow
                });


                await _db.SaveChangesAsync();
                Console.WriteLine($"¡Trabajo encontrado y guardado!: {eval.CodigoMateria}");

                // Avisamos a los suscritos a esta materia
                var estudiantesInteresados = await _db.EstudiantesSuscritos
                    .Where(e => e.MateriasInscritas.Contains(eval.CodigoMateria))
                    .ToListAsync();

                if (estudiantesInteresados.Any())
                {
                    Console.WriteLine($"Enviando {estudiantesInteresados.Count} alertas para la materia {eval.CodigoMateria}...");
                    DateTime fechaMostrar = eval.FechaEntregaReal ?? eval.FechaEntrega;

                    foreach (var estudiante in estudiantesInteresados)
                    {
                        try
                        {
                            await _emailService.EnviarNotificacionTrabajoPublicadoAsync(
                                estudiante.Correo,
                                estudiante.Nombre,
                                eval.CodigoMateria,
                                eval.Tipo,
                                fechaMostrar,
                                archivoDrive.WebViewLink
                            );
                            await Task.Delay(500); // Freno para no saturar Gmail
                            alertasEnviadas++;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error enviando correo a {estudiante.Correo}: {ex.Message}");
                        }
                    }
                }

                procesadosConExito++;
                batchDetails.AppendLine($"✅ {eval.CodigoMateria} (Alertas: {estudiantesInteresados.Count})");
                resultado.Encontrados.Add($"{eval.CodigoMateria} ({eval.Tipo}) · {estudiantesInteresados.Count} alertas");
            }
            else
            {
                resultado.SiguenPendientes.Add($"{eval.CodigoMateria} ({eval.Tipo})");
            }
        }

        resultado.AlertasEnviadas = alertasEnviadas;

        if (procesadosConExito > 0)
        {
            string summary = $"📋 {procesadosConExito} nuevos TP encontrados en Drive. ({alertasEnviadas} alertas enviadas).";
            UnaPlan.Infrastructure.Logging.TelegramLoggerExtensions.LogTelegramBatch(_logger, summary, batchDetails.ToString());
        }

        return resultado;
    }

}

// Resultado del escaneo de TPs: lo usan el endpoint manual y el botón de Telegram
public class ResultadoEscaneoTp
{
    /// <summary>true si ya había otro escaneo corriendo y este no se ejecutó.</summary>
    public bool EscaneoEnCurso { get; set; }
    public int PendientesRevisados { get; set; }
    public int AlertasEnviadas { get; set; }
    public List<string> Encontrados { get; set; } = new();
    public List<string> SiguenPendientes { get; set; } = new();
}
