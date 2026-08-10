using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAI.Developer.Backend;

public sealed class LlmClient(BackendSettings settings, HttpClient? httpClient = null)
{
    private readonly HttpClient _http = ConfigureClient(httpClient ?? new HttpClient(), settings);

    public async Task<JsonObject> CreatePlanAsync(
        string goal, string workspace, CancellationToken cancellationToken)
    {
        var schema = """
        {"summary":"short summary","steps":[{"id":"stable-id","title":"title","description":"concrete change","kind":"patch or validation","risk":"low, medium or high","targets":["relative/path"]}]}
        """;
        var text = await CompleteAsync(
            $"You are a development planner. Return JSON only. The plan must contain between 1 and " +
            $"{settings.MaxPlanSteps} steps. Keep steps atomic. Patch steps modify files; " +
            "validation steps only inspect or compile.",
            $"Goal:\n{goal}\n\nWorkspace:\n{workspace}\n\nSchema:\n{schema}",
            cancellationToken);
        return ParseObject(text);
    }

    public async Task<JsonObject> RepairPlanAsync(string goal, string workspace,
        JsonObject invalidPlan, string validationError, CancellationToken cancellationToken)
    {
        var schema = """
        {"summary":"short summary","steps":[{"id":"stable-id","title":"title","description":"concrete change","kind":"patch or validation","risk":"low, medium or high","targets":["relative/path"]}]}
        """;
        var text = await CompleteAsync(
            $"Repair the invalid development plan. Return JSON only with between 1 and " +
            $"{settings.MaxPlanSteps} steps. Preserve the goal, keep steps atomic, use unique step ids, " +
            "and follow the supplied schema exactly.",
            $"Goal:\n{goal}\n\nWorkspace:\n{workspace}\n\nValidation error:\n{validationError}" +
            $"\n\nInvalid plan:\n{invalidPlan.ToJsonString()}\n\nSchema:\n{schema}",
            cancellationToken);
        return ParseObject(text);
    }

    public Task<JsonObject> CreatePatchAsync(string goal, DevelopmentStep step,
        string context, CancellationToken cancellationToken) =>
        PatchRequestAsync("Generate the smallest safe patch for this step.",
            goal, step, context, cancellationToken);

    public Task<JsonObject> CreateRepairAsync(string goal, DevelopmentStep step,
        string context, CompilationResult compilation, CancellationToken cancellationToken) =>
        PatchRequestAsync("Repair all reported compiler errors across the supplied changed files. " +
            "Do not invent types or registrations that are absent from the supplied source. " +
            "Prefer removing an invalid reference or correcting its namespace/type to adding unrelated code. " +
            "Do not broaden scope beyond the supplied files.",
            goal, step, context + "\n\nCompiler output:\n" + compilation.Output,
            cancellationToken);

    private async Task<JsonObject> PatchRequestAsync(string instruction, string goal,
        DevelopmentStep step, string context, CancellationToken cancellationToken)
    {
        var schema = """
        {"summary":"summary","files":[{"path":"new/file","operation":"create","expectedSha256":"","content":"complete new UTF-8 content"},{"path":"existing/file","operation":"replace","expectedSha256":"supplied file hash","search":"one exact unique block from the existing file","replacement":"replacement block"}]}
        """;
        var text = await CompleteAsync(
            $"You are a patch generator. {instruction} Return JSON only. Never use absolute paths. Preserve unrelated code. " +
            "For operation choose exactly one literal value: create, replace, update, or delete. Never return 'create or update'. " +
            "For an existing file strongly prefer replace with one short, exact, uniquely occurring search block and its replacement. " +
            "Use full-content update only when a surgical replacement is impossible. Use create only when the target does not exist, " +
            "and delete only when deletion is explicitly required. " +
            "expectedSha256 must match the supplied file hash for update/delete and must be empty for create.",
            $"Goal:\n{goal}\n\nStep:\n{JsonSerializer.Serialize(step, JsonDefaults.Options)}\n\nContext:\n{context}\n\nSchema:\n{schema}",
            cancellationToken);
        return ParseObject(text);
    }

    private async Task<string> CompleteAsync(string system, string user,
        CancellationToken cancellationToken)
    {
        try
        {
            if (settings.ProviderName.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
                return await CompleteOllamaAsync(system, user, cancellationToken);
            return await CompleteOpenAiCompatibleAsync(system, user, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{settings.ProviderName} did not answer within " +
                $"{settings.LlmRequestTimeoutSeconds} seconds. The applied plan changes were rolled back.");
        }
    }

    private static HttpClient ConfigureClient(HttpClient client, BackendSettings configuration)
    {
        client.Timeout = TimeSpan.FromSeconds(
            Math.Clamp(configuration.LlmRequestTimeoutSeconds, 30, 3600));
        return client;
    }

    public async Task<string[]> ListModelsAsync(CancellationToken cancellationToken)
    {
        var key = ApiKey();
        var ollama = settings.ProviderName.Equals("Ollama", StringComparison.OrdinalIgnoreCase);
        var endpoint = ollama
            ? OllamaRoot(settings.BaseUrl) + "/api/tags"
            : OpenAiRoot(settings.BaseUrl) + "/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (!ollama && !string.IsNullOrWhiteSpace(key))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Model discovery failed ({(int)response.StatusCode}): {body}");
        using var document = JsonDocument.Parse(body);
        var property = ollama ? "models" : "data";
        if (!document.RootElement.TryGetProperty(property, out var models) ||
            models.ValueKind != JsonValueKind.Array) return [];
        return models.EnumerateArray().Select(item =>
            item.TryGetProperty(ollama ? "name" : "id", out var name)
                ? name.GetString() ?? "" : "")
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal).OrderBy(name => name,
                StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<string> CompleteOpenAiCompatibleAsync(string system,
        string user, CancellationToken cancellationToken)
    {
        var key = ApiKey();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            OpenAiRoot(settings.BaseUrl) + "/chat/completions");
        if (!string.IsNullOrWhiteSpace(key))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new
        {
            model = settings.Model,
            temperature = 0.1,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        });
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"LLM request failed ({(int)response.StatusCode}): {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private async Task<string> CompleteOllamaAsync(string system, string user,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            OllamaRoot(settings.BaseUrl) + "/api/chat");
        request.Content = JsonContent.Create(new
        {
            model = settings.Model,
            stream = false,
            format = "json",
            options = new { temperature = 0.1 },
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        });
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Ollama request failed ({(int)response.StatusCode}): {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("message")
            .GetProperty("content").GetString() ?? "";
    }

    private static string OpenAiRoot(string value)
    {
        var root = (value ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Provider base URL is required.");
        return root;
    }

    private static string OllamaRoot(string value)
    {
        var root = OpenAiRoot(value);
        return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? root[..^3].TrimEnd('/') : root;
    }

    private string ApiKey()
    {
        var providerKey = Environment.GetEnvironmentVariable("LOCALAI_" +
            settings.ProviderName.ToUpperInvariant() + "_API_KEY");
        return providerKey ?? Environment.GetEnvironmentVariable("LOCALAI_API_KEY") ??
               settings.ApiKey;
    }

    internal static JsonObject ParseObject(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            var closing = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && closing > firstLine)
                text = text[(firstLine + 1)..closing].Trim();
        }
        try
        {
            return ParseJsonObject(text);
        }
        catch (JsonException originalError)
        {
            var extracted = ExtractObject(text);
            if (!string.Equals(extracted, text, StringComparison.Ordinal))
            {
                try { return ParseJsonObject(extracted); }
                catch (JsonException) { }
            }

            var repaired = EscapeInvalidJsonStringBackslashes(extracted);
            if (string.Equals(repaired, extracted, StringComparison.Ordinal))
                throw;
            try { return ParseJsonObject(repaired); }
            catch (JsonException repairedError)
            {
                throw new JsonException(
                    "The model returned malformed JSON and deterministic escape repair was unsuccessful. " +
                    $"Remaining parser error: {repairedError.Message}",
                    originalError);
            }
        }
    }

    private static JsonObject ParseJsonObject(string text) =>
        JsonNode.Parse(text)?.AsObject() ?? throw new JsonException();

    private static string ExtractObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private static string EscapeInvalidJsonStringBackslashes(string text)
    {
        var output = new StringBuilder(text.Length + 16);
        var inString = false;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '"')
            {
                inString = !inString;
                output.Append(current);
                continue;
            }
            if (!inString || current != '\\')
            {
                output.Append(current);
                continue;
            }

            if (index + 1 < text.Length && IsValidJsonEscape(text, index + 1))
            {
                output.Append(current);
                output.Append(text[++index]);
                continue;
            }

            // Preserve the intended backslash as data instead of interpreting an
            // invalid model-generated JSON escape such as C#'s backslash-apostrophe.
            output.Append("\\\\");
        }
        return output.ToString();
    }

    private static bool IsValidJsonEscape(string text, int escapedIndex)
    {
        var escaped = text[escapedIndex];
        if (escaped is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
            return true;
        if (escaped != 'u' || escapedIndex + 4 >= text.Length)
            return false;
        for (var index = escapedIndex + 1; index <= escapedIndex + 4; index++)
            if (!Uri.IsHexDigit(text[index])) return false;
        return true;
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };
}
