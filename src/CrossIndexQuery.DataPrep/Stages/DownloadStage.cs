namespace CrossIndexQuery.DataPrep.Stages;

/// <summary>
/// Fetches the raw goodbooks-10k CSVs. Run once; the files land in <c>data/raw</c>, which is
/// git-ignored because the committed artifact is the enriched corpus, not the source data.
/// </summary>
internal sealed class DownloadStage
{
    private const string BaseUrl = "https://raw.githubusercontent.com/zygmuntz/goodbooks-10k/master";

    private static readonly string[] Files = ["books.csv", "book_tags.csv", "tags.csv"];

    private readonly string _rawDirectory;

    public DownloadStage(string dataDirectory) => _rawDirectory = Path.Combine(dataDirectory, "raw");

    public async Task<int> RunAsync(bool force, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_rawDirectory);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        foreach (string file in Files)
        {
            string destination = Path.Combine(_rawDirectory, file);
            if (File.Exists(destination) && !force)
            {
                Console.WriteLine($"  {file,-16} already present ({new FileInfo(destination).Length:N0} bytes)");
                continue;
            }

            Console.WriteLine($"  {file,-16} downloading...");
            using HttpResponseMessage response = await http
                .GetAsync($"{BaseUrl}/{file}", HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (FileStream output = File.Create(destination))
            {
                await response.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            Console.WriteLine($"  {file,-16} {new FileInfo(destination).Length,12:N0} bytes");
        }

        return 0;
    }
}
