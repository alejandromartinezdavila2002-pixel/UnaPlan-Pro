using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace UnaPlan.Api.Seguridad;

/// <summary>
/// Claves de seguridad de la API. Todas vienen de variables de entorno (Render) o de user-secrets en local:
///   Seguridad__ApiKeys           claves de API (X-Api-Key), separadas por coma para poder rotarlas
///   Seguridad__SwaggerUsuario    usuario de Swagger
///   Seguridad__SwaggerClaveHash  SHA-256 en hexadecimal de la contraseña de Swagger (nunca la contraseña en claro)
///   Telegram__WebhookSecret      secreto que Telegram manda en X-Telegram-Bot-Api-Secret-Token
/// Las claves de API se guardan en memoria ya convertidas a SHA-256: así todas las comparaciones son
/// de 32 bytes contra 32 bytes, en tiempo constante, sin revelar el largo de la clave real.
/// </summary>
public sealed partial class SeguridadOptions
{
    public const int LargoMinimoApiKey = 32;

    public IReadOnlyList<byte[]> ApiKeysHash { get; private init; } = [];
    public string? SwaggerUsuario { get; private init; }
    public byte[]? SwaggerClaveHash { get; private init; }
    public string? TelegramWebhookSecret { get; private init; }

    /// <summary>Errores de configuración: en producción impiden que la API arranque (falla cerrado).</summary>
    public IReadOnlyList<string> Errores { get; private init; } = [];

    /// <summary>Avisos que no detienen el arranque (por ejemplo, Swagger sin contraseña queda bloqueado).</summary>
    public IReadOnlyList<string> Avisos { get; private init; } = [];

    public bool SwaggerConfigurado => !string.IsNullOrEmpty(SwaggerUsuario) && SwaggerClaveHash != null;

    public static SeguridadOptions Desde(IConfiguration config)
    {
        var errores = new List<string>();
        var avisos = new List<string>();

        // Claves de API
        var claves = (config["Seguridad:ApiKeys"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (claves.Count == 0)
            errores.Add("Falta 'Seguridad:ApiKeys' (variable Seguridad__ApiKeys): sin ella ningún endpoint protegido responde.");

        if (claves.Any(c => c.Length < LargoMinimoApiKey))
            errores.Add($"Cada clave de 'Seguridad:ApiKeys' debe tener al menos {LargoMinimoApiKey} caracteres.");

        // Swagger
        var usuario = config["Seguridad:SwaggerUsuario"]?.Trim();
        var hashTexto = config["Seguridad:SwaggerClaveHash"]?.Trim();
        byte[]? hash = null;

        if (!string.IsNullOrEmpty(hashTexto))
        {
            if (hashTexto.Length == 64 && EsHexadecimal().IsMatch(hashTexto))
                hash = Convert.FromHexString(hashTexto);
            else
                errores.Add("'Seguridad:SwaggerClaveHash' debe ser un SHA-256 en hexadecimal (64 caracteres).");
        }

        if (string.IsNullOrEmpty(usuario) || hash == null)
            avisos.Add("Swagger no tiene usuario o contraseña configurados: queda bloqueado para todos.");

        // Webhook de Telegram
        var botToken = config["Telegram:BotToken"];
        var secretoWebhook = config["Telegram:WebhookSecret"]?.Trim();

        if (!string.IsNullOrEmpty(secretoWebhook) && !FormatoSecretoTelegram().IsMatch(secretoWebhook))
            errores.Add("'Telegram:WebhookSecret' solo admite letras, números, '_' y '-' (entre 32 y 256 caracteres).");

        if (!string.IsNullOrEmpty(botToken) && string.IsNullOrEmpty(secretoWebhook))
            errores.Add("Falta 'Telegram:WebhookSecret' (variable Telegram__WebhookSecret): sin él cualquiera podría hacerse pasar por Telegram.");

        return new SeguridadOptions
        {
            ApiKeysHash = claves.Select(Sha256).ToList(),
            SwaggerUsuario = string.IsNullOrEmpty(usuario) ? null : usuario,
            SwaggerClaveHash = hash,
            TelegramWebhookSecret = string.IsNullOrEmpty(secretoWebhook) ? null : secretoWebhook,
            Errores = errores,
            Avisos = avisos
        };
    }

    /// <summary>¿La clave recibida coincide con alguna de las configuradas? Siempre recorre todas (tiempo constante).</summary>
    public bool ApiKeyValida(string? recibida)
    {
        if (string.IsNullOrEmpty(recibida) || ApiKeysHash.Count == 0) return false;

        var hashRecibido = Sha256(recibida);
        var coincide = false;
        foreach (var esperado in ApiKeysHash)
            coincide |= CryptographicOperations.FixedTimeEquals(hashRecibido, esperado);

        return coincide;
    }

    /// <summary>Usuario y contraseña de Swagger. Se comparan los dos aunque el usuario falle, para no dar pistas por tiempo.</summary>
    public bool CredencialesSwaggerValidas(string usuario, string clave)
    {
        if (!SwaggerConfigurado) return false;

        var usuarioOk = CryptographicOperations.FixedTimeEquals(Sha256(usuario), Sha256(SwaggerUsuario!));
        var claveOk = CryptographicOperations.FixedTimeEquals(Sha256(clave), SwaggerClaveHash!);
        return usuarioOk & claveOk;
    }

    /// <summary>¿La cabecera que manda Telegram coincide con el secreto con el que se registró el webhook?</summary>
    public bool SecretoTelegramValido(string? recibido)
    {
        if (string.IsNullOrEmpty(recibido) || TelegramWebhookSecret == null) return false;
        return CryptographicOperations.FixedTimeEquals(Sha256(recibido), Sha256(TelegramWebhookSecret));
    }

    private static byte[] Sha256(string texto) => SHA256.HashData(Encoding.UTF8.GetBytes(texto));

    [GeneratedRegex("^[0-9a-fA-F]+$")]
    private static partial Regex EsHexadecimal();

    // Telegram exige 1-256 caracteres A-Z, a-z, 0-9, _ y -. Pedimos al menos 32 para que no sea adivinable.
    [GeneratedRegex("^[A-Za-z0-9_-]{32,256}$")]
    private static partial Regex FormatoSecretoTelegram();
}
