using Microsoft.Extensions.Caching.Memory;

namespace UnaPlan.Api.Seguridad;

/// <summary>
/// Cuenta los intentos fallidos (clave de API o contraseña de Swagger) por IP. Con 10 fallos en
/// 15 minutos la IP queda bloqueada hasta que vence la ventana. Vive en memoria: un reinicio de
/// Render lo limpia, y está bien así (no hace falta guardar nada en Supabase).
/// Cada fallo va al log como Information; solo el bloqueo sube a Warning (y por lo tanto a Telegram),
/// para no llenar el chat si alguien se pone a probar claves.
/// </summary>
public sealed class LimiteIntentos(IMemoryCache cache, ILogger<LimiteIntentos> logger)
{
    public const int MaximoFallos = 10;
    public static readonly TimeSpan Ventana = TimeSpan.FromMinutes(15);

    private readonly object _candado = new();

    public bool EstaBloqueada(string ip) =>
        cache.TryGetValue(Clave(ip), out Contador? contador) && contador!.Fallos >= MaximoFallos;

    public void RegistrarFallo(string ip, string que)
    {
        int fallos;
        lock (_candado)
        {
            var contador = cache.GetOrCreate(Clave(ip), entrada =>
            {
                entrada.AbsoluteExpirationRelativeToNow = Ventana;
                return new Contador();
            })!;
            fallos = ++contador.Fallos;
        }

        if (fallos == MaximoFallos)
            logger.LogWarning($"🔒 IP {ip} bloqueada {Ventana.TotalMinutes:0} min tras {MaximoFallos} intentos fallidos (último: {que}).");
        else
            logger.LogInformation($"🔒 Intento fallido {fallos}/{MaximoFallos} desde {ip}: {que}");
    }

    private static string Clave(string ip) => $"seguridad:fallos:{ip}";

    private sealed class Contador
    {
        public int Fallos;
    }

    /// <summary>
    /// IP real del cliente. En Render siempre hay Cloudflare delante, y Cloudflare reescribe CF-Connecting-IP
    /// con la IP que vio (el cliente no puede falsificarla). True-Client-IP queda de respaldo.
    /// X-Forwarded-For NO se usa: Render no lo limpia, solo le agrega, así que el cliente podría
    /// inventarse una IP distinta en cada intento y saltarse el límite.
    /// </summary>
    public static string IpCliente(HttpContext ctx)
    {
        var ip = ctx.Request.Headers["CF-Connecting-IP"].ToString();
        if (string.IsNullOrWhiteSpace(ip)) ip = ctx.Request.Headers["True-Client-IP"].ToString();
        if (string.IsNullOrWhiteSpace(ip)) ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "desconocida";
        return ip.Trim();
    }
}
