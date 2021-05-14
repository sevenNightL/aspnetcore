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
        private readonly ILogger _httpLogger;
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

            _httpLogger = loggerFactory.CreateLogger<HttpLoggingMiddleware>();
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
            var httpEnabled = _httpLogger.IsEnabled(LogLevel.Information);
            var w3cEnabled = _w3cLogger.IsEnabled(LogLevel.Information);
            if (!httpEnabled && !w3cEnabled)
            {
                // Logger isn't enabled.
                return _next(context);
            }

            return InvokeInternal(context, httpEnabled, w3cEnabled);
        }

        private async Task InvokeInternal(HttpContext context, bool httpEnabled, bool w3cEnabled)
        {
            var options = _options.CurrentValue;

            var w3cList = new List<KeyValuePair<string, string?>>();

            if (w3cEnabled)
            {
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
                    if (httpEnabled)
                    {
                        AddToList(list, nameof(request.Protocol), request.Protocol);
                    }
                    if (w3cEnabled)
                    {
                        AddToList(w3cList, nameof(request.Protocol), request.Protocol);
                    }
                }

                if (options.LoggingFields.HasFlag(HttpLoggingFields.RequestMethod))
                {
                    if (httpEnabled)
                    {
                        AddToList(list, nameof(request.Method), request.Method);
                    }
                    if (w3cEnabled)
                    {
                        AddToList(w3cList, nameof(request.Method), request.Method);
                    }
                }

                if (httpEnabled && options.LoggingFields.HasFlag(HttpLoggingFields.RequestScheme))
                {
                    AddToList(list, nameof(request.Scheme), request.Scheme);
                }

                if (httpEnabled && options.LoggingFields.HasFlag(HttpLoggingFields.RequestPath))
                {
                    AddToList(list, nameof(request.PathBase), request.PathBase);
                    AddToList(list, nameof(request.Path), request.Path);
                }

                if (httpEnabled && options.LoggingFields.HasFlag(HttpLoggingFields.RequestQuery))
                {
                    AddToList(list, nameof(request.QueryString), request.QueryString.Value);
                }

                if (httpEnabled && options.LoggingFields.HasFlag(HttpLoggingFields.RequestHeaders))
                {
                    if (httpEnabled)
                    {
                        FilterHeaders(list, request.Headers, options._internalHttpRequestHeaders);
                    }
                    if (w3cEnabled)
                    {
                        FilterHeaders(w3cList, request.Headers, options._internalW3CRequestHeaders);
                    }
                }

                if (httpEnabled && options.LoggingFields.HasFlag(HttpLoggingFields.RequestBody))
                {
                    if (MediaTypeHelpers.TryGetEncodingForMediaType(request.ContentType,
                        options.MediaTypeOptions.MediaTypeStates,
                        out var encoding))
                    {
                        originalBody = request.Body;
                        requestBufferingStream = new RequestBufferingStream(
                            request.Body,
                            options.RequestBodyLogLimit,
                            _httpLogger,
                            encoding);
                        request.Body = requestBufferingStream;
                    }
                    else
                    {
                        _httpLogger.UnrecognizedMediaType();
                    }
                }

                if (httpEnabled)
                {
                    var httpRequestLog = new HttpRequestLog(list);

                    _httpLogger.RequestLog(httpRequestLog);
                }
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
                        _httpLogger,
                        context,
                        options.MediaTypeOptions.MediaTypeStates,
                        options);
                    response.Body = responseBufferingStream;
                    context.Features.Set<IHttpResponseBodyFeature>(responseBufferingStream);
                }

                await _next(context);

                if (httpEnabled && requestBufferingStream?.HasLogged == false)
                {
                    // If the middleware pipeline didn't read until 0 was returned from readasync,
                    // make sure we log the request body.
                    requestBufferingStream.LogRequestBody();
                }

                if (responseBufferingStream == null || responseBufferingStream.FirstWrite == false)
                {
                    // No body, write headers here.
                    LogResponseHeaders(response, options, _httpLogger, w3cList, httpEnabled, w3cEnabled);
                }

                if (httpEnabled && responseBufferingStream != null)
                {
                    var responseBody = responseBufferingStream.GetString(responseBufferingStream.Encoding);
                    if (!string.IsNullOrEmpty(responseBody))
                    {
                        _httpLogger.ResponseBody(responseBody);
                    }
                }
                if (w3cEnabled && w3cList.Count > 0)
                {
                    var httpW3CLog = new HttpW3CLog(w3cList);
                    _httpLogger.W3CLog(httpW3CLog);
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

        private static void LogResponseHeaders(HttpResponse response, HttpLoggingOptions options, ILogger logger, List<KeyValuePair<string, string?>> w3cList, bool httpEnabled, bool w3cEnabled)
        {
            var list = new List<KeyValuePair<string, string?>>(
                response.Headers.Count + DefaultResponseFieldsMinusHeaders);

            if (options.LoggingFields.HasFlag(HttpLoggingFields.ResponseStatusCode))
            {
                if (httpEnabled)
                {
                    list.Add(new KeyValuePair<string, string?>(nameof(response.StatusCode),
                        response.StatusCode.ToString(CultureInfo.InvariantCulture)));
                }
                if (w3cEnabled)
                {
                    w3cList.Add(new KeyValuePair<string, string?>(nameof(response.StatusCode),
                        response.StatusCode.ToString(CultureInfo.InvariantCulture)));
                }
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
