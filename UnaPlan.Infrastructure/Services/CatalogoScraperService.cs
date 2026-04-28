using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using System.Text.RegularExpressions;
using UnaPlan.Core.Entities;
using UnaPlan.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.IO; // <--- AGREGAR ESTO PARA EL MEMORYSTREAM

namespace UnaPlan.Infrastructure.Services;

public class CatalogoScraperService
{


    // 1. Declaramos la variable de la base de datos
    private readonly AppDbContext _db;
    private readonly EmailService _emailService; // NUEVO

    // 2. El Constructor: Aquí es donde la API le "inyecta" la conexión al servicio
    public CatalogoScraperService(AppDbContext db, EmailService emailService)
    {
        _db = db;
        _emailService = emailService;
    }


    // =======================================================================
    // DICCIONARIOS DE GOOGLE DRIVE (Rastreo de TP y TSP)
    // Clave: Primer dígito del código de la materia (Ej: "1" para 100-199)
    // Valor: ID de la carpeta de Google Drive
    // =======================================================================
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
        { "1", "1PEQRe2W8SqkknAmBZbPUgxRnxAyxf2_W" }, // 100-199
        { "2", "108shUWVobZJ_7P1ejEW5k6f7kTiseZYO" }, // 200-299
        { "3", "1PLReKygdtO_XaTr0EgDsdvldU2mdmtbp" }, // 300-399
        // Nota: No hay carpeta 400 en los TP según los datos proporcionados
        { "5", "1Gs4m09mM4TNSL5iwry07lYwUSGQsnSYm" }, // 500-599
        { "6", "13Tt2gsSEgR8KwJZVwe1cM8-I9sU_Mp0w" }, // 600-699
        { "7", "1JhSL5a7eNAfVRnUzymZfwTZrG-RrjAgg" }, // 700-799
        { "8", "1-dSgGX50AXLgZvAdaOH_2FHStdiX6ZaL" }  // 800-899
    };


    // ========================================================================
    // 1. FUENTES DE DATOS ESTÁTICAS PARA PLANES DE CURSO
    // ========================================================================
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

    // ========================================================================
    // 2. FUENTES DE DATOS ESTÁTICAS PARA MATERIALES DE APOYO
    // ========================================================================
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

    // ========================================================================
    // 3. MOTOR DE AUTENTICACIÓN GOOGLE
    // ========================================================================
    private DriveService GetDriveService()
    {
        GoogleCredential credential;

        // 1. Ruta oficial segura en los servidores de Render
        string rutaCredenciales = "/etc/secrets/google-credentials.json";

        // 2. Si el archivo no está ahí (porque estamos probando con Windows), usa la ruta local.
        if (!File.Exists(rutaCredenciales))
        {
            rutaCredenciales = "google-credentials.json";
        }

        using (var stream = new FileStream(rutaCredenciales, FileMode.Open, FileAccess.Read))
        {
            credential = GoogleCredential.FromStream(stream)
                .CreateScoped(DriveService.Scope.DriveReadonly);
        }

        return new DriveService(new BaseClientService.Initializer()
        {
            HttpClientInitializer = credential,
            ApplicationName = "UnaPlanScraper"
        });
    }

    // ========================================================================
    // 4. LECTOR GENÉRICO DE CARPETAS (Soporta paginación)
    // ========================================================================
    private async Task<List<(string Nombre, string Url)>> LeerArchivosDeCarpetaAsync(DriveService service, string idCarpeta)
    {
        var archivosEncontrados = new List<(string, string)>();
        string? pageToken = null;

        try
        {
            do
            {
                var request = service.Files.List();
                // Buscamos archivos dentro de esta carpeta que NO estén en la papelera
                // Por esto (Añadimos el soporte para Drive compartido):
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

    // ========================================================================
    // 5. MOTOR DE FUSIÓN Y EXTRACCIÓN (Inteligencia Anti-Duplicados)
    // ========================================================================
    public async Task<List<CatalogoPreviewDto>> GenerarVistaPreviaMasivaAsync()
    {
        var materiasFusionadas = new Dictionary<string, CatalogoPreviewDto>();
        // Busca exactamente 3 números al inicio (Ej: "107")
        var regexCodigo = new Regex(@"^(\d{3})", RegexOptions.Compiled);

        var driveService = GetDriveService();

        // --------------------------------------------------------
        // PASO A: PROCESAR TODOS LOS PLANES DE CURSO
        // --------------------------------------------------------
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

        // --------------------------------------------------------
        // PASO B: PROCESAR TODOS LOS MATERIALES DE APOYO
        // --------------------------------------------------------
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
            // 1. Buscamos si el Plan de Curso ya existe
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

            // 2. Agregamos los materiales de apoyo
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

        // Guardamos todos los cambios de una sola vez (Atómico)
        await _db.SaveChangesAsync();
    }


    public async Task LimpiarTodoElCatalogoAsync()
    {
        // 1. Borramos primero los materiales (hijos) para que la base de datos no se queje
        await _db.MaterialesDeApoyo.ExecuteDeleteAsync();

        // 2. Borramos todos los planes de curso (padres)
        await _db.PlanesDeCurso.ExecuteDeleteAsync();
    }




    // ========================================================================
    // 6A. MOTOR DE ESCANEO TÁCTICO (TSPs - Por Fecha Específica)
    // ========================================================================
    public async Task EscanearTspAsync(DateTime fechaObjetivo)
    {
        var driveService = GetDriveService();

        // Solo buscamos TSPs que se entregan "hoy"
        var tspHoy = await _db.Evaluaciones
            .Where(e => e.Tipo.Contains("TSP") && e.FechaEntrega.Date == fechaObjetivo.Date)
            .ToListAsync();

        if (!tspHoy.Any()) return;

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

                // LECTURA EN MEMORIA (PdfPig) 
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

                // =========================================================
                // MÓDULO 5: DISPARAR NOTIFICACIONES A SUSCRIPTORES
                // =========================================================
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
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error enviando correo a {estudiante.Correo}: {ex.Message}");
                        }
                    }
                }

            }
        }
    }

    // ========================================================================
    // 6B. MOTOR DE ESCANEO ESTRATÉGICO (TPs - Búsqueda de pendientes)
    // ========================================================================
    public async Task EscanearTpPendientesAsync()
    {
        var driveService = GetDriveService();

        // Buscamos TPs en toda la base de datos que AÚN NO tengan un registro en TrabajosPublicados
        var tpPendientes = await _db.Evaluaciones
            .Where(e => e.Tipo.Contains("TP"))
            .Where(e => !_db.TrabajosPublicados.Any(t => t.MateriaEvaluacionId == e.Id))
            .ToListAsync();

        if (!tpPendientes.Any()) return; // Si ya encontramos todos los TPs del semestre, no hacemos nada

        foreach (var eval in tpPendientes)
        {
            if (string.IsNullOrEmpty(eval.CodigoMateria)) continue;

            string prefijo = eval.CodigoMateria.Substring(0, 1);
            if (!CarpetasTp.ContainsKey(prefijo)) continue;

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

                // =========================================================
                // MÓDULO 5: DISPARAR NOTIFICACIONES A SUSCRIPTORES
                // =========================================================
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
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error enviando correo a {estudiante.Correo}: {ex.Message}");
                        }
                    }
                }
            }
        }
    }



}
