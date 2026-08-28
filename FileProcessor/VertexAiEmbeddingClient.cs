using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace propseekr_file_processor;

/// <summary>
/// Generates pgvector-compatible embeddings with Gemini on Vertex AI using a
/// Google Cloud service account. gemini-embedding-001 accepts one text per
/// prediction request, so callers receive vectors in the same order as input.
/// </summary>
internal sealed class VertexAiEmbeddingClient
{
    private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";
    private readonly GoogleCredential _credential;
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly int _dimensions;

    public string Model { get; }

    private VertexAiEmbeddingClient(
        GoogleCredential credential,
        string projectId,
        string location,
        string model,
        int dimensions)
    {
        _credential = credential.CreateScoped(CloudPlatformScope);
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _endpoint = $"https://{location}-aiplatform.googleapis.com/v1/projects/{Uri.EscapeDataString(projectId)}/locations/{Uri.EscapeDataString(location)}/publishers/google/models/{Uri.EscapeDataString(model)}:predict";
        _dimensions = dimensions;
        Model = model;
    }

    public static VertexAiEmbeddingClient FromEnvironment()
    {
        var type = Required("GOOGLE_SERVICE_ACCOUNT_TYPE");
        if (!type.Equals("service_account", StringComparison.Ordinal))
            throw new InvalidOperationException("Google service-account type must be 'service_account'.");

        var projectId = Required("GOOGLE_CLOUD_PROJECT");
        var credentialJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = type,
            ["project_id"] = projectId,
            ["private_key_id"] = Required("GOOGLE_PRIVATE_KEY_ID"),
            ["private_key"] = Required("GOOGLE_PRIVATE_KEY").Replace("\\n", "\n", StringComparison.Ordinal),
            ["client_email"] = Required("GOOGLE_CLIENT_EMAIL"),
            ["client_id"] = Required("GOOGLE_CLIENT_ID"),
            ["auth_uri"] = Environment.GetEnvironmentVariable("GOOGLE_AUTH_URI") ?? "https://accounts.google.com/o/oauth2/auth",
            ["token_uri"] = Environment.GetEnvironmentVariable("GOOGLE_TOKEN_URI") ?? "https://oauth2.googleapis.com/token",
            ["auth_provider_x509_cert_url"] = Environment.GetEnvironmentVariable("GOOGLE_AUTH_PROVIDER_CERT_URL") ?? "https://www.googleapis.com/oauth2/v1/certs",
            ["client_x509_cert_url"] = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_CERT_URL") ?? string.Empty,
            ["universe_domain"] = Environment.GetEnvironmentVariable("GOOGLE_UNIVERSE_DOMAIN") ?? "googleapis.com"
        });

        var credential = CredentialFactory.FromJson(credentialJson, "service_account");
        var location = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_LOCATION") ?? "us-central1";
        var model = Environment.GetEnvironmentVariable("VERTEX_EMBEDDING_MODEL") ?? "gemini-embedding-001";
        var dimensionsText = Environment.GetEnvironmentVariable("EMBEDDING_DIMENSIONS");
        var dimensions = int.TryParse(dimensionsText, out var parsedDimensions) ? parsedDimensions : 1536;

        if (dimensions is < 128 or > 3072)
            throw new InvalidOperationException("Embedding dimensions must be between 128 and 3072.");

        return new VertexAiEmbeddingClient(credential, projectId, location, model, dimensions);
    }

    public async Task<List<float[]>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        var vectors = new List<float[]>(texts.Count);
        foreach (var text in texts)
            vectors.Add(await GenerateEmbeddingAsync(text, cancellationToken));
        return vectors;
    }

    public async Task<float[]> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Empty text cannot be embedded.", nameof(text));

        var accessToken = await _credential.UnderlyingCredential.GetAccessTokenForRequestAsync(
            _endpoint,
            cancellationToken);

        var payload = JsonSerializer.Serialize(new
        {
            instances = new[]
            {
                new Dictionary<string, object>
                {
                    ["content"] = text,
                    ["task_type"] = "RETRIEVAL_DOCUMENT"
                }
            },
            parameters = new
            {
                autoTruncate = true,
                outputDimensionality = _dimensions
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Vertex AI embedding request failed with HTTP {(int)response.StatusCode}: {ExtractError(responseBody)}");
        }

        using var document = JsonDocument.Parse(responseBody);
        var values = document.RootElement
            .GetProperty("predictions")[0]
            .GetProperty("embeddings")
            .GetProperty("values");

        var vector = values.EnumerateArray().Select(value => value.GetSingle()).ToArray();
        if (vector.Length != _dimensions)
            throw new InvalidOperationException($"Vertex AI returned {vector.Length} dimensions; expected {_dimensions}.");

        // gemini-embedding-001 requires manual normalization when requesting a
        // reduced dimension. pgvector cosine and inner-product matching then
        // retain consistent rankings.
        Normalize(vector);
        return vector;
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Required Vertex AI setting {name} is not configured.");

    private static void Normalize(float[] vector)
    {
        var magnitude = Math.Sqrt(vector.Sum(value => (double)value * value));
        if (magnitude <= 0) return;
        for (var index = 0; index < vector.Length; index++)
            vector[index] = (float)(vector[index] / magnitude);
    }

    private static string ExtractError(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return document.RootElement.GetProperty("error").GetProperty("message").GetString()
                ?? "Unknown Vertex AI error.";
        }
        catch
        {
            return "Vertex AI returned an unreadable error response.";
        }
    }
}
