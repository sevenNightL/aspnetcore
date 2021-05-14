using System;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.HttpLogging;
using System.Diagnostics;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using System.Collections.Specialized;
using System.Numerics;
using System.Text;
using Microsoft.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Globalization;

namespace Microsoft.Extensions.Logging.W3C
{
    internal sealed class W3CLogger : ILogger
    {
        private readonly string _name;
        private readonly W3CLoggerProcessor _messageQueue;
        private readonly IOptionsMonitor<W3CLoggerOptions> _options;
        private readonly bool _isActive;
        private readonly W3CLoggingFields _loggingFields;

        internal W3CLogger(string name, IOptionsMonitor<W3CLoggerOptions> options)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            _name = name;
            _options = options;

            // If the info isn't coming from HttpLoggingMiddleware, no-op (don't log anything)
            if (name == "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware")
            {
                _isActive = true;
                _messageQueue = new W3CLoggerProcessor(_options);
                _loggingFields = _options.CurrentValue.LoggingFields;
            }
        }

        // TODO - do we need to do anything here?
        public IDisposable BeginScope<TState>(TState state)
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            // No-op unless log is enabled, listening to HttpLoggingMiddleware, and event is W3CLog
            if (!IsEnabled(logLevel) || !_isActive || !eventId.Equals(new EventId(7, "W3CLog")))
            {
                return;
            }

            System.Console.WriteLine(eventId.Name);
            System.Console.WriteLine(eventId.Id);

            if (state is IReadOnlyCollection<KeyValuePair<string, object>> statePropertyObjects)
            {
                List<KeyValuePair<string, string>> asStrings = new List<KeyValuePair<string, string>>();
                foreach (KeyValuePair<string, object> kvp in statePropertyObjects)
                {
                    asStrings.Add(new KeyValuePair<string, string>(kvp.Key, kvp.Value.ToString()));
                }
                _messageQueue.EnqueueMessage(Format(asStrings));
            }
            else if (state is IReadOnlyCollection<KeyValuePair<string, string>> statePropertyStrings)
            {
                _messageQueue.EnqueueMessage(Format(statePropertyStrings));
            }
        }

        private string Format(IEnumerable<KeyValuePair<string, string>> stateProperties)
        {
            // Subtract 1 to account for the "All" flag
            string[] elements = new string[Enum.GetValues(typeof(W3CLoggingFields)).Length - 1];
            foreach(KeyValuePair<string, string> kvp in stateProperties)
            {
                switch (kvp.Key)
                {
                    case nameof(HttpRequest.Method):
                        elements[BitOperations.Log2((int)W3CLoggingFields.Method)] = kvp.Value.Trim();
                        break;
                    case nameof(HttpRequest.Query):
                        elements[BitOperations.Log2((int)W3CLoggingFields.UriQuery)] = kvp.Value.Trim();
                        break;
                    case nameof(HttpResponse.StatusCode):
                        elements[BitOperations.Log2((int)W3CLoggingFields.ProtocolStatus)] = kvp.Value.Trim();
                        break;
                    case nameof(HttpRequest.Protocol):
                        elements[BitOperations.Log2((int)W3CLoggingFields.ProtocolVersion)] = kvp.Value.Trim();
                        break;
                    case nameof(HttpRequest.Host):
                        elements[BitOperations.Log2((int)W3CLoggingFields.Host)] = kvp.Value.Trim();
                        break;
                    case "User-Agent":
                        // User-Agent can have whitespace - we replace whitespace characters with the '+' character
                        elements[BitOperations.Log2((int)W3CLoggingFields.UserAgent)] = Regex.Replace(kvp.Value.Trim(), @"\s", "+");
                        break;
                    case nameof(DateTime):
                        DateTime dto = DateTime.Parse(kvp.Value, CultureInfo.InvariantCulture);
                        elements[BitOperations.Log2((int)W3CLoggingFields.Date)] = dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        elements[BitOperations.Log2((int)W3CLoggingFields.Time)] = dto.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                        break;
                    case nameof(ConnectionInfo.RemoteIpAddress):
                        elements[BitOperations.Log2((int)W3CLoggingFields.ClientIpAddress)] = kvp.Value.Trim();
                        break;
                    case nameof(ConnectionInfo.LocalIpAddress):
                        elements[BitOperations.Log2((int)W3CLoggingFields.ServerIpAddress)] = kvp.Value.Trim();
                        break;
                    case nameof(ConnectionInfo.LocalPort):
                        elements[BitOperations.Log2((int)W3CLoggingFields.ServerPort)] = kvp.Value.Trim();
                        break;
                    default:
                        break;
                }
            }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < elements.Length; i++)
            {
                if (_loggingFields.HasFlag((W3CLoggingFields)(1 << i)))
                {
                    if (String.IsNullOrEmpty(elements[i]))
                    {
                        sb.Append("- ");
                    }
                    else
                    {
                        sb.Append(elements[i] + " ");
                    }
                }
            }
            return sb.ToString().Trim();
        }
    }
}
