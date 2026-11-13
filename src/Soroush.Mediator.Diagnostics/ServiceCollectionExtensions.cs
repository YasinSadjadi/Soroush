using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator.Diagnostics;

/// <summary>Registers optional mediator diagnostics without configuring exporters or replacing transports.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Adds logging, ActivitySource and Meter observation. Repeated calls do not duplicate observers.</summary>
    public static IServiceCollection AddSoroushDiagnostics(this IServiceCollection services,
        Action<SoroushDiagnosticsOptions>? configure = null)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        var options = services.AddOptions<SoroushDiagnosticsOptions>()
            .Validate(value => value.MaxMetricNames > 0 && value.MaxMetricNames <= 4096 &&
                value.MaxMetricErrorTypes > 0 && value.MaxMetricErrorTypes <= 256,
                "Metric name limits must be within 1..4096 and error type limits within 1..256.")
            .Validate(value => Enum.IsDefined(typeof(ExceptionDetailsOwner), value.ExceptionDetailsOwner) &&
                Enum.IsDefined(typeof(LogLevel), value.FailureLogLevel), "Invalid diagnostics enum value.")
            .ValidateOnStart();
        if (configure != null) options.Configure(configure);
        services.TryAddSingleton<IMediatorObserver>(provider =>
        {
            var value = provider.GetRequiredService<IOptions<SoroushDiagnosticsOptions>>().Value;
            if (!value.EnableLogging && !value.EnableTracing && !value.EnableMetrics) return NullMediatorObserver.Instance;
            try { return new DiagnosticsObserver(provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance, value); }
            catch (Exception ex)
            {
                MediatorObservation.ReportDiagnosticFailure("observer.create", ex);
                return NullMediatorObserver.Instance;
            }
        });
        return services;
    }
}
