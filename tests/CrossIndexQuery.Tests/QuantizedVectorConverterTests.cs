using System.Text.Json;
using CrossIndexQuery.Core.Models;
using Xunit;

namespace CrossIndexQuery.Tests;

/// <summary>
/// Pins the committed corpus vector encoding. The corpus file is the sample's one expensive,
/// non-reproducible artifact, so a silent change to how vectors are written or read would either
/// corrupt it or make it unloadable long after the mistake was made.
/// </summary>
public sealed class QuantizedVectorConverterTests
{
    private static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new QuantizedVectorConverter());
        return options;
    }

    /// <summary>Mimics a real embedding: L2-normalised, components in a narrow band around zero.</summary>
    private static float[] SampleVector(int dimensions, int seed)
    {
        var random = new Random(seed);
        var vector = new float[dimensions];
        double sum = 0;

        for (int i = 0; i < dimensions; i++)
        {
            vector[i] = (float)((random.NextDouble() * 2) - 1);
            sum += (double)vector[i] * vector[i];
        }

        float inverse = (float)(1.0 / Math.Sqrt(sum));
        for (int i = 0; i < dimensions; i++)
        {
            vector[i] *= inverse;
        }

        return vector;
    }

    private static float[] RoundTrip(float[] vector)
    {
        string json = JsonSerializer.Serialize(vector, Options);
        return JsonSerializer.Deserialize<float[]>(json, Options)!;
    }

    [Fact]
    public void RoundTripPreservesDimensionality()
    {
        float[] restored = RoundTrip(SampleVector(1536, 7));

        Assert.Equal(1536, restored.Length);
    }

    [Fact]
    public void RoundTripKeepsComponentsWithinOneQuantisationStep()
    {
        float[] original = SampleVector(1536, 11);
        float[] restored = RoundTrip(original);

        // Symmetric quantisation guarantees each component lands within half a step of its
        // original value; half a step is max|component| / 254.
        float tolerance = original.Max(Math.Abs) / 254f;

        for (int i = 0; i < original.Length; i++)
        {
            Assert.True(
                Math.Abs(original[i] - restored[i]) <= tolerance,
                $"component {i} moved by more than one quantisation step");
        }
    }

    /// <summary>
    /// The property that actually matters. Ranking depends on cosine similarity, not on individual
    /// components, so the encoding is only acceptable if similarity survives it.
    /// </summary>
    [Fact]
    public void RoundTripPreservesCosineSimilarity()
    {
        float[] document = SampleVector(1536, 3);
        float[] query = SampleVector(1536, 4);

        double exact = Dot(document, query);
        double quantized = Dot(RoundTrip(document), query);

        Assert.InRange(Math.Abs(exact - quantized), 0, 1e-3);
    }

    [Fact]
    public void WritesFarFewerBytesThanDecimalText()
    {
        float[] vector = SampleVector(1536, 21);

        int quantized = JsonSerializer.Serialize(vector, Options).Length;
        int plain = JsonSerializer.Serialize(vector).Length;

        // Roughly 2 KB against roughly 18 KB; assert the order of magnitude rather than an exact
        // ratio, which depends on how many digits each random component happens to need.
        Assert.True(quantized * 4 < plain, $"expected a large saving but got {quantized} vs {plain}");
    }

    /// <summary>
    /// Corpus files written before this encoding, or regenerated deliberately at full precision,
    /// must keep loading. Without this the sample would need a migration step nobody would run.
    /// </summary>
    [Fact]
    public void ReadsPlainNumericArrays()
    {
        float[] restored = JsonSerializer.Deserialize<float[]>("[0.25,-0.5,0.125]", Options)!;

        Assert.Equal([0.25f, -0.5f, 0.125f], restored);
    }

    [Fact]
    public void RoundTripsNull()
    {
        Assert.Null(JsonSerializer.Deserialize<float[]>("null", Options));
    }

    [Fact]
    public void RoundTripsAnAllZeroVector()
    {
        float[] restored = RoundTrip(new float[8]);

        Assert.Equal(8, restored.Length);
        Assert.All(restored, component => Assert.Equal(0f, component));
    }

    /// <summary>
    /// A corrupt or truncated vector has to fail loudly. Silently yielding a short vector would
    /// surface much later as an unexplained dimensionality mismatch during indexing.
    /// </summary>
    [Fact]
    public void RejectsAPayloadTooShortToHoldAScale()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<float[]>("\"AAA=\"", Options));
    }

    /// <summary>
    /// Guards the reason the converter is registered through <see cref="CorpusFile"/> options
    /// rather than as an attribute: the same document type is serialised by the Azure AI Search
    /// SDK, which must emit a JSON array of numbers for a Collection(Edm.Single) field.
    /// </summary>
    [Fact]
    public void DefaultSerialisationOfADocumentStillEmitsANumericArray()
    {
        var book = new BookDocument { Id = "book-1", ContentVector = [0.5f, -0.25f] };

        string json = JsonSerializer.Serialize(book);

        Assert.Contains("[0.5,-0.25]", json, StringComparison.Ordinal);
    }

    private static double Dot(float[] left, float[] right)
    {
        double total = 0;
        for (int i = 0; i < left.Length; i++)
        {
            total += (double)left[i] * right[i];
        }

        return total;
    }
}
