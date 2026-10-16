using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.OpenApi.Models;
using System.Net.NetworkInformation;
using UnaPlan.Core.Entities;
using UnaPlan.Infrastructure.Data;
using UnaPlan.Infrastructure.Services;
using UnaPlan.Infrastructure.Logging;

using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();

builder.Logging.AddTelegramBot(options =>
{
    options.BotToken = builder.Configuration["Telegram:BotToken"] ?? "";

    // La lista de admins puede venir de Secrets o de Render, separada por comas
    var adminIdsString = builder.Configuration["Telegram:AdminChatIds"];
    if (!string.IsNullOrEmpty(adminIdsString))
    {
        options.AdminChatIds = adminIdsString.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                             .Select(id => long.Parse(id.Trim()))
                                             .ToList();
    }
    else
    {
        var admins = builder.Configuration.GetSection("Telegram:AdminChatIds").Get<List<long>>();
        if (admins != null)
        {
            options.AdminChatIds = admins;
        }
    }

    options.RenderUrl = builder.Configuration["Telegram:RenderUrl"] ?? "";
    options.MinimumLevel = LogLevel.Warning;
});

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


// El nombre de la cadena de conexión tiene que ser exactamente "SupabaseConnection"
builder.Services.AddDbContextPool<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("SupabaseConnection")),
    poolSize: 100);

builder.Services.AddScoped<PdfExtractorService>();
builder.Services.AddScoped<CatalogoScraperService>();
builder.Services.AddScoped<ExcelGeneratorService>();

// Singleton porque mantiene estado (sesiones del panel) entre requests
builder.Services.AddSingleton<TelegramControlService>();

// Gmail también va como Singleton: crear el cliente OAuth2 en cada request sale caro
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
        DataStore = new NullDataStore() // Sin esto, en Windows el flujo OAuth2 intenta escribir un token en disco y falla
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

// Suscripción única de estudiantes (endpoint + Notion) y envío de trabajos ya publicados
builder.Services.AddScoped<SuscripcionService>();

// Ciclo de periodo académico (PDF → limpiar → cargar → convocatoria) y plan individual.
// Los usan tanto los endpoints como el panel de Telegram.
builder.Services.AddScoped<PeriodoAcademicoService>();
builder.Services.AddScoped<PlanEstudianteService>();
builder.Services.AddScoped<EstatusService>(); // Botón "📊 Estatus" de Telegram


builder.Services.AddHostedService<SupabaseKeepAliveService>();
builder.Services.AddHostedService<NotionWorkerService>();
builder.Services.AddHostedService<TpMonitorWorkerService>();
builder.Services.AddHostedService<TspMonitorWorkerService>();

// Reanuda la convocatoria de nuevo semestre cuando se libera cupo (tope por 24 h)
builder.Services.AddHostedService<ConvocatoriaWorkerService>();

// Cliente de Notion, con el token leído de la config en vez de hardcodeado
builder.Services.AddSingleton<Notion.Client.INotionClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
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

// Singleton + HostedService: así corre en segundo plano y también se puede inyectar en los endpoints
builder.Services.AddSingleton<NotionPublisherService>();

builder.Services.AddHostedService(provider => provider.GetRequiredService<NotionPublisherService>());

// Registramos el Dispatcher (Singleton para la cola, HostedService para el bucle)
builder.Services.AddSingleton<TelegramLogDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelegramLogDispatcher>());

// A partir de aquí ya no se pueden agregar más servicios al builder
var app = builder.Build();

app.UseStaticFiles(); // necesario para servir wwwroot

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.InjectStylesheet("/swagger-ui/custom.css");
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "UnaPlan API v1");

        // Tema oscuro en Swagger
        c.InjectStylesheet("https://cdn.jsdelivr.net/npm/swagger-ui-themes@3.0.1/themes/3.x/theme-monokai.css");
        c.HeadContent = "<style>body { background-color: #1b1b1b !important; } .swagger-ui .info .title { color: #ffffff !important; }</style>";
    });
}

app.UseHttpsRedirection();

// Endpoint: previsualizar
app.MapPost("/api/calendarios/preview", async (IFormFile archivoPdf, PeriodoAcademicoService periodo) =>
{
    if (archivoPdf == null || archivoPdf.Length == 0)
        return Results.BadRequest("No se envió ningún archivo.");

    if (archivoPdf.ContentType != "application/pdf")
        return Results.BadRequest("El archivo debe ser un PDF.");

    // Se procesa en memoria (sin archivo temporal), igual que el panel de Telegram
    using var memoria = new MemoryStream();
    await archivoPdf.CopyToAsync(memoria);
    memoria.Position = 0;

    var resultadoPreview = periodo.ExtraerCalendario(memoria, archivoPdf.FileName);

    return Results.Ok(resultadoPreview);
})
.DisableAntiforgery()
.WithTags("1. Procesamiento de PDFs")
.WithSummary("Previsualiza los datos de un calendario PDF")
.WithDescription("Abre el archivo PDF, extrae las materias y fechas, y devuelve un JSON limpio. No guarda nada en la base de datos.");


// Endpoint: confirmar y guardar
app.MapPost("/api/calendarios/confirm", async ([FromBody] CalendarioProcesado calendarioConfirmado, PeriodoAcademicoService periodo) =>
{
    if (calendarioConfirmado == null || !calendarioConfirmado.Evaluaciones.Any())
        return Results.BadRequest("El JSON de confirmación está vacío o inválido.");

    var id = await periodo.GuardarCalendarioAsync(calendarioConfirmado);

    return Results.Ok(new { mensaje = "¡Calendario guardado exitosamente en Supabase!", id });
})
.WithTags("2. Gestión de Base de Datos")
.WithSummary("Guarda un calendario verificado")
.WithDescription("Recibe el JSON verificado y lo inserta en las tablas de Supabase.");


// Endpoint: limpiar todo para preparar el nuevo semestre
app.MapDelete("/api/calendarios/clear-all", async (PeriodoAcademicoService periodo) =>
{
    try
    {
        // Transaccional: TrabajosPublicados, soft-reset de estudiantes, Evaluaciones y Calendarios
        await periodo.ReiniciarSemestreAsync();

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



// Catálogo de materias (planes y materiales de apoyo)

app.MapGet("/api/catalogo/planes", async (AppDbContext db) =>
{
    // Include para traer cada plan de curso junto con sus materiales
    var planes = await db.PlanesDeCurso
        .Include(p => p.MaterialesDeApoyo)
        .ToListAsync();

    return Results.Ok(planes);
})
.WithTags("4. Gestión del Catálogo")
.WithSummary("Lista de materias completas")
.WithDescription("Devuelve todos los Planes de Curso y sus Materiales de Apoyo anidados.");


// Crea o actualiza un plan de curso (upsert)
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


// Elimina un plan de curso
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


// Agrega un material de apoyo a un plan ya existente
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


// Elimina un material de apoyo específico
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

// Guarda todo el catálogo en la base de datos
app.MapPost("/api/admin/confirmar-catalogo", async ([FromBody] CatalogoResponseWrapper peticion, CatalogoScraperService scraper) =>
{
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

// Vacía toda la base de datos del catálogo (ojo, esto es destructivo)
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

// Búsqueda manual de TPs, sin esperar al escaneo semanal
app.MapPost("/api/admin/tp/buscar-ahora", async (CatalogoScraperService scraper) =>
{
    try
    {
        var resultado = await scraper.EscanearTpPendientesAsync(esperarSiOcupado: false);

        if (resultado.EscaneoEnCurso)
            return Results.Conflict(new { mensaje = "Ya hay un escaneo de TPs en curso (manual o del vigilante). Intenta en unos minutos." });

        return Results.Ok(new
        {
            mensaje = resultado.PendientesRevisados == 0
                ? "Semestre al día: no hay TPs pendientes."
                : $"Búsqueda terminada: {resultado.Encontrados.Count} de {resultado.PendientesRevisados} TPs pendientes encontrados en Drive.",
            resultado.PendientesRevisados,
            resultado.AlertasEnviadas,
            resultado.Encontrados,
            resultado.SiguenPendientes
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error buscando TPs: {ex.Message}");
    }
})
.WithTags("5. Panel de Administración")
.WithSummary("4. Buscar TPs pendientes ahora")
.WithDescription("Ejecuta en el momento el mismo escaneo de Drive que hace el TpMonitorWorkerService los sábados: registra los TPs que ya salieron y envía las alertas por correo a los suscritos. Devuelve 409 si ya hay un escaneo corriendo.");


// Endpoints públicos (para estudiantes)

app.MapPost("/api/estudiantes/solicitar-plan", async (
    [FromBody] SolicitudPlanDto solicitud,
    PlanEstudianteService planService) =>
{
    try
    {
        var r = await planService.GenerarYEnviarAsync(
            solicitud.NombreCompleto,
            solicitud.CorreoElectronico,
            solicitud.CodigosMaterias);

        if (r.Estado == EstadoPlanEstudiante.Invalido) return Results.BadRequest(r.Error);
        if (r.Estado == EstadoPlanEstudiante.NoEncontrado) return Results.NotFound(r.Error);

        return Results.Ok(new
        {
            mensaje = $"¡Plan generado! Se ha enviado el Excel a {solicitud.CorreoElectronico}",
            suscrito = true,
            suscripcionNueva = r.Suscripcion!.EsNuevo,
            trabajosYaPublicadosEnviados = r.Suscripcion.TrabajosEnviados,
            errorTrabajosPublicados = r.Suscripcion.ErrorEnvioTrabajos
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Ocurrió un error al procesar tu plan: {ex.Message}");
    }
})
.WithTags("7. Servicios para Estudiantes")
.WithSummary("Generar y enviar Plan de Evaluación en Excel")
.WithDescription("Recibe las materias inscritas por el estudiante, genera un archivo Excel en memoria y se lo envía por correo. Además lo suscribe en EstudiantesSuscritos (igual que el formulario de Notion) y le envía en un correo aparte los TP/TSP ya publicados de sus materias que no han vencido.");





// Lo dispara GitHub Actions una vez por semana. Si hay token configurado exigimos la cabecera
// X-Sync-Token; sin token el endpoint queda abierto, como estaba antes.
// En Render la variable de entorno puede llamarse "Cartelera__SyncToken" (doble guion bajo,
// que es como .NET mapea la clave anidada) o "CARTELERA_SYNC_TOKEN"; aceptamos las dos.
app.MapPost("/api/notion/sync-cartelera", async (
    NotionPublisherService publisherService,
    IConfiguration config,
    HttpRequest request,
    ILogger<Program> logger) =>
{
    var tokenEsperado = config["Cartelera:SyncToken"] ?? config["CARTELERA_SYNC_TOKEN"];

    if (!string.IsNullOrWhiteSpace(tokenEsperado))
    {
        var tokenRecibido = request.Headers["X-Sync-Token"].ToString();

        if (!string.Equals(tokenRecibido, tokenEsperado, StringComparison.Ordinal))
        {
            logger.LogWarning("🔒 Intento de sincronizar la Cartelera con un token inválido o ausente.");
            return Results.Unauthorized();
        }
    }
    else
    {
        logger.LogWarning("⚠️ 'Cartelera:SyncToken' no está configurado: el endpoint de sincronización está abierto.");
    }

    await publisherService.SincronizarCarteleraAsync();
    return Results.Ok(new { mensaje = "Cartelera sincronizada correctamente con Notion." });
})
.WithTags("8. Sincronización Notion")
.WithSummary("Actualiza la Cartelera Informativa")
.WithDescription("Lee las próximas evaluaciones de Supabase y las publica automáticamente en la base de datos de la Cartelera de Notion.");



// Excel no deja poner links de Drive directo en una celda, así que redirige por acá
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

// Panel de administración - CRM de retención de estudiantes

app.MapPost("/api/admin/crm/convocatoria-semestre", async (PeriodoAcademicoService periodo) =>
{
    try
    {
        var resultado = await periodo.EnviarConvocatoriaAsync();

        if (resultado.Motivo == MotivoFinConvocatoria.YaEnCurso)
            return Results.Conflict(new { mensaje = resultado.Detalle });

        if (resultado.Motivo == MotivoFinConvocatoria.NadaPendiente)
            return Results.Ok(new { mensaje = "No hay estudiantes pendientes de recibir la convocatoria en este periodo." });

        return Results.Ok(new
        {
            mensaje = resultado.Motivo == MotivoFinConvocatoria.Completada
                ? "Convocatoria completada: todos los pendientes la recibieron."
                : "Lote enviado. El resto se envía solo cuando se libere cupo (ConvocatoriaWorkerService).",
            motivo = resultado.Motivo.ToString(),
            pendientesAntes = resultado.PendientesAntes,
            enviosExitosos = resultado.Exitosos,
            enviosFallidos = resultado.Fallidos,
            pendientes = resultado.Restantes,
            enviadosUltimas24h = resultado.EnviadosUltimas24h,
            limiteDiario = resultado.LimiteDiario,
            proximoCupoUtc = resultado.ProximoCupoUtc,
            detalle = resultado.Detalle
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Fallo en la orquestación del envío masivo: {ex.Message}");
    }
})
.WithTags("6. Panel de Administración - CRM")
.WithSummary("1. Enviar aviso masivo de nuevo semestre")
.WithDescription("Envía la convocatoria a los estudiantes con materias vacías que aún no la recibieron (FechaConvocatoria nula), con un tope de Crm:LimiteDiarioConvocatoria (350 por defecto) cada 24 h. Cada envío queda marcado, así que relanzarlo nunca duplica correos; lo que no quepa lo reanuda el ConvocatoriaWorkerService.");


// El ultimátum: avisa a los que llevan el semestre sin materias registradas
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


// Borra de forma permanente a los estudiantes advertidos hace más de 15 días que siguen sin materias
app.MapDelete("/api/admin/crm/purgar-fantasmas", async (AppDbContext db) =>
{
    try
    {
        var fechaLimite = DateTime.UtcNow.AddDays(-15);

        // Si inscribió algo en Notion mientras tanto, ya no entra en el borrado
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

app.MapGet("/api/test-telegram", (ILogger<Program> logger) => 
{
    logger.LogWarning("🤖 Test: El Bot de Telegram de UnaPlan está vivo y reportando desde la base.");
    UnaPlan.Infrastructure.Logging.TelegramLoggerExtensions.LogTelegramBatch(logger, "✅ 3 solicitudes de prueba procesadas", "✅ Juan completado\n❌ Pedro falló\n✅ Maria completado");
    return Results.Ok("Logs de prueba enviados a Telegram. Revisa tu teléfono.");
});



// Disparamos el mensaje de bienvenida de Telegram cuando la app termina de arrancar
app.Lifetime.ApplicationStarted.Register(() =>
{
    var telegramControl = app.Services.GetRequiredService<TelegramControlService>();
    _ = telegramControl.EnviarPanelDeControlAsync();
});

// Webhook de Telegram: recibe los mensajes y los clicks en botones
app.MapPost("/api/telegram/webhook", async (
    [FromBody] Telegram.Bot.Types.Update update,
    TelegramControlService telegramService,
    Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
    IConfiguration config) =>
{
    // Telegram reintenta el mismo update si no recibe 200 a tiempo, así que lo procesamos una sola vez
    // (evita, por ejemplo, disparar dos veces el reinicio de semestre)
    var claveUpdate = $"tg-update:{update.Id}";
    if (cache.TryGetValue(claveUpdate, out _)) return Results.Ok();
    cache.Set(claveUpdate, true, TimeSpan.FromMinutes(10));

    // Mensajes: botones del teclado inferior, respuestas del panel (PIN, datos) o PDF
    if (update.Message != null)
    {
        await telegramService.ProcesarMensajeAsync(update.Message);
    }

    // Click en un botón interactivo
    if (update.CallbackQuery != null)
    {
        // Botones del panel de Periodo Académico (prefijo "pa:")
        if (TelegramControlService.EsCallbackDelPanel(update.CallbackQuery.Data))
        {
            await telegramService.ProcesarCallbackAsync(update.CallbackQuery);
            return Results.Ok();
        }

        // Desgloses del mensaje de estatus (prefijo "es:")
        if (TelegramControlService.EsCallbackDelEstatus(update.CallbackQuery.Data))
        {
            await telegramService.ProcesarCallbackEstatusAsync(update.CallbackQuery);
            return Results.Ok();
        }

        // Detalles de logs: solo administradores
        if (!telegramService.EsAdmin(update.CallbackQuery.From.Id))
            return Results.Ok();

        var callbackId = update.CallbackQuery.Data;
        Console.WriteLine($"[Webhook] Botón presionado con ID: {callbackId}");
        
        if (!string.IsNullOrEmpty(callbackId))
        {
            if (cache.TryGetValue(callbackId, out object? batchDetailsObj) && batchDetailsObj is string batchDetails)
            {
                var botToken = config["Telegram:BotToken"];
                var botClient = new Telegram.Bot.TelegramBotClient(botToken!);

                try
                {
                    // Extraemos el texto original pero lo pasamos a HTML escapando las etiquetas básicas de HTML que manda Telegram si las tuviera (pero en nuestro caso es puro texto)
                    var originalText = update.CallbackQuery.Message?.Text ?? "Detalles Técnicos:";
                    var newText = $"<b>{System.Net.WebUtility.HtmlEncode(originalText)}</b>\n\n{batchDetails}";
                    
                    Console.WriteLine($"[Webhook] Editando mensaje de Telegram...");
                    await botClient.EditMessageText(
                        chatId: update.CallbackQuery.Message!.Chat.Id,
                        messageId: update.CallbackQuery.Message.MessageId,
                        text: newText,
                        parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                        replyMarkup: null // Borramos el botón
                    );
                    Console.WriteLine($"[Webhook] Mensaje editado exitosamente.");
                }
                catch(Exception ex)
                {
                    Console.WriteLine($"[Webhook] Error al editar mensaje: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[Webhook] El ID {callbackId} no está en la memoria caché. Probablemente el servidor se reinició.");
            }
        }
    }

    // Telegram siempre espera un 200 OK para saber que recibimos el mensaje
    return Results.Ok();
})
.ExcludeFromDescription(); // No lo mostramos en Swagger por seguridad


// Registra la URL del webhook en Telegram (se llama una sola vez)
app.MapGet("/api/telegram/register-webhook", async (IConfiguration config) =>
{
    try 
    {
        var botToken = config["Telegram:BotToken"];
        if (string.IsNullOrEmpty(botToken)) return Results.Problem("Telegram:BotToken no está configurado en las variables de entorno.");

        var renderUrl = config["Telegram:RenderUrl"]?.Replace("http://", "https://") ?? "";
        if (string.IsNullOrEmpty(renderUrl)) return Results.Problem("Telegram:RenderUrl no está configurado en las variables de entorno.");

        var botClient = new Telegram.Bot.TelegramBotClient(botToken);
        var webhookUrl = $"{renderUrl}/api/telegram/webhook";

        Console.WriteLine($"Registrando Webhook en: {webhookUrl}");

        await botClient.SetWebhook(webhookUrl);

        return Results.Ok($"Webhook registrado exitosamente en: {webhookUrl}");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error interno al registrar el webhook: {ex.Message}");
    }
})
.ExcludeFromDescription();



app.Run();

// DTOs

public class CatalogoResponseWrapper
{
    public string? Mensaje { get; set; }
    public int? TotalDrivesAnalizados { get; set; }
    public List<UnaPlan.Core.Entities.CatalogoPreviewDto> Datos { get; set; } = new();
}

public class SolicitudPlanDto
{
    public string NombreCompleto { get; set; } = "";
    public string CorreoElectronico { get; set; } = "";
    public string CodigosMaterias { get; set; } = "";
}
