using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrossIndexQuery.Core.Evaluation;

/// <summary>
/// Loads the committed evaluation query set.
/// </summary>
/// <remarks>
/// <para>
/// The queries are committed data rather than generated at run time, for the same reason the corpus
/// is: a benchmark whose inputs change between runs measures nothing. Two people running this
/// sample on different services should be comparing their services, not their query sets.
/// </para>
/// <para>
/// Each query is labelled with what kind of query it is, because the aggregate is close to
/// meaningless. Whether striping hurts depends almost entirely on whether a query's good answers
/// happen to sit on one side of the split, and averaging across both kinds produces a number whose
/// value is set by the ratio of easy to hard queries in the file rather than by anything about the
/// fusion strategy.
/// </para>
/// </remarks>
public static class QuerySetLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<IReadOnlyList<EvaluationQuery>> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Query set not found at '{path}'.", path);
        }

        await using FileStream stream = File.OpenRead(path);

        List<EvaluationQuery> queries = await JsonSerializer
            .DeserializeAsync<List<EvaluationQuery>>(stream, Options, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"Query set at '{path}' was empty or malformed.");

        // Duplicate identifiers would silently collapse results in the report, and the failure
        // would look like a strategy behaving inconsistently rather than a data problem.
        var duplicates = queries
            .GroupBy(q => q.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new InvalidDataException(
                $"Query set contains duplicate ids: {string.Join(", ", duplicates)}.");
        }

        return queries;
    }
}
