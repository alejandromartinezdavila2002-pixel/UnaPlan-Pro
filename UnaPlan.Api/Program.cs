using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using System.Net.NetworkInformation;
using UnaPlan.Core.Entities;
using UnaPlan.Infrastructure.Data;
using UnaPlan.Infrastructure.Services;

using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;

var builder = WebApplication.CreateBuilder(args);

// ========================================================================
// 1. CONFIGURACIONES Y SERVICIOS
// ========================================================================
builder.Services.AddEndpointsApiExplorer();

// Un SOLO bloque de Swagger, con tu HTML personalizado y antes del Build()
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "UnaPlan API - Motor de Extracción",
        Version = "v1",
        Description = @"# 👨‍💻 Johan Alejandro Martínez Dávila
        *Estudiante de Ingeniería de Sistemas | Especialista en Arquitectura .NET (En proceso)*

        ---
        Sistema inteligente diseñado para la automatización y gestión de cronogramas académicos UNA.",
        Contact = new OpenApiContact
        {
            Name = "Johan Alejandro Martínez Dávila",
            Email = "singin.2002.well@gmail.com"
        }
    });
});


// 2 
// Usamos el nombre exacto de tu configuración: "SupabaseConnection"
builder.Services.AddDbContextPool<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("SupabaseConnection")),
    poolSize: 100);

// Agregamos el servicio de extracción de PDFs 
builder.Services.AddScoped<PdfExtractorService>();

// Agregamos el servicio de scraping del catálogo 
builder.Services.AddScoped<CatalogoScraperService>();

// Agregamos el servicio de generación de Excel y envío de correos
builder.Services.AddScoped<ExcelGeneratorService>();

// ========================================================================
// 🛡️ REGISTRO ENTERPRISE: Google Gmail Service (Singleton)
// ========================================================================
builder.Services.AddSingleton<GmailService>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();

    var clientId = config["GmailApi:ClientId"] ?? throw new InvalidOperationException("Falta GmailApi:ClientId");
    var clientSecret = config["GmailApi:ClientSecret"] ?? throw new InvalidOperationException("Falta GmailApi:ClientSecret");
    var refreshToken = config["GmailApi:RefreshToken"] ?? throw new InvalidOperationException("Falta GmailApi:RefreshToken");

    var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
    {
        ClientSecrets = new ClientSecrets
        {
            ClientId = clientId,
            ClientSecret = clientSecret
        },
        Scopes = new[] { GmailService.Scope.GmailSend },
        DataStore = new NullDataStore() // 🛡️ Clave maestra: Desactiva el File System Lock en Windows
    });

    var credential = new UserCredential(flow, "unaplan-system", new TokenResponse
    {
        RefreshToken = refreshToken
    });

    return new GmailService(new BaseClientService.Initializer
    {
        HttpClientInitializer = credential,
        ApplicationName = "UnaPlan"
    });
});

builder.Services.AddScoped<EmailService>();


// Registra el trabajador en segundo plano
builder.Services.AddHostedService<SupabaseKeepAliveService>();

// Registra el NotionWorkerService para que se ejecute continuamente en segundo plano
builder.Services.AddHostedService<NotionWorkerService>();



builder.Services.AddHostedService<TpMonitorWorkerService>();
builder.Services.AddHostedService<TspMonitorWorkerService>();

// Aquí es donde registramos el cliente de Notion, pero sin hardcodear el token
// Registro del cliente de Notion usando IConfiguration para evitar Hardcoding
builder.Services.AddSingleton<Notion.Client.INotionClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();

    // El código busca la variable, pero no conoce su valor real aquí
    var token = config["NotionSettings:Token"];

    if (string.IsNullOrEmpty(token))
    {
        throw new InvalidOperationException("El Token de Notion no se encuentra en las variables de entorno.");
    }

    return Notion.Client.NotionClientFactory.Create(new Notion.Client.ClientOptions
    {
        AuthToken = token
    });
});

// Luego registras tu servicio que usará este cliente
builder.Services.AddScoped<NotionPublisherService>();

// ¡AQUÍ SE CONSTRUYE LA APP! (Ya no se pueden agregar más servicios al builder)
var app = builder.Build();

// ... arriba en los middlewares ...
app.UseStaticFiles(); // <--- ¡IMPORTANTE! Esto permite leer la carpeta wwwroot

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    // Inyectamos nuestro archivo de estilos personalizado
    c.InjectStylesheet("/swagger-ui/custom.css");
});
// ========================================================================
// 3. PIPELINE DE MIDDLEWARE (Diseño y Ruteo)
// ========================================================================
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "UnaPlan API v1");

        // 1. Inyectamos el Tema Oscuro (Dark Mode)
        c.InjectStylesheet("https://cdn.jsdelivr.net/npm/swagger-ui-themes@3.0.1/themes/3.x/theme-monokai.css");

        // 2. FORZAMOS el fondo oscuro para que no quede blanco el resto de la página
        c.HeadContent = "<style>body { background-color: #1b1b1b !important; } .swagger-ui .info .title { color: #ffffff !important; }</style>";
    });
}

app.UseHttpsRedirection();

// ========================================================================
// 4. ENDPOINTS (Rutas de la API)
// ========================================================================

// ---> ENDPOINT 1: PREVISUALIZAR
app.MapPost("/api/calendarios/preview", async (IFormFile archivoPdf, PdfExtractorService extractor) =>
{
    if (archivoPdf == null || archivoPdf.Length == 0)
        return Results.BadRequest("No se envió ningún archivo.");

    if (archivoPdf.ContentType != "application/pdf")
        return Results.BadRequest("El archivo debe ser un PDF.");

    var rutaTemporal = Path.GetTempFileName();
    using (var stream = new FileStream(rutaTemporal, FileMode.Create))
    {
        await archivoPdf.CopyToAsync(stream);
    }

    var resultadoPreview = extractor.ProcesarCalendarioPdf(rutaTemporal);

    // ¡AQUÍ ESTÁ LA MAGIA! 🪄
    // Pisamos el nombre temporal feo con el nombre real del archivo que subiste desde tu PC
    resultadoPreview.NombreArchivo = archivoPdf.FileName;

    if (File.Exists(rutaTemporal))
    {
        File.Delete(rutaTemporal);
    }

    return Results.Ok(resultadoPreview);
})
.DisableAntiforgery()
.WithTags("1. Procesamiento de PDFs")
.WithSummary("Previsualiza los datos de un calendario PDF")
.WithDescription("Abre el archivo PDF, extrae las materias y fechas, y devuelve un JSON limpio. No guarda nada en la base de datos.");


// ---> ENDPOINT 2: CONFIRMAR Y GUARDAR
app.MapPost("/api/calendarios/confirm", async ([FromBody] CalendarioProcesado calendarioConfirmado, AppDbContext db) =>
{
    if (calendarioConfirmado == null || !calendarioConfirmado.Evaluaciones.Any())
        return Results.BadRequest("El JSON de confirmación está vacío o inválido.");

    calendarioConfirmado.FechaProcesamiento = DateTime.UtcNow;

    db.Calendarios.Add(calendarioConfirmado);
    await db.SaveChangesAsync();

    return Results.Ok(new { mensaje = "¡Calendario guardado exitosamente en Supabase!", id = calendarioConfirmado.Id });
})
.WithTags("2. Gestión de Base de Datos")
.WithSummary("Guarda un calendario verificado")
.WithDescription("Recibe el JSON verificado y lo inserta en las tablas de Supabase.");


// ---> ENDPOINT 3: LIMPIAR TODO (PREPARACIÓN NUEVO SEMESTRE)
app.MapDelete("/api/calendarios/clear-all", async (AppDbContext db) =>
{
    try
    {
        // 1. Borramos el historial de PDFs enviados (vital para que el scraper funcione en el nuevo semestre)
        await db.TrabajosPublicados.ExecuteDeleteAsync();

        // 2. 🚀 SOFT-RESET CRM: En lugar de borrar los estudiantes, vaciamos sus materias inscritas.
        // Conservamos Nombre, Correo y FechaSuscripcion intactos, y reseteamos cualquier advertencia previa.
        await db.EstudiantesSuscritos.ExecuteUpdateAsync(setters => setters
            .SetProperty(e => e.MateriasInscritas, new List<string>())
            .SetProperty(e => e.FechaAdvertencia, (DateTime?)null));

        // 3. Borramos los calendarios (Si tu DB tiene ON DELETE CASCADE, esto borrará también las Evaluaciones)
        await db.Calendarios.ExecuteDeleteAsync();

        return Results.Ok(new { mensaje = "¡Nuevo Semestre Listo! Calendarios y publicaciones limpias. Se conservó la base de datos de estudiantes (Suscripciones vaciadas)." });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error al limpiar la base de datos: {ex.Message}");
    }
})
.WithTags("2. Gestión de Base de Datos")
.WithSummary("¡Reinicio de Semestre Inteligente!")
.WithDescription("Elimina los calendarios e historial de publicaciones viejas, pero conserva la identidad de los estudiantes limpiando su lista de materias inscritas para el nuevo ciclo académico.");



// ========================================================================
// 5. CATÁLOGO DE MATERIAS (Planes y Materiales de Apoyo)
// ========================================================================

// ---> 1. OBTENER TODO EL CATÁLOGO
app.MapGet("/api/catalogo/planes", async (AppDbContext db) =>
{
    // Usamos Include para traer el Plan de Curso JUNTO con su lista de materiales
    var planes = await db.PlanesDeCurso
        .Include(p => p.MaterialesDeApoyo)
        .ToListAsync();

    return Results.Ok(planes);
})
.WithTags("4. Gestión del Catálogo")
.WithSummary("Lista de materias completas")
.WithDescription("Devuelve todos los Planes de Curso y sus Materiales de Apoyo anidados.");


// ---> 2. CREAR O ACTUALIZAR UN PLAN DE CURSO (Upsert)
app.MapPost("/api/catalogo/planes", async ([FromBody] PlanDeCurso nuevoPlan, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(nuevoPlan.CodigoMateria))
        return Results.BadRequest("El Código de Materia es obligatorio.");

    var planExistente = await db.PlanesDeCurso.FindAsync(nuevoPlan.CodigoMateria);

    if (planExistente != null)
    {
        // Si ya existe, lo editamos (Actualizar)
        planExistente.NombreMateria = nuevoPlan.NombreMateria;
        planExistente.UrlDocumento = nuevoPlan.UrlDocumento;
        db.PlanesDeCurso.Update(planExistente);
    }
    else
    {
        // Si no existe, lo creamos (Insertar)
        db.PlanesDeCurso.Add(nuevoPlan);
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { mensaje = "Plan de Curso procesado con éxito.", codigo = nuevoPlan.CodigoMateria });
})
.WithTags("4. Gestión del Catálogo")
.WithSummary("Crea o Edita un Plan de Curso")
.WithDescription("Si el código de materia no existe, lo inserta. Si ya existe, actualiza sus datos.");


// ---> 3. ELIMINAR UN PLAN DE CURSO
app.MapDelete("/api/catalogo/planes/{codigo}", async (string codigo, AppDbContext db) =>
{
    var plan = await db.PlanesDeCurso.FindAsync(codigo);
    if (plan == null) return Results.NotFound("El Plan de Curso no fue encontrado.");

    // Gracias al ON DELETE CASCADE en la BD, esto también borrará sus Materiales de Apoyo
    db.PlanesDeCurso.Remove(plan);
    await db.SaveChangesAsync();

    return Results.Ok(new { mensaje = $"El Plan de la materia {codigo} ha sido eliminado." });
})
.WithTags("4. Gestión del Catálogo");


// ---> 4. AGREGAR UN MATERIAL DE APOYO (A un Plan ya existente)
app.MapPost("/api/catalogo/materiales", async ([FromBody] MaterialApoyo nuevoMaterial, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(nuevoMaterial.CodigoMateria))
        return Results.BadRequest("Debe especificar a qué Código de Materia pertenece este material.");

    // Verificamos que la materia padre exista
    var planExiste = await db.PlanesDeCurso.AnyAsync(p => p.CodigoMateria == nuevoMaterial.CodigoMateria);
    if (!planExiste)
        return Results.BadRequest($"No existe un Plan de Curso con el código {nuevoMaterial.CodigoMateria}. Créelo primero.");

    db.MaterialesDeApoyo.Add(nuevoMaterial);
    await db.SaveChangesAsync();

    return Results.Ok(new { mensaje = "Material de Apoyo guardado exitosamente.", id = nuevoMaterial.Id });
})
.WithTags("3. Gestión del Catálogo")
.WithSummary("Agrega un link de estudio")
.WithDescription("Inserta un nuevo material de apoyo conectado a un Plan de Curso específico mediante su código.");


// ---> 5. ELIMINAR UN MATERIAL DE APOYO ESPECÍFICO
app.MapDelete("/api/catalogo/materiales/{id}", async (int id, AppDbContext db) =>
{
    var material = await db.MaterialesDeApoyo.FindAsync(id);
    if (material == null) return Results.NotFound("Material de Apoyo no encontrado.");

    db.MaterialesDeApoyo.Remove(material);
    await db.SaveChangesAsync();

    return Results.Ok(new { mensaje = "Material eliminado correctamente." });
})
.WithTags("4. Gestión del Catálogo");



app.MapGet("/api/admin/preview-catalogo", async (CatalogoScraperService scraper) =>
{
    try
    {
        // Llamamos al motor de fusión inteligente
        var vistaPreviaJson = await scraper.GenerarVistaPreviaMasivaAsync();

        return Results.Ok(new
        {
            Mensaje = $"Se detectaron y fusionaron {vistaPreviaJson.Count} materias únicas.",
            TotalDrivesAnalizados = CatalogoScraperService.DrivePlanesDeCurso.Count + CatalogoScraperService.DriveMaterialesApoyo.Count,
            Datos = vistaPreviaJson
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error generando la vista previa: {ex.Message}");
    }
})
.WithTags("5. Panel de Administración")
.WithSummary("1. Previsualizar Mapeo de Drive (JSON)")
.WithDescription("Analiza todos los enlaces de Drive estáticos, fusiona las materias duplicadas usando su código de 3 dígitos, y muestra un JSON limpio listo para ser insertado en Supabase.");

// ---> 2. GUARDAR TODO EL CATÁLOGO EN LA BASE DE DATOS
app.MapPost("/api/admin/confirmar-catalogo", async ([FromBody] CatalogoResponseWrapper peticion, CatalogoScraperService scraper) =>
{
    // Extraemos la lista de la envoltura que envía Swagger
    var datos = peticion.Datos;

    if (datos == null || !datos.Any())
        return Results.BadRequest("No se proporcionaron datos para guardar.");

    try
    {
        await scraper.GuardarCatalogoEnBaseDeDatosAsync(datos);
        return Results.Ok(new { mensaje = $"¡Éxito! Se han procesado y guardado {datos.Count} materias en Supabase." });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error al guardar en la base de datos: {ex.Message}");
    }
})
.WithTags("6. Panel de Administración")
.WithSummary("2. Confirmar y Guardar en Supabase")
.WithDescription("Recibe el JSON completo generado por el endpoint de previsualización (incluyendo mensaje y total) y lo inserta permanentemente en las tablas de Planes y Materiales.");

// ---> 3. VACIAR TODA LA BASE DE DATOS (Peligro)
app.MapDelete("/api/admin/limpiar-catalogo", async (CatalogoScraperService scraper) =>
{
    try
    {
        await scraper.LimpiarTodoElCatalogoAsync();
        return Results.Ok(new { mensaje = "Catálogo vaciado completamente. La base de datos está en cero." });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error al limpiar el catálogo: {ex.Message}");
    }
})
.WithTags("5. Panel de Administración")
.WithSummary("3. Vaciar tablas (Borrado total)")
.WithDescription("Elimina todos los registros de Planes de Curso y Materiales de Apoyo de la base de datos de un solo golpe usando ExecuteDeleteAsync.");


// ========================================================================
// 5. ENDPOINTS PÚBLICOS (Para Estudiantes)
// ========================================================================

app.MapPost("/api/estudiantes/solicitar-plan", async (
    [FromBody] SolicitudPlanDto solicitud,
    AppDbContext db,
    ExcelGeneratorService excelService,
    EmailService emailService) =>
{
    // 1. Validación inicial
    if (string.IsNullOrWhiteSpace(solicitud.CodigosMaterias))
        return Results.BadRequest("Debes proporcionar al menos un código de materia.");

    try
    {
        // 2. PARSER INDESTRUCTIBLE
        char[] separadores = { ',', '.', ' ', '-', ';' };

        List<string> listaMaterias = solicitud.CodigosMaterias
            .Split(separadores, StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m.Trim())
            .Distinct()
            .ToList();

        if (!listaMaterias.Any())
            return Results.BadRequest("El formato de los códigos proporcionados no es válido o está vacío.");

        // 3. Búsqueda en BD
        var materiasBd = await db.PlanesDeCurso
            .Include(p => p.MaterialesDeApoyo)
            .Include(p => p.Evaluaciones)
            .Where(p => listaMaterias.Contains(p.CodigoMateria))
            .ToListAsync();

        if (!materiasBd.Any())
            return Results.NotFound("No se encontraron las materias en el catálogo oficial.");

        // 4. DETECTAR MATERIAS FALTANTES (Teoría de Conjuntos)
        var codigosEncontrados = materiasBd.Select(m => m.CodigoMateria).ToList();
        List<string> materiasNoEncontradas = listaMaterias.Except(codigosEncontrados).ToList();

        var listaParaExcel = new List<ExcelGeneratorService.MateriaPlanillaDto>();

        // 5. Mapeo para Excel
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

        // 6. Generamos Excel en Memoria
        var archivoExcelBytes = excelService.GenerarPlanDeEvaluacionExcel(listaParaExcel);

        // 7. Enviamos Correo (¡AHORA PASAMOS LA LISTA DE FALTANTES!)
        await emailService.EnviarPlanPersonalizadoAsync(
            solicitud.CorreoElectronico,
            solicitud.NombreCompleto,
            archivoExcelBytes,
            materiasNoEncontradas);

        return Results.Ok(new { mensaje = $"¡Plan generado! Se ha enviado el Excel a {solicitud.CorreoElectronico}" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Ocurrió un error al procesar tu plan: {ex.Message}");
    }
})
.WithTags("7. Servicios para Estudiantes")
.WithSummary("Generar y enviar Plan de Evaluación en Excel")
.WithDescription("Recibe las materias inscritas por el estudiante, genera un archivo Excel en memoria y se lo envía automáticamente por correo electrónico.");




app.MapPost("/api/notion/sync-cartelera", async (NotionPublisherService publisherService) =>
{
    await publisherService.SincronizarCarteleraAsync();
    return Results.Ok(new { mensaje = "Cartelera sincronizada correctamente con Notion." });
})
.WithTags("8. Sincronización Notion")
.WithSummary("Actualiza la Cartelera Informativa")
.WithDescription("Lee las próximas evaluaciones de Supabase y las publica automáticamente en la base de datos de la Cartelera de Notion.");



// ---> ENDPOINT MÁGICO PARA ENGAÑAR A EXCEL Y ABRIR GOOGLE DRIVE
app.MapGet("/api/go", (string target) =>
{
    if (string.IsNullOrEmpty(target))
        return Results.BadRequest("URL no válida");

    // Creamos una página web invisible que Excel sí acepta
    string html = $@"
    <!DOCTYPE html>
    <html lang='es'>
    <head>
        <meta charset='UTF-8'>
        <meta http-equiv='refresh' content='0; url={target}'>
        <script>window.location.replace('{target}');</script>
        <title>Redirigiendo...</title>
    </head>
    <body style='font-family: Arial, sans-serif; text-align: center; margin-top: 50px;'>
        <h2>Abriendo Google Drive... 🚀</h2>
        <p>Cargando tu material de la UNA. Si no abre en 3 segundos, <a href='{target}'>haz clic aquí</a>.</p>
    </body>
    </html>";

    return Results.Content(html, "text/html");
})
.ExcludeFromDescription(); // Esto hace que no se muestre en Swagger para mantenerlo limpio



// Endpoint para despertar la API (útil para Render y para monitoreo)
app.MapGet("/api/ping", () => Results.Ok(new { mensaje = "API Despierta y lista" }))
   .ExcludeFromDescription();


//// Endpoint para sincronizar la cartelera de Notion (puede ser llamado por el Worker o manualmente)
//app.MapPost("/api/notion/sync-cartelera", async (NotionPublisherService publisherService) =>
//{
//    await publisherService.SincronizarCarteleraAsync();
//    return Results.Ok(new { mensaje = "Cartelera sincronizada correctamente con Notion." });
//})
//.ExcludeFromDescription();

// ========================================================================
// 6. PANEL DE ADMINISTRACIÓN - SISTEMA CRM DE RETENCIÓN DE ESTUDIANTES
// ========================================================================

// ---> ENDPOINT CRM 1: ENVIAR CONVOCATORIA DE INICIO DE SEMESTRE
app.MapPost("/api/admin/crm/convocatoria-semestre", async (AppDbContext db, EmailService emailService) =>
{
    try
    {
        // Obtenemos todos los estudiantes que tienen sus materias limpias (Listas para el nuevo periodo)
        var estudiantesAConsultar = await db.EstudiantesSuscritos
            .Where(e => e.MateriasInscritas.Count == 0)
            .ToListAsync();

        if (!estudiantesAConsultar.Any())
            return Results.Ok(new { mensaje = "No se encontraron estudiantes con suscripciones vacías para notificar en este periodo." });

        // Disparamos el motor masivo balanceado
        var resultado = await emailService.EnviarConvocatoriaNuevoSemestreMasivoAsync(estudiantesAConsultar);

        return Results.Ok(new
        {
            mensaje = "Proceso de broadcast completado con éxito.",
            totalEstudiantesProcesados = estudiantesAConsultar.Count,
            enviosExitosos = resultado.exitosos,
            enviosFallidos = resultado.fallidos
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Fallo en la orquestación del envío masivo: {ex.Message}");
    }
})
.WithTags("6. Panel de Administración - CRM")
.WithSummary("1. Enviar aviso masivo de nuevo semestre")
.WithDescription("Extrae a todos los estudiantes de la base de datos cuyas materias fueron vaciadas y les envía un correo masivo, notificándoles que ya pueden solicitar sus nuevos planes.");


// ---> ENDPOINT CRM 2: PROCESAR ADVERTENCIAS DE INACTIVIDAD (EL ULTIMÁTUM)
app.MapPost("/api/admin/crm/procesar-advertencias", async (AppDbContext db, EmailService emailService) =>
{
    try
    {
        // Buscamos estudiantes que sigan inactivos (arreglo vacío) y que no hayan sido advertidos todavía
        var estudiantesInactivos = await db.EstudiantesSuscritos
            .Where(e => e.MateriasInscritas.Count == 0 && e.FechaAdvertencia == null)
            .ToListAsync();

        if (!estudiantesInactivos.Any())
            return Results.Ok(new { mensaje = "Todos los estudiantes inactivos ya se encuentran advertidos o no hay usuarios registrados." });

        int advertidosContador = 0;

        foreach (var estudiante in estudiantesInactivos)
        {
            try
            {
                // Enviamos la plantilla de alerta de 15 días
                await emailService.EnviarCorreoAdvertenciaInactividadAsync(estudiante);

                // Estampamos de manera inalterable la fecha exacta del aviso en Supabase
                estudiante.FechaAdvertencia = DateTime.UtcNow;
                advertidosContador++;
            }
            catch
            {
                // Si falla un correo individual (ej. sintaxis errónea), continuamos con el bucle para no frenar el lote
            }
        }

        if (advertidosContador > 0)
        {
            await db.SaveChangesAsync();
        }

        return Results.Ok(new { mensaje = $"Proceso finalizado. Se enviaron {advertidosContador} correos de ultimátum y se marcaron en base de datos." });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error al procesar las advertencias: {ex.Message}");
    }
})
.WithTags("6. Panel de Administración - CRM")
.WithSummary("2. Ejecutar ultimátum a usuarios inactivos")
.WithDescription("Detecta a los estudiantes que no han registrado materias en el ciclo actual, les despacha la alerta de remoción y guarda la estampa de tiempo.");


// ---> ENDPOINT CRM 3: EJECUTAR PURGA DE USUARIOS FANTASMAS (EL VERDUGO)
app.MapDelete("/api/admin/crm/purgar-fantasmas", async (AppDbContext db) =>
{
    try
    {
        // El umbral crítico ocurre cuando la fecha de advertencia supera los 15 días en el pasado
        var fechaLimite = DateTime.UtcNow.AddDays(-15);

        // El verdugo ejecuta un Hard Delete únicamente si:
        // 1. Fue advertido hace más de 15 días.
        // 2. 🚨 Sigue con la lista de materias vacía (Si inscribió en Notion, se salvó automáticamente).
        var filasBorradas = await db.EstudiantesSuscritos
            .Where(e => e.FechaAdvertencia != null && e.FechaAdvertencia <= fechaLimite && e.MateriasInscritas.Count == 0)
            .ExecuteDeleteAsync();

        return Results.Ok(new
        {
            mensaje = "Limpieza de base de datos finalizada.",
            usuariosFantasmasEliminados = filasBorradas
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error al ejecutar la purga en Supabase: {ex.Message}");
    }
})
.WithTags("6. Panel de Administración - CRM")
.WithSummary("3. Purga total de usuarios fantasmas (Hard Delete)")
.WithDescription("Elimina permanentemente de Supabase a todos aquellos estudiantes que ignoraron el ultimátum de 15 días y mantuvieron su cuenta sin actividad académica.");

app.Run();

// ========================================================================
// CLASES Y DTOS
// ========================================================================
public class CatalogoResponseWrapper
{
    public string? Mensaje { get; set; }
    public int? TotalDrivesAnalizados { get; set; }

    // Aquí es donde viajará tu lista de materias
    public List<UnaPlan.Core.Entities.CatalogoPreviewDto> Datos { get; set; } = new();
}

public class SolicitudPlanDto
{
    public string NombreCompleto { get; set; } = "";
    public string CorreoElectronico { get; set; } = "";
    public string CodigosMaterias { get; set; } = "";
}
