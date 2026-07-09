using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System;

namespace UnaPlan.Infrastructure.Logging;

public static class TelegramLoggerExtensions
{
    public static ILoggingBuilder AddTelegramBot(this ILoggingBuilder builder, Action<TelegramLoggerOptions> configure)
    {
        // Registramos el proveedor de Telegram
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, TelegramLoggerProvider>());
        
        // Configuramos las opciones inyectadas
        builder.Services.Configure(configure);

        return builder;
    }

    public static void LogTelegramBatch(this ILogger logger, string summary, string details)
    {
        // Codificamos el mensaje con un prefijo para que el Logger lo intercepte
        logger.LogWarning(new EventId(999, "TelegramBatch"), "{Summary}|BATCH_DETAILS|{Details}", summary, details);
    }
}
