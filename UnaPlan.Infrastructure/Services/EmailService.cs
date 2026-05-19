using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Microsoft.Extensions.Configuration;
using MimeKit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace UnaPlan.Infrastructure.Services;

public class EmailService
{
    private readonly IConfiguration _config;

    public EmailService(IConfiguration config)
    {
        _config = config;
    }

    // =========================================================================
    // 🚀 NUEVO MOTOR CENTRAL: Se encarga de la autorización OAuth2 y el envío HTTP
    // =========================================================================
    private async Task EnviarConGmailApiAsync(MimeMessage email)
    {
        // 1. Configuramos las credenciales extraídas de tu secrets.json (o variables de entorno en Render)
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = _config["GmailApi:ClientId"],
                ClientSecret = _config["GmailApi:ClientSecret"]
            },
            Scopes = new[] { GmailService.Scope.GmailSend }
        });

        var credential = new UserCredential(flow, "user", new TokenResponse
        {
            RefreshToken = _config["GmailApi:RefreshToken"]
        });

        // 2. Iniciamos el cliente HTTP de Gmail
        using var service = new GmailService(new BaseClientService.Initializer()
        {
            HttpClientInitializer = credential,
            ApplicationName = "UnaPlan"
        });

        // 3. Convertimos el correo a Base64Url (El estándar RFC 2822 que exige Google)
        using var memoryStream = new MemoryStream();
        await email.WriteToAsync(memoryStream);

        var base64Url = Convert.ToBase64String(memoryStream.ToArray())
            .Replace('+', '-')
            .Replace('/', '_')
            .Replace("=", "");

        var message = new Google.Apis.Gmail.v1.Data.Message { Raw = base64Url };

        // 4. Despachamos el correo por el puerto 443 (HTTPS)
        await service.Users.Messages.Send(message, "me").ExecuteAsync();
    }

    // =========================================================================

    public async Task EnviarPlanPersonalizadoAsync(string correoDestino, string nombreEstudiante, byte[] archivoExcel, List<string> materiasFaltantes)
    {
        var email = new MimeMessage();
        email.From.Add(new MailboxAddress(_config["SmtpSettings:SenderName"], _config["SmtpSettings:SenderEmail"]));
        email.To.Add(new MailboxAddress(nombreEstudiante, correoDestino));
        email.Subject = $"🎓 Tu Plan de Evaluación Personalizado UNA - {DateTime.Now.Year}";

        string advertenciaMaterias = "";
        if (materiasFaltantes != null && materiasFaltantes.Any())
        {
            string codigosPerdidos = string.Join(", ", materiasFaltantes);
            advertenciaMaterias = $@"
                <div style='background-color: #ffebee; color: #c62828; padding: 15px; border-radius: 5px; margin-top: 20px; border-left: 5px solid #d32f2f;'>
                    <strong>⚠️ Nota Importante:</strong> No pudimos encontrar el plan de evaluación ni el material para las siguientes materias: <b>{codigosPerdidos}</b>. <br>
                    Es posible que aún no estén en nuestra base de datos oficial o el código ingresado sea incorrecto.
                </div>";
        }

        var builder = new BodyBuilder();
        builder.HtmlBody = $@"
            <div style='font-family: Arial, sans-serif; color: #333;'>
                <h2>¡Hola, {nombreEstudiante}!</h2>
                <p>Tu solicitud ha sido procesada con éxito. Adjunto a este correo encontrarás tu <strong>Plan de Evaluación Personalizado</strong> en formato Excel.</p>
                {advertenciaMaterias}
                <p>Este archivo contiene:</p>
                <ul>
                    <li>Las fechas de entrega extraídas directamente de los Calendario de Evaluaciones UNA.</li>
                    <li>Enlaces directos a los PDFs de cada Plan de curso.</li>
                    <li>Enlaces a los materiales de apoyo disponibles.</li>
                </ul>
                <p>¡Mucho éxito en este semestre!</p>
                <br/>
                <small>Este es un correo automatizado generado por el sistema UnaPlan. Por favor no respondas a este mensaje.</small>
            </div>";

        builder.Attachments.Add("Mi_Plan_De_Evaluacion_UNA.xlsx", archivoExcel, ContentType.Parse("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        email.Body = builder.ToMessageBody();

        // Llamamos al nuevo motor seguro
        await EnviarConGmailApiAsync(email);
    }

    public async Task EnviarNotificacionTrabajoPublicadoAsync(string destinatario, string nombreEstudiante, string codigoMateria, string tipo, DateTime fechaEntrega, string urlDrive)
    {
        var email = new MimeMessage();
        email.From.Add(new MailboxAddress(_config["SmtpSettings:SenderName"], _config["SmtpSettings:SenderEmail"]));
        email.To.Add(new MailboxAddress(nombreEstudiante, destinatario));
        email.Subject = $"🚨 ¡Tu {tipo} de la materia {codigoMateria} ya está disponible!";

        string cuerpoHtml = $@"
        <div style='font-family: Arial, sans-serif; color: #333; max-width: 600px; margin: auto; border: 1px solid #ddd; border-radius: 10px; overflow: hidden;'>
            <div style='background-color: #0056b3; padding: 20px; text-align: center;'>
                <h2 style='color: white; margin: 0;'>Alerta Automática UNA</h2>
            </div>
            <div style='padding: 20px;'>
                <p>Hola <strong>{nombreEstudiante}</strong>,</p>
                <p>El sistema automatizado de UnaPlan ha detectado que la universidad acaba de publicar un nuevo trabajo de tus materias inscritas:</p>
                <div style='background-color: #f8f9fa; padding: 15px; border-left: 4px solid #28a745; margin: 20px 0;'>
                    <h3 style='margin: 0 0 10px 0;'>Materia: {codigoMateria}</h3>
                    <p style='margin: 5px 0;'><strong>Tipo de Evaluación:</strong> {tipo}</p>
                    <p style='margin: 5px 0;'><strong>Fecha Límite de Entrega:</strong> {fechaEntrega:dd/MM/yyyy}</p>
                </div>
                <div style='text-align: center; margin: 30px 0;'>
                    <a href='{urlDrive}' style='background-color: #28a745; color: white; padding: 12px 25px; text-decoration: none; border-radius: 5px; font-weight: bold; font-size: 16px;'>📄 Ver Documento Oficial en Drive</a>
                </div>
                <p style='font-size: 12px; color: #777; text-align: center;'>Recibes este correo porque estás suscrito a las alertas de UnaPlan.</p>
            </div>
        </div>";

        var builder = new BodyBuilder { HtmlBody = cuerpoHtml };
        email.Body = builder.ToMessageBody();

        // Llamamos al nuevo motor seguro
        await EnviarConGmailApiAsync(email);
    }
}