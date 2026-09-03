using System.Diagnostics;
using System.Globalization;
using Azure.Core;
using Azure.Core.Pipeline;

namespace CrossIndexQuery.Core.Telemetry;

/// <summary>
/// Pipeline policy that times every Azure AI Search request and reads the serverless
/// compute-unit header off the response.
/// </summary>
/// <remarks>
/// Installed once on the client options, this captures cost for every call the SDK makes —
/// including calls issued inside <c>KnowledgeBaseRetrievalClient</c>, whose internal fan-out
/// we could not otherwise account for.
/// </remarks>
public sealed class ComputeUnitPolicy : HttpPipelinePolicy
{
    /// <summary>Response header emitted by serverless Azure AI Search on every request.</summary>
    public const string HeaderName = "x-ms-azs-compute-units-consumed";

    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        if (!ComputeUnitScope.IsActive)
        {
            ProcessNext(message, pipeline);
            return;
        }

        long start = Stopwatch.GetTimestamp();
        try
        {
            ProcessNext(message, pipeline);
        }
        finally
        {
            RecordMeasurement(message, start);
        }
    }

    public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        if (!ComputeUnitScope.IsActive)
        {
            await ProcessNextAsync(message, pipeline).ConfigureAwait(false);
            return;
        }

        long start = Stopwatch.GetTimestamp();
        try
        {
            await ProcessNextAsync(message, pipeline).ConfigureAwait(false);
        }
        finally
        {
            RecordMeasurement(message, start);
        }
    }

    private static void RecordMeasurement(HttpMessage message, long startTimestamp)
    {
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);

        if (!message.HasResponse)
        {
            ComputeUnitScope.Record(new RequestMeasurement(DescribeOperation(message), null, elapsed, 0));
            return;
        }

        double? computeUnits = null;
        if (message.Response.Headers.TryGetValue(HeaderName, out string? raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            computeUnits = parsed;
        }

        ComputeUnitScope.Record(
            new RequestMeasurement(DescribeOperation(message), computeUnits, elapsed, message.Response.Status));
    }

    /// <summary>
    /// Builds a compact label such as <c>POST books-stripe-a/docs/search</c> so measurements stay
    /// readable in the results table without dragging the full URL along.
    /// </summary>
    private static string DescribeOperation(HttpMessage message)
    {
        Uri? uri = message.Request.Uri.ToUri();
        string path = uri is null ? "?" : uri.AbsolutePath.Trim('/');
        return $"{message.Request.Method} {path}";
    }
}
