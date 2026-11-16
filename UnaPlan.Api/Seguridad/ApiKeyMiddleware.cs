namespace UnaPlan.Api.Seguridad;

/// <summary>
/// Marca un endpoint como público (no exige X-Api-Key). Se pone con <c>.SinApiKey()</c>.
/// Todo lo que NO lo tenga queda protegido por defecto: un endpoint nuevo nace cerrado.
/// </summary>
public sealed class SinApiKeyMetadata
{
    public static readonly SinApiKeyMetadata Instancia = new();
}

/// <summary>
/// Exige la cabecera X-Api-Key en todos los endpoints de la API, salvo los marcados con <c>.SinApiKey()</c>.
/// Decide por endpoint (después de UseRouting), no por texto de la URL: así no hay forma de colarse
/// con mayúsculas, barras dobles o codificaciones raras.
/// Una clave válida siempre pasa, aunque su IP esté bloqueada: la clave tiene 256 bits y no se puede
/// adivinar, y así un atacante no puede bloquear a GitHub Actions compartiendo IP con él.
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, SeguridadOptions opciones, LimiteIntentos limite)
{
    public const string Cabecera = "X-Api-Key";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var endpoint = ctx.GetEndpoint();

        // Sin endpoint (ruta inexistente → 404) o endpoint público: no hay nada que proteger.
        if (endpoint == null || endpoint.Metadata.GetMetadata<SinApiKeyMetadata>() != null)
        {
            await next(ctx);
            return;
        }

        if (opciones.ApiKeyValida(ctx.Request.Headers[Cabecera].ToString()))
        {
            await next(ctx);
            return;
        }

        var ip = LimiteIntentos.IpCliente(ctx);

        if (limite.EstaBloqueada(ip))
        {
            await RespuestasSeguridad.DemasiadosIntentosAsync(ctx);
            return;
        }

        limite.RegistrarFallo(ip, $"{ctx.Request.Method} {ctx.Request.Path} sin clave válida");
        await RespuestasSeguridad.NoAutorizadoAsync(ctx);
    }
}

public static class RespuestasSeguridad
{
    public static Task NoAutorizadoAsync(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return ctx.Response.WriteAsJsonAsync(new { error = "No autorizado." });
    }

    public static Task DemasiadosIntentosAsync(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.Response.Headers.RetryAfter = ((int)LimiteIntentos.Ventana.TotalSeconds).ToString();
        return ctx.Response.WriteAsJsonAsync(new { error = "Demasiados intentos fallidos. Intenta más tarde." });
    }
}
