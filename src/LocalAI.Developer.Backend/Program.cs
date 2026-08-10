using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAI.Developer.Backend;

public static class Program
{
    public static async Task Main()
    {
        var server = new BackendServer();
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonRpcRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<JsonRpcRequest>(line, JsonDefaults.Options);
            }
            catch (Exception error)
            {
                await server.WriteErrorAsync(null, -32700, error.Message);
                continue;
            }
            if (request is null) continue;
            _ = server.HandleAsync(request);
        }
        server.CancelAll();
    }
}

public sealed class BackendServer
{
    private readonly SemaphoreSlim _output = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runs = new();
    private BackendSettings? _settings;
    private WorkspaceService? _workspace;
    private SessionStore? _store;
    private RoslynBackend? _roslyn;
    private WorkflowService? _workflow;
    private LlmClient? _llm;
    private GitWorkflowService? _git;

    public async Task HandleAsync(JsonRpcRequest request)
    {
        try
        {
            var result = await DispatchAsync(request.Method, request.Params ?? new JsonObject());
            if (request.Id is not null) await WriteResultAsync(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            if (request.Id is not null)
                await WriteErrorAsync(request.Id, -32800, "Request cancelled.");
        }
        catch (Exception error)
        {
            if (request.Id is not null)
                await WriteErrorAsync(request.Id, -32000, error.Message, error.ToString());
        }
    }

    public void CancelAll()
    {
        foreach (var source in _runs.Values) source.Cancel();
    }

    private async Task<object?> DispatchAsync(string method, JsonObject parameters)
    {
        if (method == "initialize")
        {
            _settings = parameters.Deserialize<BackendSettings>(JsonDefaults.Options) ??
                        throw new InvalidOperationException("Backend settings are invalid.");
            _settings.WorkspaceRoot = Path.GetFullPath(_settings.WorkspaceRoot);
            _settings.StorageDirectory = Path.GetFullPath(_settings.StorageDirectory);
            if (IsVisualStudioInstallPath(_settings.WorkspaceRoot))
                throw new InvalidOperationException(
                    "The Visual Studio installation directory cannot be used as workspace root.");
            _workspace = new WorkspaceService(_settings);
            _store = new SessionStore(_settings);
            _roslyn = new RoslynBackend(_settings);
            _llm = new LlmClient(_settings);
            _git = new GitWorkflowService(_settings);
            _workflow = new WorkflowService(_settings, _workspace, _store,
                _llm, _roslyn, _git, WriteNotificationAsync);
            return new { initialized = true, processId = Environment.ProcessId };
        }

        EnsureInitialized();
        switch (method)
        {
            case "createPlan":
            {
                var goal = RequiredString(parameters, "goal");
                var key = Guid.NewGuid().ToString("N");
                var source = Register(key);
                try { return await _workflow!.CreatePlanAsync(goal, source.Token); }
                finally { Release(key, source); }
            }
            case "run":
            {
                var id = RequiredString(parameters, "sessionId");
                var session = await _store!.LoadAsync(id) ??
                              throw new InvalidOperationException($"Session not found: {id}");
                var approved = parameters["explicitApproval"]?.GetValue<bool>() ?? false;
                var source = Register(id);
                try { return await _workflow!.RunAsync(session, approved, source.Token); }
                finally { Release(id, source); }
            }
            case "skipCurrentStep":
                return await _workflow!.SkipCurrentStepAsync(
                    await RequiredSessionAsync(parameters));
            case "cancelSession":
                return await _workflow!.CancelSessionAsync(
                    await RequiredSessionAsync(parameters));
            case "rollbackTransaction":
                return await _workflow!.RollbackTransactionAsync(
                    await RequiredSessionAsync(parameters),
                    RequiredString(parameters, "transactionId"));
            case "rollbackAllTransactions":
                return await _workflow!.RollbackAllTransactionsAsync(
                    await RequiredSessionAsync(parameters));
            case "resumeLatest":
                return await _store!.LoadLatestAsync();
            case "listSessions":
                return await _store!.ListAsync();
            case "loadSession":
                return await _store!.LoadAsync(RequiredString(parameters, "sessionId"));
            case "deleteSession":
                return new { deleted = await _store!.DeleteAsync(
                    RequiredString(parameters, "sessionId")) };
            case "listModels":
            {
                var source = Register("models");
                try { return await _llm!.ListModelsAsync(source.Token); }
                finally { Release("models", source); }
            }
            case "gitStatus":
            {
                var source = Register("git-status");
                try { return await _git!.StatusAsync(source.Token); }
                finally { Release("git-status", source); }
            }
            case "gitPush":
            {
                var session = await RequiredSessionAsync(parameters);
                var approved = parameters["explicitApproval"]?.GetValue<bool>() ?? false;
                var source = Register("git-push");
                try
                {
                    var message = await _git!.PushAsync(session, approved, source.Token);
                    session.History.Add(new DeveloperHistoryEvent
                    {
                        Type = "GitBranchPushed", Message = message
                    });
                    await _store!.SaveAsync(session);
                    await WriteNotificationAsync("sessionUpdated", session);
                    return new { success = true, message };
                }
                finally { Release("git-push", source); }
            }
            case "githubCreatePullRequest":
            {
                var session = await RequiredSessionAsync(parameters);
                var approved = parameters["explicitApproval"]?.GetValue<bool>() ?? false;
                var source = Register("github-pr");
                try
                {
                    var message = await _git!.CreatePullRequestAsync(
                        session, approved, source.Token);
                    session.History.Add(new DeveloperHistoryEvent
                    {
                        Type = "GitHubPullRequestCreated", Message = message
                    });
                    await _store!.SaveAsync(session);
                    await WriteNotificationAsync("sessionUpdated", session);
                    return new { success = true, message };
                }
                finally { Release("github-pr", source); }
            }
            case "getBudget":
                return _workflow!.CalculateBudget(
                    parameters["prompt"]?.GetValue<string>() ?? "",
                    parameters["context"]?.GetValue<string>() ?? "");
            case "compile":
            {
                var kind = parameters["kind"]?.GetValue<string>() ?? CompilationKinds.Validation;
                var paths = parameters["paths"]?.AsArray()
                    .Select(node => _workspace!.Resolve(
                        node?.GetValue<string>() ?? "")).ToArray();
                var source = Register("compile");
                try { return await _roslyn!.CompileAsync(kind, paths, source.Token); }
                finally { Release("compile", source); }
            }
            case "analyze":
            {
                var paths = parameters["paths"]?.AsArray()
                    .Select(node => _workspace!.Resolve(node?.GetValue<string>() ?? "")) ?? [];
                return _roslyn!.AnalyzeFiles(paths);
            }
            case "cancel":
            {
                var id = RequiredString(parameters, "sessionId");
                return new { cancelled = _runs.TryGetValue(id, out var source) && Cancel(source) };
            }
            default:
                throw new InvalidOperationException($"Unknown JSON-RPC method: {method}");
        }
    }

    private CancellationTokenSource Register(string key)
    {
        if (_runs.TryRemove(key, out var existing)) existing.Cancel();
        var source = new CancellationTokenSource();
        _runs[key] = source;
        source.Token.Register(() => _runs.TryRemove(key, out _));
        return source;
    }

    private void Release(string key, CancellationTokenSource source)
    {
        _runs.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, source));
        source.Dispose();
    }

    private static bool Cancel(CancellationTokenSource source)
    {
        source.Cancel();
        return true;
    }

    private void EnsureInitialized()
    {
        if (_settings is null || _workspace is null || _store is null ||
            _roslyn is null || _workflow is null || _llm is null || _git is null)
            throw new InvalidOperationException("Backend is not initialized.");
    }

    private async Task<DevelopmentSession> RequiredSessionAsync(JsonObject parameters)
    {
        var id = RequiredString(parameters, "sessionId");
        return await _store!.LoadAsync(id) ??
               throw new InvalidOperationException("Session not found: " + id);
    }

    private static string RequiredString(JsonObject value, string property)
    {
        var result = value[property]?.GetValue<string>()?.Trim();
        return string.IsNullOrWhiteSpace(result)
            ? throw new InvalidOperationException($"{property} is required.") : result;
    }

    private static bool IsVisualStudioInstallPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            .TrimEnd(Path.DirectorySeparatorChar);
        return Path.GetFullPath(path).StartsWith(
            Path.Combine(programFiles, "Microsoft Visual Studio") + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task WriteResultAsync(JsonNode id, object? result) =>
        await WriteAsync(new { jsonrpc = "2.0", id, result });

    public async Task WriteErrorAsync(JsonNode? id, int code, string message,
        string? data = null) => await WriteAsync(new
        {
            jsonrpc = "2.0", id,
            error = new { code, message, data }
        });

    private async Task WriteNotificationAsync(string method, object parameters) =>
        await WriteAsync(new { jsonrpc = "2.0", method, @params = parameters });

    private async Task WriteAsync(object value)
    {
        var text = JsonSerializer.Serialize(value, JsonDefaults.Options);
        await _output.WaitAsync();
        try
        {
            await Console.Out.WriteLineAsync(text);
            await Console.Out.FlushAsync();
        }
        finally { _output.Release(); }
    }
}
