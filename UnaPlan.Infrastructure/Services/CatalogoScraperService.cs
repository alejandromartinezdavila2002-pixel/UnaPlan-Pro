using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using System.Text.RegularExpressions;
using UnaPlan.Core.Entities;
using UnaPlan.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace UnaPlan.Infrastructure.Services;

public class CatalogoScraperService
{


    // 1. Declaramos la variable de la base de datos
    private readonly AppDbContext _db;

    // 2. El Constructor: Aquí es donde la API le "inyecta" la conexión al servicio
    public CatalogoScraperService(AppDbContext db)
    {
        _db = db;
    }

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


}
