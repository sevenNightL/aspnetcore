using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.Logging.W3C
{
    public static class W3CLoggerFactoryExtensions
    {
        public static ILoggingBuilder AddW3CLogger(this ILoggingBuilder builder)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, W3CLoggerProvider>());
            builder.AddFilter((category, provider, logLevel) =>
            {
                return (category.Equals("Microsoft.AspNetCore.W3CLogging") && provider.Equals("Microsoft.Aspnetcore.W3CLoggerProvider") && logLevel >= LogLevel.Information);
            });
            return builder;
        }
    }
}
