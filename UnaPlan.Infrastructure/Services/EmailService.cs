using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;

namespace UnaPlan.Infrastructure.Services;

public class EmailService
{
    private readonly IConfiguration _config;

    public EmailService(IConfiguration config)
    {
        _config = config;
    }

    public async Task EnviarPlanPersonalizadoAsync(string correoDestino, string nombreEstudiante, byte[] archivoExcel)
    {
        var email = new MimeMessage();

        // 1. Configuramos Remitente y Destinatario
        email.From.Add(new MailboxAddress(
            _config["SmtpSettings:SenderName"],
            _config["SmtpSettings:SenderEmail"]));

        email.To.Add(new MailboxAddress(nombreEstudiante, correoDestino));
        email.Subject = $"🎓 Tu Plan de Evaluación Personalizado UNA - {DateTime.Now.Year}";

        // 2. Creamos el cuerpo del correo
        var builder = new BodyBuilder();
        builder.HtmlBody = $@"
            <div style='font-family: Arial, sans-serif; color: #333;'>
                <h2>¡Hola, {nombreEstudiante}!</h2>
                <p>Tu solicitud ha sido procesada con éxito. Adjunto a este correo encontrarás tu <strong>Plan de Evaluación Personalizado</strong> en formato Excel.</p>
                <p>Este archivo contiene:</p>
                <ul>
                    <li>Las fechas de entrega extraídas directamente de los Planes de Curso oficiales.</li>
                    <li>Enlaces directos a los PDFs de cada materia.</li>
                    <li>Enlaces a los materiales de apoyo disponibles.</li>
                </ul>
                <p>¡Mucho éxito en este semestre!</p>
                <br/>
                <small>Este es un correo automatizado generado por el sistema UnaPlan.</small>
            </div>";

        // 3. Adjuntamos el Excel que viene desde la memoria RAM
        builder.Attachments.Add("Mi_Plan_De_Evaluacion_UNA.xlsx", archivoExcel, ContentType.Parse("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));

        email.Body = builder.ToMessageBody();

        // 4. Conectamos al servidor SMTP y enviamos
        using var smtp = new SmtpClient();
        try
        {
            await smtp.ConnectAsync(_config["SmtpSettings:Host"], int.Parse(_config["SmtpSettings:Port"]!), SecureSocketOptions.StartTls);
            await smtp.AuthenticateAsync(_config["SmtpSettings:SenderEmail"], _config["SmtpSettings:Password"]);
            await smtp.SendAsync(email);
        }
        finally
        {
            await smtp.DisconnectAsync(true);
        }
    }
}