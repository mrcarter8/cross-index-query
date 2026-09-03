using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using CrossIndexQuery.Core.Configuration;
using OpenAI.Embeddings;

namespace CrossIndexQuery.Core.Clients;

/// <summary>Turns query text into the vector the indexes were built with.</summary>
/// <remarks>
/// <para>
/// An interface rather than a concrete client for one reason that matters to the benchmark: the
/// evaluation harness issues the same query many times, and embedding it afresh each time would add
/// a network round trip and its variance to every latency measurement, attributing to fusion a cost
/// that belongs to embedding. The caching implementation removes that noise.
/// </para>
/// <para>
/// The correctness constraint underneath is absolute. Every index in this sample must be built with
/// the same embedding model at the same dimensionality, and queries must use that model too.
/// Vectors from different models occupy unrelated coordinate spaces, so cosine similarity between
/// them is not merely inaccurate — it is meaningless, while still returning a plausible-looking
/// number. This is the one prerequisite that fails silently, which is why the preflight check
/// enforces it.
/// </para>
/// </remarks>
public interface IQueryEmbedder
{
    ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Embeds via Azure OpenAI, caching each distinct query for the process lifetime.</summary>
public sealed class AzureOpenAIQueryEmbedder : IQueryEmbedder
{
    private readonly EmbeddingClient _client;
    private readonly Dictionary<string, ReadOnlyMemory<float>> _cache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public AzureOpenAIQueryEmbedder(CrossIndexOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        EmbeddingOptions embedding = options.Embedding;
        var endpoint = new Uri(embedding.Endpoint);

        AzureOpenAIClient client = string.IsNullOrWhiteSpace(embedding.ApiKey)
            ? new AzureOpenAIClient(endpoint, new DefaultAzureCredential())
            : new AzureOpenAIClient(endpoint, new ApiKeyCredential(embedding.ApiKey));

        _client = client.GetEmbeddingClient(embedding.Deployment);
        Dimensions = embedding.Dimensions;
    }

    /// <summary>Dimensionality requested, which must match what the indexes were built with.</summary>
    public int Dimensions { get; }

    public async ValueTask<ReadOnlyMemory<float>> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        lock (_gate)
        {
            if (_cache.TryGetValue(text, out ReadOnlyMemory<float> cached))
            {
                return cached;
            }
        }

        ClientResult<OpenAIEmbedding> result = await _client
            .GenerateEmbeddingAsync(
                text,
                new EmbeddingGenerationOptions { Dimensions = Dimensions },
                cancellationToken)
            .ConfigureAwait(false);

        ReadOnlyMemory<float> vector = result.Value.ToFloats();

        lock (_gate)
        {
            _cache[text] = vector;
        }

        return vector;
    }
}
