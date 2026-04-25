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

    public async Task EnviarPlanPersonalizadoAsync(string correoDestino, string nombreEstudiante, byte[] archivoExcel, List<string> materiasFaltantes)
    {
        var email = new MimeMessage();

        // 1. Configuramos Remitente y Destinatario
        email.From.Add(new MailboxAddress(
            _config["SmtpSettings:SenderName"],
            _config["SmtpSettings:SenderEmail"]));

        email.To.Add(new MailboxAddress(nombreEstudiante, correoDestino));
        email.Subject = $"🎓 Tu Plan de Evaluación Personalizado UNA - {DateTime.Now.Year}";

        // 2. Creamos la alerta visual si faltan materias (Teoría de Conjuntos)
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

        // 3. Creamos el cuerpo del correo y le inyectamos la advertencia si existe
        var builder = new BodyBuilder();
        builder.HtmlBody = $@"
            <div style='font-family: Arial, sans-serif; color: #333;'>
                <h2>¡Hola, {nombreEstudiante}!</h2>
                <p>Tu solicitud ha sido procesada con éxito. Adjunto a este correo encontrarás tu <strong>Plan de Evaluación Personalizado</strong> en formato Excel.</p>
                
                {advertenciaMaterias}

                <p>Este archivo contiene:</p>
                <ul>
                      <li>Las fechas de entrega extraídas directamente de los Calendario de evaluaciones UNA.</li>
                      <li>Enlaces directos a los PDFs de cada Plan de curso.</li>
                      <li>Enlaces a los materiales de apoyo disponibles.</li>
                </ul>
                <p>¡Mucho éxito en este semestre!</p>
                <br/>
                <small>Este es un correo automatizado generado por el sistema UnaPlan. Por favor no respondas a este mensaje.</small>
            </div>";

        // 4. Adjuntamos el Excel que viene desde la memoria RAM
        builder.Attachments.Add("Mi_Plan_De_Evaluacion_UNA.xlsx", archivoExcel, ContentType.Parse("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));

        email.Body = builder.ToMessageBody();

        // 5. Conectamos al servidor SMTP y enviamos
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
