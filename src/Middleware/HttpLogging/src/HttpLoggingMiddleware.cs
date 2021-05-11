// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.HttpLogging
{
    /// <summary>
    /// Middleware that logs HTTP requests and HTTP responses.
    /// </summary>
    internal sealed class HttpLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger _logger;
        private readonly ILogger _w3cLogger;
        private readonly IOptionsMonitor<HttpLoggingOptions> _options;
        private const int DefaultRequestFieldsMinusHeaders = 7;
        private const int DefaultResponseFieldsMinusHeaders = 2;
        private const string Redacted = "[Redacted]";

        /// <summary>
        /// Initializes <see cref="HttpLoggingMiddleware" />.
        /// </summary>
        /// <param name="next"></param>
        /// <param name="options"></param>
        /// <param name="loggerFactory"></param>
        public HttpLoggingMiddleware(RequestDelegate next, IOptionsMonitor<HttpLoggingOptions> options, ILoggerFactory loggerFactory)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            _options = options;

            _logger = loggerFactory.CreateLogger<HttpLoggingMiddleware>();
            // TODO - change this, maybe proxy type
            _w3cLogger = loggerFactory.CreateLogger("Microsoft.AspNetCore.W3CLogging");
        }

        /// <summary>
        /// Invokes the <see cref="HttpLoggingMiddleware" />.
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>HttpResponseLog.cs
        public Task Invoke(HttpContext context)
        {
            if (!_logger.IsEnabled(LogLevel.Information))
            {
                // Logger isn't enabled.
                return _next(context);
            }

            return InvokeInternal(context);
        }

        private async Task InvokeInternal(HttpContext context)
        {
            Debugger.Launch();
            Debugger.Break();
            var options = _options.CurrentValue;

            var w3cList = new List<KeyValuePair<string, string?>>();

            if (_logger is Microsoft.Extensions.Logging.Logger)
            {

            }

            if (options.LoggingFields.HasFlag(HttpLoggingFields.DateTime))
            {
                AddToList(w3cList, nameof(DateTime), DateTime.Now.ToString(CultureInfo.InvariantCulture));
            }

            if ((HttpLoggingFields.ConnectionInfoFields & options.LoggingFields) != HttpLoggingFields.None)
            {
                var connectionInfo = context.Connection;

                if (options.LoggingFields.HasFlag(HttpLoggingFields.ClientIpAddress))
                {
                    AddToList(w3cList, nameof(ConnectionInfo.RemoteIpAddress), connectionInfo.RemoteIpAddress is null ? "" : connectionInfo.RemoteIpAddress.ToString());
                }

                if (options.LoggingFields.HasFlag(HttpLoggingFields.ServerIpAddress))
                {
                    AddToList(w3cList, nameof(ConnectionInfo.LocalIpAddress), connectionInfo.LocalIpAddress is null ? "" : connectionInfo.LocalIpAddress.ToString());
                }

                if (options.LoggingFields.HasFlag(HttpLoggingFields.ServerPort))
                {
                    AddToList(w3cList, nameof(ConnectionInfo.LocalPort), connectionInfo.LocalPort.ToString(CultureInfo.InvariantCulture));
                }
            }

            RequestBufferingStream ? requestBufferingStream = null;
            Stream? originalBody = null;

            if ((HttpLoggingFields.Request & options.LoggingFields) != HttpLoggingFields.None)
            {
                var request = context.Request;
                var list = new List<KeyValuePair<string, string?>>(
                    request.Headers.Count + DefaultRequestFieldsMinusHeaders);

                if (options.LoggingFields.HasFlag(HttpLoggingFields.RequestProtocol))
                {
                    AddToList(list, nameof(request.Protocol), request.Protocol);
                }

                if (options.LoggingFields.HasFlag(HttpLoggingFields.RequestMethod))
                {
                    AddToList(list, nameof(request.Method), request.Method);
                }

                if (options.LoggingFields.HasFlag(HttpLoggingFields.RequestScheme))
                {
                    AddToList(list, nameof(request.Scheme), request.Scheme);
                }

                if (options.LoggingFields.HasFlag(HttpLoggingFields.RequestPath))
                {
                    AddToList(list, nameof(request.PathBase), request.PathBase);
                    AddToList(list, nameof(request.Path), request.Path);
                }

                if (options.LoggingFields.HasFlag(HttpLoggingFields.RequestQuery))
                {
                    AddToList(list, nameof(request.QueryString), request.QueryString.Value);
                }

                if (options.LoggingFields.HasFlag(HttpLoggingFields.RequestHeaders))
                {
                    FilterHeaders(list, request.Headers, options._internalRequestHeaders);
                }

                if (options.LoggingFields.HasFlag(HttpLoggingFields.RequestBody))
                {
                    if (MediaTypeHelpers.TryGetEncodingForMediaType(request.ContentType,
                        options.MediaTypeOptions.MediaTypeStates,
                        out var encoding))
                    {
                        originalBody = request.Body;
                        requestBufferingStream = new RequestBufferingStream(
                            request.Body,
                            options.RequestBodyLogLimit,
                            _logger,
                            encoding);
                        request.Body = requestBufferingStream;
                    }
                    else
                    {
                        _logger.UnrecognizedMediaType();
                    }
                }

                var httpRequestLog = new HttpRequestLog(list);

                _logger.RequestLog(httpRequestLog);
            }

            ResponseBufferingStream? responseBufferingStream = null;
            IHttpResponseBodyFeature? originalBodyFeature = null;

            try
            {
                var response = context.Response;

                if (options.LoggingFields.HasFlag(HttpLoggingFields.ResponseBody))
                {
                    originalBodyFeature = context.Features.Get<IHttpResponseBodyFeature>()!;

                    // TODO pool these.
                    responseBufferingStream = new ResponseBufferingStream(originalBodyFeature,
                        options.ResponseBodyLogLimit,
                        _logger,
                        context,
                        options.MediaTypeOptions.MediaTypeStates,
                        options);
                    response.Body = responseBufferingStream;
                    context.Features.Set<IHttpResponseBodyFeature>(responseBufferingStream);
                }

                await _next(context);

                if (requestBufferingStream?.HasLogged == false)
                {
                    // If the middleware pipeline didn't read until 0 was returned from readasync,
                    // make sure we log the request body.
                    requestBufferingStream.LogRequestBody();
                }

                if (responseBufferingStream == null || responseBufferingStream.FirstWrite == false)
                {
                    // No body, write headers here.
                    LogResponseHeaders(response, options, _logger);
                }

                if (responseBufferingStream != null)
                {
                    var responseBody = responseBufferingStream.GetString(responseBufferingStream.Encoding);
                    if (!string.IsNullOrEmpty(responseBody))
                    {
                        _logger.ResponseBody(responseBody);
                    }
                }
                if (w3cList.Count > 0)
                {
                    var httpW3CLog = new HttpW3CLog(w3cList);
                    _logger.W3CLog(httpW3CLog);
                }
            }
            finally
            {
                responseBufferingStream?.Dispose();

                if (originalBodyFeature != null)
                {
                    context.Features.Set(originalBodyFeature);
                }

                requestBufferingStream?.Dispose();

                if (originalBody != null)
                {
                    context.Request.Body = originalBody;
                }
            }
        }

        private static void AddToList(List<KeyValuePair<string, string?>> list, string key, string? value)
        {
            list.Add(new KeyValuePair<string, string?>(key, value));
        }

        public static void LogResponseHeaders(HttpResponse response, HttpLoggingOptions options, ILogger logger)
        {
            var list = new List<KeyValuePair<string, string?>>(
                response.Headers.Count + DefaultResponseFieldsMinusHeaders);

            if (options.LoggingFields.HasFlag(HttpLoggingFields.ResponseStatusCode))
            {
                list.Add(new KeyValuePair<string, string?>(nameof(response.StatusCode),
                    response.StatusCode.ToString(CultureInfo.InvariantCulture)));
            }

            if (options.LoggingFields.HasFlag(HttpLoggingFields.ResponseHeaders))
            {
                FilterHeaders(list, response.Headers, options._internalResponseHeaders);
            }

            var httpResponseLog = new HttpResponseLog(list);

            logger.ResponseLog(httpResponseLog);
        }

        internal static void FilterHeaders(List<KeyValuePair<string, string?>> keyValues,
            IHeaderDictionary headers,
            HashSet<string> allowedHeaders)
        {
            foreach (var (key, value) in headers)
            {
                if (!allowedHeaders.Contains(key))
                {
                    // Key is not among the "only listed" headers.
                    keyValues.Add(new KeyValuePair<string, string?>(key, Redacted));
                    continue;
                }
                keyValues.Add(new KeyValuePair<string, string?>(key, value.ToString()));
            }
        }
    }
}
