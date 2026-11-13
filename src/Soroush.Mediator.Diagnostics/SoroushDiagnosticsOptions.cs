using Microsoft.Extensions.Logging;

namespace Soroush.Mediator.Diagnostics;

/// <summary>Chooses which boundary owns complete exception details.</summary>
public enum ExceptionDetailsOwner
{
    /// <summary>The application middleware or worker logs exceptions.</summary>
    ApplicationBoundary,
    /// <summary>The outermost mediator operation logs escaping exceptions.</summary>
    OutermostMediator
}

/// <summary>Controls optional signals. Request and response payload capture is intentionally not provided.</summary>
public sealed class SoroushDiagnosticsOptions
{
    /// <summary>Emits structured lifecycle logs.</summary>
    public bool EnableLogging { get; set; } = true;
    /// <summary>Creates operation activities when listeners are present.</summary>
    public bool EnableTracing { get; set; } = true;
    /// <summary>Publishes operation metrics.</summary>
    public bool EnableMetrics { get; set; } = true;
    /// <summary>Creates stage and subscriber activities.</summary>
    public bool EnableStageTracing { get; set; } = true;
    /// <summary>Publishes stage duration metrics.</summary>
    public bool EnableStageMetrics { get; set; } = true;
    /// <summary>Publishes one metric increment per delivered stream item.</summary>
    public bool EnableStreamItemMetrics { get; set; } = true;
    /// <summary>The level for returned execution/preparation failures.</summary>
    public LogLevel FailureLogLevel { get; set; } = LogLevel.Warning;
    /// <summary>The owner of complete exception details. The default avoids duplicating host exception logs.</summary>
    public ExceptionDetailsOwner ExceptionDetailsOwner { get; set; } = ExceptionDetailsOwner.ApplicationBoundary;
    /// <summary>The maximum distinct type/stage names used in metric dimensions; overflow maps to other.</summary>
    public int MaxMetricNames { get; set; } = 256;
    /// <summary>The maximum distinct error types used in metric dimensions; overflow maps to other.</summary>
    public int MaxMetricErrorTypes { get; set; } = 64;
}

/// <summary>Stable instrumentation scope names used when configuring an OpenTelemetry SDK.</summary>
public static class SoroushDiagnostics
{
    /// <summary>Register this source with the tracing SDK.</summary>
    public const string ActivitySourceName = "Soroush.Mediator";
    /// <summary>Register this meter with the metrics SDK.</summary>
    public const string MeterName = "Soroush.Mediator";
}
