using System.Text.Json.Nodes;

namespace LocalAI.Developer.Backend;

public static class SessionStatuses
{
    public const string Created = "Created";
    public const string Planning = "Planning";
    public const string Ready = "Ready";
    public const string Running = "Running";
    public const string AwaitingApproval = "AwaitingApproval";
    public const string Compiling = "Compiling";
    public const string Repairing = "Repairing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

public static class StepStatuses
{
    public const string Pending = "Pending";
    public const string GeneratingPatch = "GeneratingPatch";
    public const string AwaitingApproval = "AwaitingApproval";
    public const string Applying = "Applying";
    public const string Compiling = "Compiling";
    public const string Repairing = "Repairing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}

public static class CompilationKinds
{
    public const string Step = "StepCompilation";
    public const string Validation = "ValidationCompilation";
}

public sealed class BackendSettings
{
    public string WorkspaceRoot { get; set; } = "";
    public string StorageDirectory { get; set; } = "";
    public string ProviderName { get; set; } = "LMStudio";
    public string BaseUrl { get; set; } = "http://127.0.0.1:1234/v1";
    public string Model { get; set; } = "local-model";
    public string ApiKey { get; set; } = "";
    public string CompileExecutable { get; set; } = "dotnet";
    public string[] CompileArguments { get; set; } = ["build", "--nologo"];
    public string ApprovalMode { get; set; } = "autoLowRisk";
    public int MaxPlanSteps { get; set; } = 12;
    public int MaxRepairAttempts { get; set; } = 2;
    public int MaxFilesPerPatch { get; set; } = 12;
    public int LlmRequestTimeoutSeconds { get; set; } = 600;
    public GitWorkflowPolicy Git { get; set; } = new();
}

public sealed class GitWorkflowPolicy
{
    public bool Enabled { get; set; }
    public bool RequireCleanStart { get; set; } = true;
    public bool CreateBranch { get; set; } = true;
    public bool CommitEachStep { get; set; } = true;
    public string BranchPrefix { get; set; } = "unity-ai/";
    public string RemoteName { get; set; } = "origin";
}

public sealed class GitWorkflowState
{
    public bool Active { get; set; }
    public bool Completed { get; set; }
    public string RepositoryRoot { get; set; } = "";
    public string OriginalBranch { get; set; } = "";
    public string WorkflowBranch { get; set; } = "";
    public string BaseCommit { get; set; } = "";
    public string LastCommit { get; set; } = "";
    public DateTime? StartedAtUtc { get; set; }
    public List<string> BaselineDirtyFiles { get; set; } = [];
    public List<GitCommitRecord> Commits { get; set; } = [];
}

public sealed class GitCommitRecord
{
    public string Hash { get; set; } = "";
    public string StepId { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class DevelopmentSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string WorkspaceRoot { get; set; } = "";
    public string Goal { get; set; } = "";
    public string Status { get; set; } = SessionStatuses.Created;
    public DevelopmentPlan? Plan { get; set; }
    public string? CurrentStepId { get; set; }
    public PreparedPatch? PendingPatch { get; set; }
    public List<PreparedPatch> ActiveTransaction { get; set; } = [];
    public List<DevelopmentTransaction> Transactions { get; set; } = [];
    public int RepairAttempts { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<WorkflowLog> Logs { get; set; } = [];
    public BudgetSnapshot? Budget { get; set; }
    public string ProviderName { get; set; } = "";
    public string ModelName { get; set; } = "";
    public GitWorkflowPolicy GitPolicy { get; set; } = new();
    public GitWorkflowState GitState { get; set; } = new();
    public List<DeveloperHistoryEvent> History { get; set; } = [];
}

public sealed class DevelopmentTransaction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string StepId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "Applied";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? RolledBackAtUtc { get; set; }
    public List<PreparedPatch> Patches { get; set; } = [];
}

public sealed class DeveloperHistoryEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public string StepId { get; set; } = "";
    public object? Details { get; set; }
}

public sealed class SessionSummary
{
    public string Id { get; set; } = "";
    public string Goal { get; set; } = "";
    public string Status { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public string ModelName { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int HistoryEventCount { get; set; }
}

public sealed class DevelopmentPlan
{
    public string Summary { get; set; } = "";
    public string Status { get; set; } = "Ready";
    public List<DevelopmentStep> Steps { get; set; } = [];
}

public sealed class DevelopmentStep
{
    public string Id { get; set; } = "";
    public int Order { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Kind { get; set; } = "patch";
    public string Risk { get; set; } = "medium";
    public string[] Targets { get; set; } = [];
    public string Status { get; set; } = StepStatuses.Pending;
    public int RepairAttempts { get; set; }
    public string LastError { get; set; } = "";
}

public sealed class WorkflowLog
{
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public object? Details { get; set; }
}

public class PatchDocument
{
    public string Summary { get; set; } = "";
    public List<PatchFile> Files { get; set; } = [];
}

public sealed class PatchFile
{
    public string Path { get; set; } = "";
    public string Operation { get; set; } = "";
    public string ExpectedSha256 { get; set; } = "";
    public string Content { get; set; } = "";
    public string Search { get; set; } = "";
    public string Replacement { get; set; } = "";
    public string? Before { get; set; }
}

public sealed class PreparedPatch : PatchDocument
{
    public string StepId { get; set; } = "";
    public string Risk { get; set; } = "medium";
    public string Kind { get; set; } = "Patch";
}

public sealed class CompilationResult
{
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public bool InfrastructureFailure { get; set; }
    public string BuildTarget { get; set; } = "";
    public string Kind { get; set; } = "";
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public long DurationMs { get; set; }
    public string Backend { get; set; } = "Roslyn+Build";
    public List<RoslynDiagnostic> RoslynDiagnostics { get; set; } = [];
}

public sealed class RoslynDiagnostic
{
    public string Id { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
    public string Path { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
}

public sealed class JsonRpcRequest
{
    public string Jsonrpc { get; set; } = "2.0";
    public JsonNode? Id { get; set; }
    public string Method { get; set; } = "";
    public JsonObject? Params { get; set; }
}
