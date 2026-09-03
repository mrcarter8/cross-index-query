using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

namespace CrossIndexQuery.DataPrep.Stages;

/// <summary>
/// Thin client for the Azure OpenAI v1 files and batches endpoints.
/// </summary>
/// <remarks>
/// The Batch API is used rather than synchronous chat completions because generating ten thousand
/// blurbs is a one-time offline job with no latency requirement, and batch pricing plus batch
/// quota make a frontier model affordable for it. The v1 surface is OpenAI-compatible and needs no
/// <c>api-version</c> query string.
/// </remarks>
internal sealed class AzureOpenAiBatchClient : IDisposable
{
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private AccessToken _token;

    public AzureOpenAiBatchClient(string endpoint, string? apiKey)
    {
        _credential = new DefaultAzureCredential();
        _http = new HttpClient
        {
            BaseAddress = new Uri($"{endpoint.TrimEnd('/')}/openai/v1/"),
            Timeout = TimeSpan.FromMinutes(10),
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Add("api-key", apiKey);
        }
    }

    private async Task AuthorizeAsync(CancellationToken cancellationToken)
    {
        if (_http.DefaultRequestHeaders.Contains("api-key"))
        {
            return;
        }

        if (_token.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5))
        {
            _token = await _credential
                .GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken)
                .ConfigureAwait(false);
        }

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token.Token);
    }

    /// <summary>Uploads a JSONL request file and returns its file id.</summary>
    public async Task<string> UploadAsync(string path, CancellationToken cancellationToken)
    {
        await AuthorizeAsync(cancellationToken).ConfigureAwait(false);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/jsonl");
        content.Add(fileContent, "file", Path.GetFileName(path));
        content.Add(new StringContent("batch"), "purpose");

        using HttpResponseMessage response = await _http.PostAsync("files", content, cancellationToken).ConfigureAwait(false);
        JsonNode node = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return node["id"]!.GetValue<string>();
    }

    /// <summary>Creates a batch job over a previously uploaded file.</summary>
    public async Task<JsonNode> CreateBatchAsync(string inputFileId, CancellationToken cancellationToken)
    {
        await AuthorizeAsync(cancellationToken).ConfigureAwait(false);

        var body = new JsonObject
        {
            ["input_file_id"] = inputFileId,
            ["endpoint"] = "/chat/completions",
            ["completion_window"] = "24h",
        };

        using HttpResponseMessage response = await _http
            .PostAsJsonAsync("batches", body, cancellationToken)
            .ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonNode> GetBatchAsync(string batchId, CancellationToken cancellationToken)
    {
        await AuthorizeAsync(cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage response = await _http.GetAsync($"batches/{batchId}", cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> DownloadFileAsync(string fileId, CancellationToken cancellationToken)
    {
        await AuthorizeAsync(cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage response = await _http.GetAsync($"files/{fileId}/content", cancellationToken).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} downloading file {fileId}: {text}");
        }

        return text;
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {text}");
        }

        return JsonNode.Parse(text)
            ?? throw new InvalidOperationException($"Unexpected empty response: {text}");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Writes a JSON node with indentation, for the job-state files kept on disk.</summary>
    public static string Pretty(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>UTF-8 without a BOM, which the Batch API rejects on uploaded JSONL.</summary>
    public static Encoding JsonlEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
