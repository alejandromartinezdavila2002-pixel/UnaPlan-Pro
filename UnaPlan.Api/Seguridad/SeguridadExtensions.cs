namespace UnaPlan.Api.Seguridad;

public static class SeguridadExtensions
{
    /// <summary>Registra las claves (leídas una sola vez al arrancar) y el contador de intentos fallidos.</summary>
    public static IServiceCollection AddSeguridadUnaPlan(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(SeguridadOptions.Desde(config));
        services.AddSingleton<LimiteIntentos>();
        return services;
    }

    /// <summary>
    /// Falla cerrado: fuera de Development, si falta una clave obligatoria la API no arranca.
    /// En Render eso hace que el deploy falle y siga corriendo la versión anterior, en vez de
    /// publicar una API abierta. En Development solo se avisa por consola.
    /// </summary>
    public static void ValidarSeguridad(this WebApplication app)
    {
        var opciones = app.Services.GetRequiredService<SeguridadOptions>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Seguridad");

        foreach (var aviso in opciones.Avisos)
            logger.LogWarning($"⚠️ Seguridad: {aviso}");

        if (opciones.Errores.Count == 0) return;

        var detalle = string.Join(Environment.NewLine, opciones.Errores.Select(e => $" - {e}"));

        if (app.Environment.IsDevelopment())
        {
            Console.WriteLine($"⚠️ Configuración de seguridad incompleta (en producción la API no arrancaría):{Environment.NewLine}{detalle}");
            return;
        }

        throw new InvalidOperationException($"Configuración de seguridad incompleta:{Environment.NewLine}{detalle}");
    }

    /// <summary>Usuario y contraseña para /swagger. Debe ir antes de UseSwagger y UseSwaggerUI.</summary>
    public static IApplicationBuilder UseSwaggerProtegido(this IApplicationBuilder app) =>
        app.UseMiddleware<SwaggerBasicAuthMiddleware>();

    /// <summary>X-Api-Key en todos los endpoints no marcados con SinApiKey(). Debe ir después de UseRouting.</summary>
    public static IApplicationBuilder UseApiKey(this IApplicationBuilder app) =>
        app.UseMiddleware<ApiKeyMiddleware>();

    /// <summary>Deja este endpoint abierto, sin X-Api-Key. Úsalo solo con motivo y explícalo en un comentario.</summary>
    public static TBuilder SinApiKey<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(SinApiKeyMetadata.Instancia);
}
