using System.Net.Http.Headers;
using System.Text;

namespace UnaPlan.Api.Seguridad;

/// <summary>
/// Pide usuario y contraseña (la ventanita nativa del navegador, HTTP Basic) para todo lo que empiece
/// por /swagger: la página y el swagger.json con la lista de endpoints.
/// La contraseña recibida se pasa a SHA-256 y se compara con Seguridad__SwaggerClaveHash.
/// Aquí el bloqueo por IP sí se aplica antes de mirar la contraseña: es una contraseña escrita por
/// una persona y sí se podría adivinar probando muchas.
/// Va antes de UseSwagger/UseSwaggerUI, que son middlewares y no endpoints.
/// </summary>
public sealed class SwaggerBasicAuthMiddleware(RequestDelegate next, SeguridadOptions opciones, LimiteIntentos limite)
{
    private const string Desafio = "Basic realm=\"UnaPlan Swagger\", charset=\"UTF-8\"";

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!ctx.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase))
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

        var credenciales = LeerCredenciales(ctx.Request.Headers.Authorization.ToString());

        if (credenciales is { } c && opciones.CredencialesSwaggerValidas(c.Usuario, c.Clave))
        {
            await next(ctx);
            return;
        }

        // Sin cabecera es la primera visita (el navegador aún no pidió los datos): no cuenta como fallo.
        if (credenciales != null)
            limite.RegistrarFallo(ip, "contraseña de Swagger incorrecta");

        ctx.Response.Headers.WWWAuthenticate = Desafio;
        await RespuestasSeguridad.NoAutorizadoAsync(ctx);
    }

    private static (string Usuario, string Clave)? LeerCredenciales(string cabecera)
    {
        if (!AuthenticationHeaderValue.TryParse(cabecera, out var valor) ||
            !"Basic".Equals(valor.Scheme, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(valor.Parameter))
            return null;

        try
        {
            var texto = Encoding.UTF8.GetString(Convert.FromBase64String(valor.Parameter));
            var separador = texto.IndexOf(':');
            if (separador < 0) return ("", "");
            return (texto[..separador], texto[(separador + 1)..]);
        }
        catch (FormatException)
        {
            return ("", ""); // Cabecera mal formada: cuenta como intento fallido
        }
    }
}
