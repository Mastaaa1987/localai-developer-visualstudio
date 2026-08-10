using System.Text.Json.Nodes;
using System.Net;
using System.Text;
using System.Text.Json;
using LocalAI.Developer.Backend;

var tests = new List<(string Name, Action Run)>
{
    ("provider quota profiles", TestProfiles),
    ("token and character quota", TestBudget),
    ("safe transactional patch", TestPatch),
    ("completed transaction rollback", TestCompletedTransactionRollback),
    ("Roslyn syntax diagnostics", TestRoslyn),
    ("JSON-RPC backend dispatch", TestJsonRpc),
    ("provider transport and model discovery", () => TestProviders().GetAwaiter().GetResult()),
    ("malformed model JSON escape repair", () => TestMalformedModelJsonEscape().GetAwaiter().GetResult()),
    ("invalid plan step count is repaired", () => TestInvalidPlanStepCountRepair().GetAwaiter().GetResult()),
    ("session history persistence", () => TestSessions().GetAwaiter().GetResult()),
    ("safe Git workflow", () => TestGitWorkflow().GetAwaiter().GetResult()),
    ("nested build target resolution", () => TestBuildTargetResolution().GetAwaiter().GetResult()),
    ("C# repair workflow", () => TestWorkflow().GetAwaiter().GetResult()),
    ("failed workflow retains changes until manual rollback", () =>
        TestFailedWorkflowManualRollback().GetAwaiter().GetResult()),
    ("ambiguous replace regenerates before approval", () =>
        TestAmbiguousReplaceRegeneration().GetAwaiter().GetResult()),
    ("invalid C# syntax regenerates before approval", () =>
        TestInvalidSyntaxRegeneration().GetAwaiter().GetResult()),
    ("all transactions rollback atomically", () =>
        TestRollbackAllTransactions().GetAwaiter().GetResult())
};
var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}");
    }
}
return failures == 0 ? 0 : 1;

static void TestProfiles()
{
    Equal(16384, ContextProfileResolver.Resolve("Ollama", "llama").ContextWindowTokens);
    Equal(262144, ContextProfileResolver.Resolve("Mistral", "mistral-latest").ContextWindowTokens);
    Equal(65536, ContextProfileResolver.Resolve("OpenAI", "gpt").ContextWindowTokens);
}

static void TestBudget()
{
    var profile = ContextProfileResolver.Resolve("LMStudio", "local");
    var budget = new BudgetService().Calculate(profile, "Create { value; }", new string('x', 3500));
    True(budget.EstimatedContextTokens == 1000, "Context estimate must use 3.5 characters/token.");
    True(budget.EstimatedTotalRequestTokens >= 8500, "Reserved quotas were omitted.");
    True(budget.TokenUsagePercent > 0 && budget.CharacterUsagePercent > 0,
        "Quota percentages were not calculated.");
}

static async Task TestInvalidPlanStepCountRepair()
{
    var root = Path.Combine(Path.GetTempPath(), "unityai-plan-repair-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var settings = new BackendSettings
        {
            WorkspaceRoot = root,
            StorageDirectory = Path.Combine(root, "sessions"),
            ProviderName = "Mistral",
            Model = "test",
            BaseUrl = "http://fake/v1",
            MaxPlanSteps = 2
        };
        var responses = new Queue<string>([
            "{\"summary\":\"invalid\",\"steps\":[]}",
            "{\"summary\":\"repaired\",\"steps\":[{\"id\":\"one\",\"title\":\"One\",\"description\":\"Create file\",\"kind\":\"patch\",\"risk\":\"low\",\"targets\":[\"One.cs\"]}]}"
        ]);
        var workflow = new WorkflowService(settings, new WorkspaceService(settings),
            new SessionStore(settings),
            new LlmClient(settings, new HttpClient(new FakeLlmHandler(responses))),
            new RoslynBackend(settings), new GitWorkflowService(settings),
            (_, _) => Task.CompletedTask);

        var session = await workflow.CreatePlanAsync("Create one file", CancellationToken.None);
        Equal(SessionStatuses.Ready, session.Status);
        Equal(1, session.Plan!.Steps.Count);
        True(session.History.Any(item => item.Type == "PlanRepairRequested"),
            "Invalid plan did not trigger a repair request.");
    }
    finally { DeleteTree(root); }
}

static void TestPatch()
{
    var root = Path.Combine(Path.GetTempPath(), "unityai-csharp-patch-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        File.WriteAllText(Path.Combine(root, "Probe.cs"), "old");
        var settings = new BackendSettings { WorkspaceRoot = root, MaxFilesPerPatch = 2 };
        var workspace = new WorkspaceService(settings);
        var raw = JsonNode.Parse($$"""
        {"files":[{"path":"Probe.cs","operation":"update","expectedSha256":"{{WorkspaceService.Sha256("old")}}","content":"new"}]}
        """)!.AsObject();
        var prepared = workspace.Prepare(workspace.ParsePatch(raw), "step", "medium");
        workspace.Apply(prepared);
        Equal("new", File.ReadAllText(Path.Combine(root, "Probe.cs")));
        workspace.Rollback(prepared.Files);
        Equal("old", File.ReadAllText(Path.Combine(root, "Probe.cs")));
        Throws(() => workspace.Normalize("../outside.cs"));
        Throws(() => workspace.Normalize("Library/cache.txt"));
        Throws(() => workspace.ParsePatch(JsonNode.Parse("""
            {"files":[{"path":"Probe.cs","operation":"update","expectedSha256":"hash","content":""}]}
            """)!.AsObject()));
        var combined = workspace.ParsePatch(JsonNode.Parse($$"""
            {"files":[{"path":"Probe.cs","operation":"create or update","expectedSha256":"{{WorkspaceService.Sha256("old")}}","content":"new"}]}
            """)!.AsObject());
        Equal("update", combined.Files[0].Operation);
        var combinedCreate = workspace.ParsePatch(JsonNode.Parse("""
            {"files":[{"path":"NewProbe.cs","operation":"create/update","expectedSha256":"","content":"new"}]}
            """)!.AsObject());
        Equal("create", combinedCreate.Files[0].Operation);
        var replacement = workspace.ParsePatch(JsonNode.Parse($$"""
            {"files":[{"path":"Probe.cs","operation":"replace","expectedSha256":"{{WorkspaceService.Sha256("old")}}","search":"old","replacement":"new"}]}
            """)!.AsObject());
        var preparedReplacement = workspace.Prepare(replacement, "replace", "medium");
        Equal("update", preparedReplacement.Files[0].Operation);
        Equal("new", preparedReplacement.Files[0].Content);
    }
    finally { DeleteTree(root); }
}

static void TestCompletedTransactionRollback()
{
    var root = Path.Combine(Path.GetTempPath(), "unityai-transaction-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var path = Path.Combine(root, "Probe.cs");
        File.WriteAllText(path, "old");
        var workspace = new WorkspaceService(new BackendSettings { WorkspaceRoot = root });
        PreparedPatch CreatePatch() => new()
        {
            StepId = "step",
            Files = [new PatchFile
            {
                Path = "Probe.cs", Operation = "update", Before = "old", Content = "new"
            }]
        };
        var patch = CreatePatch();
        workspace.Apply(patch);
        workspace.RollbackTransaction([patch]);
        Equal("old", File.ReadAllText(path));

        patch = CreatePatch();
        workspace.Apply(patch);
        File.WriteAllText(path, "newer external change");
        Throws(() => workspace.RollbackTransaction([patch]));
        Equal("newer external change", File.ReadAllText(path));
    }
    finally { DeleteTree(root); }
}

static void TestRoslyn()
{
    var root = Path.Combine(Path.GetTempPath(), "unityai-csharp-roslyn-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var file = Path.Combine(root, "Broken.cs");
        File.WriteAllText(file, "public class Broken { void M( }");
        var diagnostics = new RoslynBackend(new BackendSettings { WorkspaceRoot = root })
            .AnalyzeFiles([file]);
        True(diagnostics.Any(item => item.Severity == "Error"),
            "Roslyn did not report invalid C# syntax.");
    }
    finally { DeleteTree(root); }
}

static async Task TestBuildTargetResolution()
{
    var root = Path.Combine(Path.GetTempPath(), "unityai-build-target-" + Guid.NewGuid().ToString("N"));
    var project = Path.Combine(root, "NestedProject");
    Directory.CreateDirectory(project);
    try
    {
        var projectPath = Path.Combine(project, "NestedProject.csproj");
        var sourcePath = Path.Combine(project, "Probe.cs");
        File.WriteAllText(projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(sourcePath, "public class Probe { }");
        var backend = new RoslynBackend(new BackendSettings
        {
            WorkspaceRoot = root,
            CompileExecutable = "dotnet",
            CompileArguments = ["build", "--nologo"]
        });
        var result = await backend.CompileAsync(
            CompilationKinds.Step, [sourcePath], CancellationToken.None);
        True(result.Success, result.Output);
        Equal(projectPath, result.BuildTarget);

        var empty = Path.Combine(root, "Empty");
        Directory.CreateDirectory(empty);
        var missingTarget = await new RoslynBackend(new BackendSettings
        {
            WorkspaceRoot = empty,
            CompileExecutable = "dotnet",
            CompileArguments = ["build", "--nologo"]
        }).CompileAsync(CompilationKinds.Validation, null, CancellationToken.None);
        True(missingTarget.Success && missingTarget.Skipped,
            "Compilation must be skipped when no solution or project exists.");
    }
    finally { DeleteTree(root); }
}

static void TestJsonRpc()
{
    var root = Path.Combine(Path.GetTempPath(), "unityai-csharp-rpc-" + Guid.NewGuid().ToString("N"));
    var storage = Path.Combine(root, "sessions");
    Directory.CreateDirectory(root);
    var original = Console.Out;
    var output = new StringWriter();
    try
    {
        Console.SetOut(output);
        var server = new BackendServer();
        server.HandleAsync(new JsonRpcRequest
        {
            Id = JsonValue.Create(1), Method = "initialize",
            Params = JsonNode.Parse($$"""
            {"workspaceRoot":"{{root.Replace("\\", "\\\\")}}","storageDirectory":"{{storage.Replace("\\", "\\\\")}}","providerName":"LMStudio","model":"local"}
            """)!.AsObject()
        }).GetAwaiter().GetResult();
        server.HandleAsync(new JsonRpcRequest
        {
            Id = JsonValue.Create(2), Method = "getBudget",
            Params = JsonNode.Parse("{\"prompt\":\"test\",\"context\":\"class Probe {}\"}")!.AsObject()
        }).GetAwaiter().GetResult();
        var lines = output.ToString().Split(Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        True(lines.Any(line => line.Contains("\"initialized\":true", StringComparison.Ordinal)),
            "Initialize JSON-RPC response is missing.");
        True(lines.Any(line => line.Contains("\"estimatedTotalRequestTokens\"", StringComparison.Ordinal)),
            "Budget JSON-RPC response is missing.");
    }
    finally
    {
        Console.SetOut(original);
        Directory.Delete(root, true);
    }
}

static async Task TestWorkflow()
{
    var root = Path.Combine(Path.GetTempPath(), "unityai-csharp-workflow-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        const string original = "public class Probe { }";
        const string broken = "public class Probe { MissingType Value; }";
        const string repaired = "public class Probe { }";
        File.WriteAllText(Path.Combine(root, "Probe.cs"), original);
        File.WriteAllText(Path.Combine(root, "Probe.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        var settings = new BackendSettings
        {
            WorkspaceRoot = root,
            StorageDirectory = Path.Combine(root, "sessions"),
            ProviderName = "LMStudio",
            Model = "test",
            BaseUrl = "http://fake/v1",
            CompileExecutable = "dotnet",
            CompileArguments = ["build", "--nologo"],
            MaxRepairAttempts = 2
        };
        var responses = new Queue<string>([
            JsonSerializer.Serialize(new
            {
                summary = "break",
                files = new[] { new
                {
                    path = "Probe.cs", operation = "update",
                    expectedSha256 = WorkspaceService.Sha256(original), content = broken
                } }
            }),
            JsonSerializer.Serialize(new
            {
                summary = "repair",
                files = new[] { new
                {
                    path = "Probe.cs", operation = "update",
                    expectedSha256 = WorkspaceService.Sha256(broken), content = repaired
                } }
            })
        ]);
        var client = new HttpClient(new FakeLlmHandler(responses));
        var workspace = new WorkspaceService(settings);
        var store = new SessionStore(settings);
        var roslyn = new RoslynBackend(settings);
        var workflow = new WorkflowService(settings, workspace, store,
            new LlmClient(settings, client), roslyn,
            new GitWorkflowService(settings), (_, _) => Task.CompletedTask);
        var session = new DevelopmentSession
        {
            Goal = "Repair Probe", WorkspaceRoot = root,
            Plan = new DevelopmentPlan
            {
                Steps =
                [
                    new DevelopmentStep
                    {
                        Id = "patch", Order = 1, Title = "Patch Probe",
                        Description = "Patch and repair", Kind = "patch",
                        Risk = "medium", Targets = ["Probe.cs"]
                    }
                ]
            }
        };
        await store.SaveAsync(session);
        await workflow.RunAsync(session, false, CancellationToken.None);
        Equal(SessionStatuses.AwaitingApproval, session.Status);
        await workflow.RunAsync(session, true, CancellationToken.None);
        Equal(SessionStatuses.AwaitingApproval, session.Status);
        True(session.PendingPatch?.Kind == "Repair",
            "Repair must require its own approval after the original patch approval was consumed.");
        await workflow.RunAsync(session, true, CancellationToken.None);
        Equal(SessionStatuses.Completed, session.Status);
        Equal(1, session.RepairAttempts);
        Equal(repaired, File.ReadAllText(Path.Combine(root, "Probe.cs")));
    }
    finally { DeleteTree(root); }
}

static async Task TestFailedWorkflowManualRollback()
{
    var root = Path.Combine(Path.GetTempPath(), "unityai-manual-rollback-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        const string original = "public class Probe { }";
        const string broken = "public class Probe { MissingType Value; }";
        File.WriteAllText(Path.Combine(root, "Probe.cs"), original);
        File.WriteAllText(Path.Combine(root, "Probe.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        var settings = new BackendSettings
        {
            WorkspaceRoot = root, StorageDirectory = Path.Combine(root, "sessions"),
            ProviderName = "LMStudio", Model = "test", BaseUrl = "http://fake/v1",
            CompileExecutable = "dotnet", CompileArguments = ["build", "--nologo"],
            MaxRepairAttempts = 0
        };
        var responses = new Queue<string>([JsonSerializer.Serialize(new
        {
            summary = "break",
            files = new[] { new
            {
                path = "Probe.cs", operation = "update",
                expectedSha256 = WorkspaceService.Sha256(original), content = broken
            } }
        })]);
        var workspace = new WorkspaceService(settings);
        var store = new SessionStore(settings);
        var workflow = new WorkflowService(settings, workspace, store,
            new LlmClient(settings, new HttpClient(new FakeLlmHandler(responses))),
            new RoslynBackend(settings), new GitWorkflowService(settings),
            (_, _) => Task.CompletedTask);
        var session = new DevelopmentSession
        {
            Goal = "Keep failed patch", WorkspaceRoot = root,
            Plan = new DevelopmentPlan
            {
                Steps = [new DevelopmentStep
                {
                    Id = "patch", Order = 1, Title = "Break Probe",
                    Description = "Test manual rollback", Kind = "patch",
                    Risk = "medium", Targets = ["Probe.cs"]
                }]
            }
        };
        await store.SaveAsync(session);
        await workflow.RunAsync(session, false, CancellationToken.None);
        await workflow.RunAsync(session, true, CancellationToken.None);
        Equal(SessionStatuses.Failed, session.Status);
        Equal(broken, File.ReadAllText(Path.Combine(root, "Probe.cs")));
        Equal("FailedApplied", session.Transactions.Single().Status);
        await workflow.RollbackTransactionAsync(session, session.Transactions.Single().Id);
        Equal(original, File.ReadAllText(Path.Combine(root, "Probe.cs")));
        Equal("RolledBack", session.Transactions.Single().Status);
        Equal(SessionStatuses.Ready, session.Status);
    }
    finally { DeleteTree(root); }
}

static async Task TestAmbiguousReplaceRegeneration()
{
    var root = Path.Combine(Path.GetTempPath(), "unityai-regenerate-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        const string original = "public class Probe { // marker\n// marker\n}";
        const string corrected = "public class Probe { }";
        File.WriteAllText(Path.Combine(root, "Probe.cs"), original);
        var settings = new BackendSettings
        {
            WorkspaceRoot = root, StorageDirectory = Path.Combine(root, "sessions"),
            ProviderName = "LMStudio", Model = "test", BaseUrl = "http://fake/v1"
        };
        var responses = new Queue<string>([
            JsonSerializer.Serialize(new
            {
                summary = "ambiguous", files = new[] { new
                {
                    path = "Probe.cs", operation = "replace",
                    expectedSha256 = WorkspaceService.Sha256(original),
                    search = "marker", replacement = "value"
                } }
            }),
            JsonSerializer.Serialize(new
            {
                summary = "unique", files = new[] { new
                {
                    path = "Probe.cs", operation = "replace",
                    expectedSha256 = WorkspaceService.Sha256(original),
                    search = original, replacement = corrected
                } }
            })
        ]);
        var store = new SessionStore(settings);
        var workflow = new WorkflowService(settings, new WorkspaceService(settings), store,
            new LlmClient(settings, new HttpClient(new FakeLlmHandler(responses))),
            new RoslynBackend(settings), new GitWorkflowService(settings),
            (_, _) => Task.CompletedTask);
        var session = new DevelopmentSession
        {
            Goal = "Regenerate patch", WorkspaceRoot = root,
            Plan = new DevelopmentPlan
            {
                Steps = [new DevelopmentStep
                {
                    Id = "patch", Order = 1, Title = "Replace marker",
                    Description = "Use a unique replacement", Kind = "patch",
                    Risk = "medium", Targets = ["Probe.cs"]
                }]
            }
        };
        await store.SaveAsync(session);
        await workflow.RunAsync(session, false, CancellationToken.None);
        Equal(SessionStatuses.AwaitingApproval, session.Status);
        Equal(corrected, session.PendingPatch!.Files.Single().Content);
        Equal(0, responses.Count);
        Equal(original, File.ReadAllText(Path.Combine(root, "Probe.cs")));
    }
    finally { DeleteTree(root); }
}

static async Task TestInvalidSyntaxRegeneration()
{
    var root = Path.Combine(Path.GetTempPath(), "unityai-syntax-regenerate-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        const string original = "public class Probe { }";
        const string corrected = "public class Probe { public int Value; }";
        File.WriteAllText(Path.Combine(root, "Probe.cs"), original);
        var settings = new BackendSettings
        {
            WorkspaceRoot = root, StorageDirectory = Path.Combine(root, "sessions"),
            ProviderName = "LMStudio", Model = "test", BaseUrl = "http://fake/v1"
        };
        string Response(string content) => JsonSerializer.Serialize(new
        {
            summary = "syntax", files = new[] { new
            {
                path = "Probe.cs", operation = "update",
                expectedSha256 = WorkspaceService.Sha256(original), content
            } }
        });
        var responses = new Queue<string>([Response("public class Probe {"), Response(corrected)]);
        var store = new SessionStore(settings);
        var workflow = new WorkflowService(settings, new WorkspaceService(settings), store,
            new LlmClient(settings, new HttpClient(new FakeLlmHandler(responses))),
            new RoslynBackend(settings), new GitWorkflowService(settings),
            (_, _) => Task.CompletedTask);
        var session = new DevelopmentSession
        {
            Goal = "Regenerate invalid syntax", WorkspaceRoot = root,
            Plan = new DevelopmentPlan
            {
                Steps = [new DevelopmentStep
                {
                    Id = "patch", Order = 1, Title = "Update Probe",
                    Description = "Add a field", Kind = "patch", Risk = "medium",
                    Targets = ["Probe.cs"]
                }]
            }
        };
        await store.SaveAsync(session);
        await workflow.RunAsync(session, false, CancellationToken.None);
        Equal(SessionStatuses.AwaitingApproval, session.Status);
        Equal(corrected, session.PendingPatch!.Files.Single().Content);
        Equal(0, responses.Count);
        Equal(original, File.ReadAllText(Path.Combine(root, "Probe.cs")));
    }
    finally { DeleteTree(root); }
}

static async Task TestRollbackAllTransactions()
{
    var root = Path.Combine(Path.GetTempPath(), "unityai-rollback-all-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var path = Path.Combine(root, "Probe.cs");
        File.WriteAllText(path, "original");
        var settings = new BackendSettings
        {
            WorkspaceRoot = root, StorageDirectory = Path.Combine(root, "sessions"),
            ProviderName = "LMStudio", Model = "test", BaseUrl = "http://fake/v1"
        };
        var workspace = new WorkspaceService(settings);
        var first = new PreparedPatch
        {
            StepId = "one", Files = [new PatchFile
            {
                Path = "Probe.cs", Operation = "update", Before = "original", Content = "middle"
            }]
        };
        var second = new PreparedPatch
        {
            StepId = "two", Files = [new PatchFile
            {
                Path = "Probe.cs", Operation = "update", Before = "middle", Content = "final"
            }]
        };
        workspace.Apply(first);
        workspace.Apply(second);
        var session = new DevelopmentSession
        {
            Goal = "Rollback all", WorkspaceRoot = root, Status = SessionStatuses.Failed,
            Plan = new DevelopmentPlan
            {
                Status = "Failed", Steps =
                [
                    new DevelopmentStep { Id = "one", Status = StepStatuses.Completed },
                    new DevelopmentStep { Id = "two", Status = StepStatuses.Completed }
                ]
            },
            Transactions =
            [
                new DevelopmentTransaction { StepId = "one", Status = "FailedApplied", Patches = [first] },
                new DevelopmentTransaction { StepId = "two", Status = "FailedApplied", Patches = [second] }
            ]
        };
        var store = new SessionStore(settings);
        var workflow = new WorkflowService(settings, workspace, store,
            new LlmClient(settings, new HttpClient(new FakeLlmHandler(new Queue<string>()))),
            new RoslynBackend(settings), new GitWorkflowService(settings),
            (_, _) => Task.CompletedTask);
        await workflow.RollbackAllTransactionsAsync(session);
        Equal("original", File.ReadAllText(path));
        True(session.Transactions.All(item => item.Status == "RolledBack"),
            "Not all transactions were marked rolled back.");
        Equal(SessionStatuses.Ready, session.Status);
        True(session.Plan.Steps.All(item => item.Status == StepStatuses.Pending),
            "Rolled-back completed steps were not reset.");
    }
    finally { DeleteTree(root); }
}

static async Task TestProviders()
{
    var handler = new RoutingHttpHandler(request =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/api/tags"))
            return "{\"models\":[{\"name\":\"llama3.1\"},{\"name\":\"qwen\"}]}";
        True(request.RequestUri.AbsolutePath.EndsWith("/api/chat"),
            "Ollama must use its native chat endpoint.");
        return "{\"message\":{\"content\":\"{\\\"summary\\\":\\\"ok\\\",\\\"steps\\\":[]}\"}}";
    });
    var settings = new BackendSettings
    {
        ProviderName = "Ollama", BaseUrl = "http://127.0.0.1:11434/v1",
        Model = "llama3.1"
    };
    var client = new LlmClient(settings, new HttpClient(handler));
    var models = await client.ListModelsAsync(CancellationToken.None);
    Equal(2, models.Length);
    Equal("llama3.1", models[0]);
    var plan = await client.CreatePlanAsync("test", "workspace", CancellationToken.None);
    Equal("ok", plan["summary"]!.GetValue<string>());
}

static async Task TestMalformedModelJsonEscape()
{
    const string malformedPatch =
        "{\"summary\":\"apostrophe\",\"files\":[{\"path\":\"Probe.cs\",\"operation\":\"create\",\"expectedSha256\":\"\",\"content\":\"if (value == \\\'x\\\') {}\"}]}";
    var handler = new RoutingHttpHandler(request =>
    {
        var requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var requestJson = JsonDocument.Parse(requestBody);
        Equal("json_object", requestJson.RootElement.GetProperty("response_format")
            .GetProperty("type").GetString()!);
        return JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = malformedPatch } } }
        });
    });
    var settings = new BackendSettings
    {
        ProviderName = "LMStudio", BaseUrl = "http://127.0.0.1:1234/v1",
        Model = "local"
    };
    var client = new LlmClient(settings, new HttpClient(handler));
    var patch = await client.CreatePatchAsync("test", new DevelopmentStep
    {
        Id = "patch", Title = "Patch", Description = "Patch Probe.cs",
        Kind = "patch", Targets = ["Probe.cs"]
    }, "context", CancellationToken.None);
    Equal("if (value == \\'x\\') {}",
        patch["files"]![0]!["content"]!.GetValue<string>());
}

static async Task TestSessions()
{
    var root = Path.Combine(Path.GetTempPath(), "unityai-sessions-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var store = new SessionStore(new BackendSettings { StorageDirectory = root });
        var session = new DevelopmentSession
        {
            Goal = "Persist history", Status = SessionStatuses.Completed,
            ProviderName = "LMStudio", ModelName = "local"
        };
        session.History.Add(new DeveloperHistoryEvent
        {
            Type = "Completed", Message = "done"
        });
        await store.SaveAsync(session);
        var summaries = await store.ListAsync();
        Equal(1, summaries.Count);
        Equal(1, summaries[0].HistoryEventCount);
        var loaded = await store.LoadAsync(session.Id);
        Equal("Completed", loaded!.History[0].Type);
        True(await store.DeleteAsync(session.Id), "Session deletion failed.");
        Equal(0, (await store.ListAsync()).Count);
    }
    finally { DeleteTree(root); }
}

static async Task TestGitWorkflow()
{
    var root = Path.Combine(Path.GetTempPath(), "unityai-git-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "unityai-tests@example.invalid");
        RunGit(root, "config", "user.name", "LocalAI Developer Tests");
        File.WriteAllText(Path.Combine(root, "Probe.cs"), "old");
        RunGit(root, "add", "Probe.cs");
        RunGit(root, "commit", "-m", "initial");
        var settings = new BackendSettings
        {
            WorkspaceRoot = root,
            Git = new GitWorkflowPolicy { Enabled = true, BranchPrefix = "test/" }
        };
        var service = new GitWorkflowService(settings);
        var session = new DevelopmentSession
        {
            Id = Guid.NewGuid().ToString("N"), Goal = "Change probe",
            GitPolicy = settings.Git
        };
        await service.StartAsync(session, CancellationToken.None);
        True(session.GitState.Active, "Git workflow did not start.");
        True(session.GitState.WorkflowBranch.StartsWith("test/"), "Branch prefix was ignored.");
        var workspace = new WorkspaceService(settings);
        var patch = workspace.Prepare(new PatchDocument
        {
            Files = [new PatchFile
            {
                Path = "Probe.cs", Operation = "update",
                ExpectedSha256 = WorkspaceService.Sha256("old"), Content = "new"
            }]
        }, "step", "medium");
        await service.ValidateBeforeApplyAsync(session, patch, CancellationToken.None);
        workspace.Apply(patch);
        var step = new DevelopmentStep { Id = "step", Title = "Change probe" };
        var message = await service.CommitStepAsync(session, step, [patch], CancellationToken.None);
        True(message.StartsWith("Git commit created:"), "Scoped Git commit was not created.");
        True(session.GitState.Commits.Count == 1, "Git commit history was not persisted.");
        await ThrowsAsync(() => service.PushAsync(session, false, CancellationToken.None));
    }
    finally { DeleteTree(root); }
}

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void Throws(Action action)
{
    try { action(); }
    catch { return; }
    throw new InvalidOperationException("Expected operation to throw.");
}

static async Task ThrowsAsync(Func<Task> action)
{
    try { await action(); }
    catch { return; }
    throw new InvalidOperationException("Expected async operation to throw.");
}

static void RunGit(string directory, params string[] arguments)
{
    var info = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "git", WorkingDirectory = directory,
        UseShellExecute = false, RedirectStandardOutput = true,
        RedirectStandardError = true, CreateNoWindow = true
    };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = System.Diagnostics.Process.Start(info)!;
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException("git " + string.Join(' ', arguments) + ": " + error + output);
}

static void DeleteTree(string root)
{
    if (!Directory.Exists(root)) return;
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(root, true);
}

sealed class FakeLlmHandler(Queue<string> responses) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = responses.Dequeue();
        var response = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } }
        });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json")
        });
    }
}

sealed class RoutingHttpHandler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response(request), Encoding.UTF8, "application/json")
        });
}
