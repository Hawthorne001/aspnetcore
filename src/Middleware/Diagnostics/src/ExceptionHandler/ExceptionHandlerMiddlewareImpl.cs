// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Diagnostics;

/// <summary>
/// A middleware for handling exceptions in the application.
/// </summary>
internal sealed class ExceptionHandlerMiddlewareImpl
{
    private const int DefaultStatusCode = StatusCodes.Status500InternalServerError;

    private readonly RequestDelegate _next;
    private readonly ExceptionHandlerOptions _options;
    private readonly ILogger _logger;
    private readonly Func<object, Task> _clearCacheHeadersDelegate;
    private readonly DiagnosticListener _diagnosticListener;
    private readonly IExceptionHandler[] _exceptionHandlers;
    private readonly DiagnosticsMetrics _metrics;
    private readonly IProblemDetailsService? _problemDetailsService;

    public ExceptionHandlerMiddlewareImpl(
        RequestDelegate next,
        ILoggerFactory loggerFactory,
        IOptions<ExceptionHandlerOptions> options,
        DiagnosticListener diagnosticListener,
        IEnumerable<IExceptionHandler> exceptionHandlers,
        IMeterFactory meterFactory,
        IProblemDetailsService? problemDetailsService = null)
    {
        _next = next;
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<ExceptionHandlerMiddleware>();
        _clearCacheHeadersDelegate = ClearCacheHeaders;
        _diagnosticListener = diagnosticListener;
        _exceptionHandlers = exceptionHandlers as IExceptionHandler[] ?? new List<IExceptionHandler>(exceptionHandlers).ToArray();
        _metrics = new DiagnosticsMetrics(meterFactory);
        _problemDetailsService = problemDetailsService;

        if (_options.ExceptionHandler == null)
        {
            if (_options.ExceptionHandlingPath == null)
            {
                if (problemDetailsService == null)
                {
                    throw new InvalidOperationException(Resources.ExceptionHandlerOptions_NotConfiguredCorrectly);
                }
            }
            else
            {
                _options.ExceptionHandler = _next;
            }
        }
    }

    /// <summary>
    /// Executes the middleware.
    /// </summary>
    /// <param name="context">The <see cref="HttpContext"/> for the current request.</param>
    public Task Invoke(HttpContext context)
    {
        ExceptionDispatchInfo edi;

        try
        {
            var task = _next(context);
            if (!task.IsCompletedSuccessfully)
            {
                return Awaited(this, context, task);
            }

            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            // Get the Exception, but don't continue processing in the catch block as its bad for stack usage.
            edi = ExceptionDispatchInfo.Capture(exception);
        }

        return HandleException(context, edi);

        static async Task Awaited(ExceptionHandlerMiddlewareImpl middleware, HttpContext context, Task task)
        {
            ExceptionDispatchInfo? edi = null;
            try
            {
                await task;
            }
            catch (Exception exception)
            {
                // Get the Exception, but don't continue processing in the catch block as its bad for stack usage.
                edi = ExceptionDispatchInfo.Capture(exception);
            }

            if (edi != null)
            {
                await middleware.HandleException(context, edi);
            }
        }
    }

    private async Task HandleException(HttpContext context, ExceptionDispatchInfo edi)
    {
        var exceptionName = edi.SourceException.GetType().FullName!;

        if ((edi.SourceException is OperationCanceledException || edi.SourceException is IOException) && context.RequestAborted.IsCancellationRequested)
        {
            _logger.RequestAbortedException();

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            }

            _metrics.RequestException(exceptionName, ExceptionResult.Aborted, handler: null);
            return;
        }

        // We can't do anything if the response has already started, just abort.
        if (context.Response.HasStarted)
        {
            _logger.ResponseStartedErrorHandler();

            DiagnosticsTelemetry.ReportUnhandledException(_logger, context, edi.SourceException);
            _metrics.RequestException(exceptionName, ExceptionResult.Skipped, handler: null);
            edi.Throw();
        }

        var originalPath = context.Request.Path;
        if (_options.ExceptionHandlingPath.HasValue)
        {
            context.Request.Path = _options.ExceptionHandlingPath;
        }
        var oldScope = _options.CreateScopeForErrors ? context.RequestServices : null;
        await using AsyncServiceScope? scope = _options.CreateScopeForErrors ? context.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope() : null;

        try
        {
            if (scope.HasValue)
            {
                context.RequestServices = scope.Value.ServiceProvider;
            }

            var exceptionHandlerFeature = new ExceptionHandlerFeature()
            {
                Error = edi.SourceException,
                Path = originalPath.Value!,
                Endpoint = context.GetEndpoint(),
                RouteValues = context.Features.Get<IRouteValuesFeature>()?.RouteValues
            };

            ClearHttpContext(context);

            context.Features.Set<IExceptionHandlerFeature>(exceptionHandlerFeature);
            context.Features.Set<IExceptionHandlerPathFeature>(exceptionHandlerFeature);
            context.Response.StatusCode = _options.StatusCodeSelector?.Invoke(edi.SourceException) ?? DefaultStatusCode;
            context.Response.OnStarting(_clearCacheHeadersDelegate, context.Response);

            string? handlerTag = null;
            var result = ExceptionHandledType.Unhandled;
            foreach (var exceptionHandler in _exceptionHandlers)
            {
                if (await exceptionHandler.TryHandleAsync(context, edi.SourceException, context.RequestAborted))
                {
                    result = ExceptionHandledType.ExceptionHandlerService;
                    handlerTag = exceptionHandler.GetType().FullName;
                    break;
                }
            }

            if (result == ExceptionHandledType.Unhandled)
            {
                if (_options.ExceptionHandler is not null)
                {
                    await _options.ExceptionHandler!(context);

                    // If the response has started, assume exception handler was successful.
                    if (context.Response.HasStarted)
                    {
                        if (_options.ExceptionHandlingPath.HasValue)
                        {
                            result = ExceptionHandledType.ExceptionHandlingPath;
                            handlerTag = _options.ExceptionHandlingPath.Value;
                        }
                        else
                        {
                            result = ExceptionHandledType.ExceptionHandlerDelegate;
                        }
                    }
                }
                else
                {
                    if (await _problemDetailsService!.TryWriteAsync(new()
                    {
                        HttpContext = context,
                        AdditionalMetadata = exceptionHandlerFeature.Endpoint?.Metadata,
                        ProblemDetails = { Status = context.Response.StatusCode },
                        Exception = edi.SourceException,
                    }))
                    {
                        result = ExceptionHandledType.ProblemDetailsService;
                        handlerTag = _problemDetailsService.GetType().FullName;
                    }
                }
            }

            if (result != ExceptionHandledType.Unhandled || _options.StatusCodeSelector != null || context.Response.StatusCode != StatusCodes.Status404NotFound || _options.AllowStatusCode404Response)
            {
                var suppressDiagnostics = false;

                // Customers may prefer to handle the exception and to do their own diagnostics.
                // In that case, it can be undesirable for the middleware to log the exception at an error level.
                // Run the configured callback to determine if exception diagnostics in the middleware should be suppressed.
                if (_options.SuppressDiagnosticsCallback is { } suppressCallback)
                {
                    var suppressDiagnosticsContext = new ExceptionHandlerSuppressDiagnosticsContext
                    {
                        HttpContext = context,
                        Exception = edi.SourceException,
                        ExceptionHandledBy = result
                    };
                    suppressDiagnostics = suppressCallback(suppressDiagnosticsContext);
                }
                else
                {
                    // Default behavior is to suppress diagnostics if the exception was handled by an IExceptionHandler service instance.
                    suppressDiagnostics = result == ExceptionHandledType.ExceptionHandlerService;
                }

                if (!suppressDiagnostics)
                {
                    // Note: Microsoft.AspNetCore.Diagnostics.HandledException is used by AppInsights to log errors.
                    // The diagnostics event is run together with standard exception logging.
                    const string eventName = "Microsoft.AspNetCore.Diagnostics.HandledException";
                    if (_diagnosticListener.IsEnabled() && _diagnosticListener.IsEnabled(eventName))
                    {
                        WriteDiagnosticEvent(_diagnosticListener, eventName, new { httpContext = context, exception = edi.SourceException });
                    }

                    DiagnosticsTelemetry.ReportUnhandledException(_logger, context, edi.SourceException);
                }

                _metrics.RequestException(exceptionName, ExceptionResult.Handled, handlerTag);
                return;
            }

            // Exception is unhandled. Record diagnostics for the unhandled exception before it is wrapped.
            DiagnosticsTelemetry.ReportUnhandledException(_logger, context, edi.SourceException);

            edi = ExceptionDispatchInfo.Capture(new InvalidOperationException($"The exception handler configured on {nameof(ExceptionHandlerOptions)} produced a 404 status response. " +
                $"This {nameof(InvalidOperationException)} containing the original exception was thrown since this is often due to a misconfigured {nameof(ExceptionHandlerOptions.ExceptionHandlingPath)}. " +
                $"If the exception handler is expected to return 404 status responses then set {nameof(ExceptionHandlerOptions.AllowStatusCode404Response)} to true.", edi.SourceException));
        }
        catch (Exception ex2)
        {
            // Suppress secondary exceptions, re-throw the original.
            _logger.ErrorHandlerException(ex2);

            // There was an error handling the exception. Log original unhandled exception.
            DiagnosticsTelemetry.ReportUnhandledException(_logger, context, edi.SourceException);
        }
        finally
        {
            context.Request.Path = originalPath;
            if (oldScope != null)
            {
                context.RequestServices = oldScope;
            }
        }

        _metrics.RequestException(exceptionName, ExceptionResult.Unhandled, handler: null);
        edi.Throw(); // Re-throw wrapped exception or the original if we couldn't handle it

        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026",
            Justification = "The values being passed into Write have the commonly used properties being preserved with DynamicDependency.")]
        static void WriteDiagnosticEvent<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(DiagnosticSource diagnosticSource, string name, TValue value)
            => diagnosticSource.Write(name, value);
    }

    private static void ClearHttpContext(HttpContext context)
    {
        context.Response.Clear();

        // An endpoint may have already been set. Since we're going to re-invoke the middleware pipeline we need to reset
        // the endpoint and route values to ensure things are re-calculated.
        HttpExtensions.ClearEndpoint(context);
    }

    private static Task ClearCacheHeaders(object state)
    {
        var headers = ((HttpResponse)state).Headers;
        headers.CacheControl = "no-cache,no-store";
        headers.Pragma = "no-cache";
        headers.Expires = "-1";
        headers.ETag = default;
        return Task.CompletedTask;
    }
}
