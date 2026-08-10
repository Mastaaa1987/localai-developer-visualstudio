namespace LocalAI.Developer.Backend;

public sealed class ContextProfile
{
    public string ProviderName { get; set; } = "";
    public string ModelName { get; set; } = "";
    public int ContextWindowTokens { get; set; } = 16384;
    public int ReservedSystemTokens { get; set; } = 1000;
    public int ReservedPromptTokens { get; set; } = 2500;
    public int ReservedResponseTokens { get; set; } = 4000;
    public int MaximumContextFiles { get; set; } = 8;
    public int AvailableContextTokens => Math.Max(0,
        ContextWindowTokens - ReservedSystemTokens -
        ReservedPromptTokens - ReservedResponseTokens);
}

public sealed class BudgetSnapshot
{
    public string ProviderName { get; set; } = "";
    public string ModelName { get; set; } = "";
    public int ContextWindowTokens { get; set; }
    public int AvailableContextTokens { get; set; }
    public int ReservedSystemTokens { get; set; }
    public int ReservedPromptTokens { get; set; }
    public int ReservedResponseTokens { get; set; }
    public int MaximumCharacters { get; set; }
    public int UsedCharacters { get; set; }
    public int RemainingCharacters { get; set; }
    public double CharacterUsagePercent { get; set; }
    public int EstimatedContextTokens { get; set; }
    public int EstimatedPromptTokens { get; set; }
    public int EstimatedTotalRequestTokens { get; set; }
    public double TokenUsagePercent { get; set; }
    public bool ExceedsContextWindow { get; set; }
    public string Warning { get; set; } = "";
}

public sealed class TokenEstimator(float charactersPerToken = 3.5f)
{
    private readonly float _charactersPerToken = Math.Max(1f, charactersPerToken);

    public int EstimateTokens(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        var characterEstimate = (int)Math.Ceiling(value.Length / _charactersPerToken);
        var structural = value.Count(character => "{}()[];,:".Contains(character)) / 6;
        return Math.Max(1, characterEstimate + structural);
    }

    public int EstimateCharacters(int tokens) =>
        tokens <= 0 ? 0 : Math.Max(1, (int)Math.Floor(tokens * _charactersPerToken));
}

public static class ContextProfileResolver
{
    public static ContextProfile Resolve(string providerName, string modelName)
    {
        providerName ??= "";
        modelName ??= "";
        if (Contains(providerName, "Ollama"))
            return Create(providerName, modelName, 16384, 800, 2200, 3500, 6);
        if (Contains(providerName, "Mistral"))
        {
            var window = MistralContextWindow(modelName);
            var large = window >= 128000;
            return Create(providerName, modelName, window,
                large ? 2000 : 1000, large ? 8000 : 3000,
                large ? 16000 : 6000, large ? 16 : 10);
        }
        if (Contains(providerName, "OpenAI"))
            return Create(providerName, modelName, 65536, 1200, 4000, 10000, 12);
        return Create(string.IsNullOrWhiteSpace(providerName) ? "Local" : providerName,
            string.IsNullOrWhiteSpace(modelName) ? "Unknown" : modelName,
            16384, 1000, 2500, 4000, 8);
    }

    private static ContextProfile Create(string provider, string model, int window,
        int system, int prompt, int response, int files) => new()
    {
        ProviderName = provider, ModelName = model,
        ContextWindowTokens = window, ReservedSystemTokens = system,
        ReservedPromptTokens = prompt, ReservedResponseTokens = response,
        MaximumContextFiles = files
    };

    private static bool Contains(string value, string search) =>
        value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static int MistralContextWindow(string model)
    {
        if (Contains(model, "2501") || Contains(model, "small-3.0")) return 32768;
        if (Contains(model, "latest") || Contains(model, "2603") ||
            Contains(model, "small-4") || Contains(model, "large-3") ||
            Contains(model, "devstral-2") || Contains(model, "ministral-3"))
            return 262144;
        return 131072;
    }
}

public sealed class BudgetService
{
    private readonly TokenEstimator _estimator = new();

    public BudgetSnapshot Calculate(ContextProfile profile, string prompt, string context)
    {
        var maxCharacters = _estimator.EstimateCharacters(profile.ContextWindowTokens);
        var usedCharacters = context.Length;
        var contextTokens = _estimator.EstimateTokens(context);
        var promptTokens = _estimator.EstimateTokens(prompt);
        var fixedEstimate = profile.ReservedSystemTokens + profile.ReservedPromptTokens +
                            profile.ReservedResponseTokens + contextTokens;
        var actualEstimate = profile.ReservedSystemTokens +
                             profile.ReservedResponseTokens + promptTokens + contextTokens;
        var total = Math.Max(fixedEstimate, actualEstimate);
        return new BudgetSnapshot
        {
            ProviderName = profile.ProviderName,
            ModelName = profile.ModelName,
            ContextWindowTokens = profile.ContextWindowTokens,
            AvailableContextTokens = profile.AvailableContextTokens,
            ReservedSystemTokens = profile.ReservedSystemTokens,
            ReservedPromptTokens = profile.ReservedPromptTokens,
            ReservedResponseTokens = profile.ReservedResponseTokens,
            MaximumCharacters = maxCharacters,
            UsedCharacters = usedCharacters,
            RemainingCharacters = Math.Max(0, maxCharacters - usedCharacters),
            CharacterUsagePercent = Percent(usedCharacters, maxCharacters),
            EstimatedContextTokens = contextTokens,
            EstimatedPromptTokens = promptTokens,
            EstimatedTotalRequestTokens = total,
            TokenUsagePercent = Percent(total, profile.ContextWindowTokens),
            ExceedsContextWindow = total > profile.ContextWindowTokens,
            Warning = total > profile.ContextWindowTokens
                ? "Estimated request exceeds the configured context window." : ""
        };
    }

    private static double Percent(int value, int maximum) => maximum <= 0
        ? 0 : Math.Round(Math.Clamp(value * 100d / maximum, 0d, 999d), 1);
}
