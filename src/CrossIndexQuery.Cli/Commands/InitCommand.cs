using Azure;
using CrossIndexQuery.Core;
using CrossIndexQuery.Core.Clients;
using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.Core.Indexing;
using CrossIndexQuery.Core.Models;

namespace CrossIndexQuery.Cli.Commands;

/// <summary>
/// Creates the three indexes and loads the corpus into them.
/// </summary>
/// <remarks>
/// The oracle index is built from exactly the same documents as the two stripes combined, with the
/// same schema and the same vectors. That identity is the basis of every later comparison: anything
/// the harness measures has to be attributable to the split and nothing else, so the sample takes
/// care that there is nothing else.
/// </remarks>
public sealed class InitCommand(CrossIndexOptions options)
{
    public async Task<int> RunAsync(
        bool recreate,
        bool skipOracle = false,
        bool knowledgeBaseOnly = false,
        CancellationToken cancellationToken = default)
    {
        string dataDirectory = RepositoryLocator.ResolveDataDirectory(options.Corpus.DataDirectory);
        string corpusPath = Path.Combine(dataDirectory, CorpusFile.FileName);

        // The knowledge base points at indexes that already exist, so re-pointing it needs none of
        // the corpus work below. Worth a dedicated path because the alternative — a full init —
        // re-uploads 10,000 documents, and bulk upload throttles subsequent queries into 503s for
        // several minutes on serverless. Paying that to create one small resource is a bad trade.
        if (knowledgeBaseOnly)
        {
            return await CreateKnowledgeBaseAsync(
                new SearchClientFactory(options.Search), cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(corpusPath))
        {
            ReportMissingCorpus(corpusPath);
            return 1;
        }

        List<BookDocument> books = await CorpusFile.LoadAsync(corpusPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"Loaded {books.Count:N0} documents from {Path.GetFileName(corpusPath)}.");

        int dimensions = books.FirstOrDefault()?.ContentVector?.Length ?? 0;
        if (dimensions == 0)
        {
            Console.Error.WriteLine(
                "Corpus documents have no vectors. Run the embed stage before building indexes.");
            return 1;
        }

        if (dimensions != options.Foundry.EmbeddingDimensions)
        {
            // Vectors from different models or dimensionalities occupy unrelated coordinate spaces.
            // Comparing them still produces a number, which is precisely why this has to be an
            // error rather than a warning.
            Console.Error.WriteLine(
                $"Corpus vectors are {dimensions}-dimensional but configuration expects "
                + $"{options.Foundry.EmbeddingDimensions}. Refusing to build indexes whose vectors cannot be "
                + "compared.");
            return 1;
        }

        // GenreMap owns its own parsing: the file stores `stripeGroups` keyed a/b, which Load
        // flattens into the two genre lists. Deserializing the type directly bypasses that and
        // yields empty stripe groups, which would silently route every document by hash instead.
        GenreMap genreMap = GenreMap.Load(Path.Combine(dataDirectory, "genre-map.json"));

        var factory = new SearchClientFactory(options.Search);
        var provisioner = new IndexProvisioner(factory, options.Search);
        var router = new StripeRouter(options.Search, options.Corpus, genreMap);

        Console.WriteLine($"Stripe mode: {router.Mode}.");

        if (recreate)
        {
            Console.WriteLine("Deleting existing indexes...");
            await provisioner.DeleteIndexesAsync(cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine("Creating indexes...");
        await provisioner.CreateIndexesAsync(dimensions, cancellationToken).ConfigureAwait(false);

        Console.WriteLine("Uploading documents...");
        UploadSummary summary = await provisioner
            .UploadAsync(books, router, skipOracle, cancellationToken).ConfigureAwait(false);

        // Agentic retrieval queries a knowledge base rather than the indexes directly, so the
        // sources have to be re-pointed whenever the stripes change or it silently keeps answering
        // from the previous configuration.
        await CreateKnowledgeBaseAsync(factory, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"  {options.Search.StripeAIndex}: {summary.StripeA:N0}");
        Console.WriteLine($"  {options.Search.StripeBIndex}: {summary.StripeB:N0}");
        Console.WriteLine($"  {options.Search.OracleIndex}: {summary.Oracle:N0}");

        await VerifyCountsAsync(factory, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Reads the document count each index reports back.
    /// </summary>
    /// <remarks>
    /// Indexing is eventually consistent, so a count taken immediately after upload can lag. It is
    /// reported rather than asserted for that reason — the purpose is to catch an index that took
    /// far fewer documents than it was sent, not to fail on a few seconds of replication lag.
    /// </remarks>
    private async Task VerifyCountsAsync(SearchClientFactory factory, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("Reported document counts (indexing is eventually consistent):");

        foreach (string indexName in factory.AllIndexNames)
        {
            try
            {
                long count = await factory.GetSearchClient(indexName)
                    .GetDocumentCountAsync(cancellationToken).ConfigureAwait(false);

                Console.WriteLine($"  {indexName}: {count:N0}");
            }
            catch (RequestFailedException ex)
            {
                Console.Error.WriteLine($"  {indexName}: count failed ({ex.Status}) {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Registers the knowledge base that agentic retrieval queries.
    /// </summary>
    /// <remarks>
    /// Failure is reported and swallowed. Agentic retrieval is one strategy out of fifteen, and it
    /// depends on a preview feature that is not available in every region or on every tier — letting
    /// that take down index provisioning would make the whole sample unusable wherever the feature
    /// is absent.
    /// </remarks>
    private async Task<int> CreateKnowledgeBaseAsync(
        SearchClientFactory factory,
        CancellationToken cancellationToken)
    {
        try
        {
            Console.WriteLine("Registering knowledge sources...");
            await new KnowledgeBaseProvisioner(factory, options)
                .CreateAsync(cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"  {options.Search.KnowledgeBaseName} ready.");
            return 0;
        }
        catch (RequestFailedException ex)
        {
            Console.Error.WriteLine(
                $"  knowledge base setup failed ({ex.Status}): {ex.Message}");
            Console.Error.WriteLine(
                "  agentic retrieval will be unavailable; everything else still works.");
            return 0;
        }
    }

    private static void ReportMissingCorpus(string corpusPath)
    {
        Console.Error.WriteLine($"Corpus not found at {corpusPath}.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Build it with the DataPrep pipeline:");
        Console.Error.WriteLine("  dotnet run --project src/CrossIndexQuery.DataPrep -- download");
        Console.Error.WriteLine("  dotnet run --project src/CrossIndexQuery.DataPrep -- prepare");
        Console.Error.WriteLine("  dotnet run --project src/CrossIndexQuery.DataPrep -- blurbs submit --job full");
        Console.Error.WriteLine("  dotnet run --project src/CrossIndexQuery.DataPrep -- blurbs collect --job full");
        Console.Error.WriteLine("  dotnet run --project src/CrossIndexQuery.DataPrep -- embed");
    }
}
