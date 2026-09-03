namespace CrossIndexQuery.Core.Telemetry;

/// <summary>
/// One measured Azure AI Search request.
/// </summary>
/// <param name="Operation">Caller-supplied label, e.g. <c>keyword:books-stripe-a</c>.</param>
/// <param name="ComputeUnits">
/// Value of the <c>x-ms-azs-compute-units-consumed</c> response header, or <see langword="null"/>
/// when the service did not return it (non-serverless tiers do not emit this header).
/// </param>
/// <param name="Elapsed">Wall-clock duration of the request.</param>
/// <param name="Status">HTTP status code.</param>
public readonly record struct RequestMeasurement(
    string Operation,
    double? ComputeUnits,
    TimeSpan Elapsed,
    int Status);

/// <summary>
/// Ambient collector for per-request cost and latency.
/// </summary>
/// <remarks>
/// <para>
/// Serverless Azure AI Search returns <c>x-ms-azs-compute-units-consumed</c> on every response.
/// That turns the usual hand-waving about "multi-index querying costs more" into a measured
/// number, which is the point of this sample's evaluation harness: each fusion strategy issues a
/// different number and shape of requests, and we want the real bill, not an estimate.
/// </para>
/// <para>
/// Scopes nest and flow across <see langword="await"/> boundaries via <see cref="AsyncLocal{T}"/>.
/// A measurement is recorded into the innermost active scope and every scope enclosing it, so an
/// outer "whole query" scope and an inner "stripe A leg" scope both see the requests they contain.
/// </para>
/// </remarks>
public sealed class ComputeUnitScope : IDisposable
{
    private static readonly AsyncLocal<ComputeUnitScope?> CurrentScope = new();

    private readonly ComputeUnitScope? _parent;
    private readonly List<RequestMeasurement> _measurements = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    private ComputeUnitScope(string label)
    {
        Label = label;
        _parent = CurrentScope.Value;
        CurrentScope.Value = this;
    }

    /// <summary>Human-readable name for this scope.</summary>
    public string Label { get; }

    /// <summary>Begins a new measurement scope. Dispose it to restore the previous scope.</summary>
    public static ComputeUnitScope Begin(string label) => new(label);

    /// <summary>All requests recorded in this scope, in completion order.</summary>
    public IReadOnlyList<RequestMeasurement> Measurements
    {
        get
        {
            lock (_gate)
            {
                return _measurements.ToArray();
            }
        }
    }

    /// <summary>Number of Azure AI Search requests recorded in this scope.</summary>
    public int RequestCount
    {
        get
        {
            lock (_gate)
            {
                return _measurements.Count;
            }
        }
    }

    /// <summary>
    /// Total compute units consumed, or <see langword="null"/> when no response carried the
    /// header — which is the expected case on non-serverless tiers.
    /// </summary>
    public double? TotalComputeUnits
    {
        get
        {
            lock (_gate)
            {
                double total = 0;
                bool any = false;
                foreach (RequestMeasurement m in _measurements)
                {
                    if (m.ComputeUnits is { } cu)
                    {
                        total += cu;
                        any = true;
                    }
                }

                return any ? total : null;
            }
        }
    }

    /// <summary>
    /// Sum of individual request durations. This exceeds wall-clock time when stripe queries run
    /// in parallel, which is intentional: it reflects work done, while the harness measures
    /// wall-clock separately to reflect what a user waits for.
    /// </summary>
    public TimeSpan TotalRequestTime
    {
        get
        {
            lock (_gate)
            {
                TimeSpan total = TimeSpan.Zero;
                foreach (RequestMeasurement m in _measurements)
                {
                    total += m.Elapsed;
                }

                return total;
            }
        }
    }

    internal static void Record(RequestMeasurement measurement)
    {
        for (ComputeUnitScope? scope = CurrentScope.Value; scope is not null; scope = scope._parent)
        {
            lock (scope._gate)
            {
                scope._measurements.Add(measurement);
            }
        }
    }

    /// <summary>True when at least one scope is active, so the policy can skip work otherwise.</summary>
    internal static bool IsActive => CurrentScope.Value is not null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CurrentScope.Value = _parent;
    }
}
