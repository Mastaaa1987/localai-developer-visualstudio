using System.Text.Json.Nodes;

namespace LocalAI.Developer.Backend;

public sealed class WorkflowService(
    BackendSettings settings,
    WorkspaceService workspace,
    SessionStore store,
    LlmClient llm,
    RoslynBackend roslyn,
    GitWorkflowService git,
    Func<string, object, Task> notify)
{
    private readonly BudgetService _budgets = new();
    private readonly ContextProfile _profile = ContextProfileResolver.Resolve(
        settings.ProviderName, settings.Model);

    public async Task<DevelopmentSession> CreatePlanAsync(
        string goal, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(goal))
            throw new InvalidOperationException("Goal is required.");
        var session = new DevelopmentSession
        {
            Goal = goal.Trim(), WorkspaceRoot = Path.GetFullPath(settings.WorkspaceRoot),
            ProviderName = settings.ProviderName,
            ModelName = settings.Model,
            GitPolicy = new GitWorkflowPolicy
            {
                Enabled = settings.Git.Enabled,
                RequireCleanStart = settings.Git.RequireCleanStart,
                CreateBranch = settings.Git.CreateBranch,
                CommitEachStep = settings.Git.CommitEachStep,
                BranchPrefix = settings.Git.BranchPrefix,
                RemoteName = settings.Git.RemoteName
            }
        };
        History(session, "SessionCreated", "Developer session created.");
        SetStatus(session, SessionStatuses.Planning, "Generating development plan.");
        await CheckpointAsync(session);
        var summary = workspace.WorkspaceSummary();
        session.Budget = _budgets.Calculate(_profile, goal, summary);
        await notify("budgetUpdated", session.Budget);
        if (session.Budget.ExceedsContextWindow)
            throw new InvalidOperationException(session.Budget.Warning);
        var raw = await llm.CreatePlanAsync(goal, summary, cancellationToken);
        try
        {
            session.Plan = ParsePlan(raw);
        }
        catch (InvalidOperationException firstError)
        {
            Log(session, "planner", "Planner response was invalid; requesting one corrected plan.",
                new { error = firstError.Message, maxPlanSteps = settings.MaxPlanSteps });
            History(session, "PlanRepairRequested", firstError.Message, raw);
            SetStatus(session, SessionStatuses.Planning,
                "Planner response was invalid; generating a corrected plan.");
            await CheckpointAsync(session);
            var repaired = await llm.RepairPlanAsync(
                goal, summary, raw, firstError.Message, cancellationToken);
            try
            {
                session.Plan = ParsePlan(repaired);
            }
            catch (InvalidOperationException secondError)
            {
                var message = "The planner returned an invalid development plan twice. " +
                              secondError.Message;
                SetStatus(session, SessionStatuses.Failed, message);
                History(session, "PlanGenerationFailed", message, repaired);
                await CheckpointAsync(session);
                throw new InvalidOperationException(message, secondError);
            }
        }
        History(session, "PlanCreated", "Development plan created.", session.Plan);
        SetStatus(session, SessionStatuses.Ready, "Development plan is ready.");
        await CheckpointAsync(session);
        return session;
    }

    public async Task<DevelopmentSession> RunAsync(
        DevelopmentSession session, bool explicitApproval,
        CancellationToken cancellationToken)
    {
        if (session.Plan is null) throw new InvalidOperationException("Session has no plan.");
        EnsureSessionWorkspace(session);
        NormalizePlanStepKinds(session.Plan);
        if (session.Transactions.Any(item => item.Status == "FailedApplied"))
            throw new InvalidOperationException(
                "This failed session still has applied changes. Inspect the files and manually roll back " +
                "all FailedApplied transactions before continuing the workflow.");
        if (session.Status is SessionStatuses.Failed or SessionStatuses.Cancelled)
            ResetRolledBackSteps(session);
        if (session.GitPolicy.Enabled && !session.GitState.Active && !session.GitState.Completed)
        {
            try
            {
                var gitMessage = await git.StartAsync(session, cancellationToken);
                History(session, "GitWorkflowStarted", gitMessage, session.GitState);
                await CheckpointAsync(session);
            }
            catch (Exception error)
            {
                SetStatus(session, SessionStatuses.Failed, error.Message);
                History(session, "Failed", error.Message);
                await CheckpointAsync(session);
                return session;
            }
        }
        SetStatus(session, SessionStatuses.Running, "Development workflow started.");
        await CheckpointAsync(session);
        if (session.PendingPatch?.Kind == "Repair" &&
            session.CurrentStepId is not null)
        {
            var repairStep = session.Plan.Steps.FirstOrDefault(item =>
                item.Id == session.CurrentStepId) ??
                throw new InvalidOperationException("Repair step is no longer available.");
            if (!explicitApproval) return session;
            var pendingRepair = session.PendingPatch;
            session.PendingPatch = null;
            await git.ValidateBeforeApplyAsync(session, pendingRepair, cancellationToken);
            Apply(session, repairStep, pendingRepair);
            session.ActiveTransaction.Add(pendingRepair);
            RecordTransaction(session, repairStep, pendingRepair);
            repairStep.Status = StepStatuses.Completed;
            explicitApproval = false;
        }
        foreach (var step in session.Plan.Steps)
        {
            if (step.Status is StepStatuses.Completed or StepStatuses.Skipped) continue;
            cancellationToken.ThrowIfCancellationRequested();
            session.CurrentStepId = step.Id;
            if (step.Kind == "validation")
            {
                step.Status = StepStatuses.Completed;
                Log(session, "compile", $"Deferred validation until the end of the plan: {step.Title}.");
                await CheckpointAsync(session);
                continue;
            }

            var applied = session.ActiveTransaction;
            try
            {
                PreparedPatch prepared;
                if (step.Status == StepStatuses.AwaitingApproval &&
                    session.PendingPatch?.StepId == step.Id)
                {
                    if (!explicitApproval) return session;
                    prepared = session.PendingPatch;
                    session.PendingPatch = null;
                    explicitApproval = false;
                }
                else
                {
                    step.Status = StepStatuses.GeneratingPatch;
                    Log(session, "patch", $"Generating patch for {step.Title}.");
                    await CheckpointAsync(session);
                    var context = workspace.Describe(step.Targets, roslyn);
                    UpdateBudget(session, step.Description, context);
                    Log(session, "llm", $"Waiting for {settings.ProviderName} to generate " +
                        $"the patch (timeout: {settings.LlmRequestTimeoutSeconds} seconds).");
                    await CheckpointAsync(session);
                    prepared = await GeneratePreparedPatchAsync(session, step, context,
                        cancellationToken);
                    if (await AwaitApprovalAsync(session, step, prepared, false))
                        return session;
                }

                await git.ValidateBeforeApplyAsync(session, prepared, cancellationToken);
                Apply(session, step, prepared);
                applied.Add(prepared);
                RecordTransaction(session, step, prepared);
                step.Status = StepStatuses.Completed;
                step.LastError = "";
                Log(session, "step", $"Applied {step.Title}; compilation is deferred until plan completion.");
                await CheckpointAsync(session);
            }
            catch (Exception error)
            {
                try { await git.UnstageAsync(session, applied, CancellationToken.None); }
                catch { }
                MarkAppliedTransactionsFailed(session);
                applied.Clear();
                return await FailAsync(session, step, error.Message +
                    " Applied changes were retained for inspection and require manual rollback.");
            }
        }

        var finalCompilation = await roslyn.CompileAsync(
            CompilationKinds.Validation, null, cancellationToken);
        Log(session, "compile", "Final validation compilation completed.", finalCompilation);
        var finalRepairStep = session.Plan.Steps.LastOrDefault(step =>
            step.Kind != "validation" && step.Status == StepStatuses.Completed);
        while (!finalCompilation.Success &&
               !finalCompilation.InfrastructureFailure &&
               finalRepairStep is not null &&
               finalRepairStep.RepairAttempts < settings.MaxRepairAttempts)
        {
            finalRepairStep.Status = StepStatuses.Repairing;
            finalRepairStep.RepairAttempts++;
            session.RepairAttempts++;
            session.CurrentStepId = finalRepairStep.Id;
            SetStatus(session, SessionStatuses.Repairing,
                $"Repairing final compilation ({finalRepairStep.RepairAttempts}/{settings.MaxRepairAttempts}).");
            var changed = session.ActiveTransaction.SelectMany(patch => patch.Files)
                .Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var repairTargets = finalRepairStep.Targets.Concat(changed)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var context = workspace.Describe(repairTargets, roslyn);
            UpdateBudget(session, finalRepairStep.Description + finalCompilation.Output, context);
            var repairStep = new DevelopmentStep
            {
                Id = finalRepairStep.Id,
                Title = "Repair final validation compilation",
                Description = "Resolve every reported compiler error using only the supplied changed files. " +
                              "Preserve the intended completed plan and do not invent missing types.",
                Kind = "patch", Risk = "medium", Targets = repairTargets
            };
            Log(session, "llm", $"Waiting for {settings.ProviderName} to repair the final compilation " +
                $"(timeout: {settings.LlmRequestTimeoutSeconds} seconds).");
            await CheckpointAsync(session);
            PreparedPatch repair;
            try
            {
                repair = await GeneratePreparedRepairAsync(session, repairStep, context,
                    finalCompilation, cancellationToken);
            }
            catch (Exception error)
            {
                MarkAppliedTransactionsFailed(session);
                session.ActiveTransaction.Clear();
                return await FailAsync(session, finalRepairStep, error.Message +
                    " Applied changes were retained for inspection and require manual rollback.");
            }
            if (await AwaitApprovalAsync(session, repairStep, repair, false))
                return session;
            await git.ValidateBeforeApplyAsync(session, repair, cancellationToken);
            Apply(session, repairStep, repair);
            session.ActiveTransaction.Add(repair);
            RecordTransaction(session, finalRepairStep, repair);
            finalRepairStep.Status = StepStatuses.Completed;
            finalCompilation = await roslyn.CompileAsync(
                CompilationKinds.Validation, null, cancellationToken);
            Log(session, "compile", "Final validation compilation completed.", finalCompilation);
        }
        if (!finalCompilation.Success)
        {
            MarkAppliedTransactionsFailed(session);
            SetStatus(session, SessionStatuses.Failed, finalCompilation.InfrastructureFailure
                ? "Compilation infrastructure failed. Applied changes were retained for inspection and require manual rollback. " +
                  finalCompilation.Output
                : "Final validation compilation failed. Applied changes were retained for inspection and require manual rollback.");
        }
        else
        {
            foreach (var group in session.ActiveTransaction.GroupBy(patch => patch.StepId))
            {
                var commitStep = session.Plan.Steps.FirstOrDefault(step => step.Id == group.Key);
                if (commitStep is null) continue;
                var commitMessage = await git.CommitStepAsync(session, commitStep,
                    group.ToList(), cancellationToken);
                if (session.GitPolicy.Enabled && session.GitPolicy.CommitEachStep)
                    History(session, "GitCommitCreated", commitMessage,
                        session.GitState.Commits.LastOrDefault());
            }
            session.Plan.Status = "Completed";
            foreach (var transaction in session.Transactions.Where(item => item.Status == "Applied"))
            {
                transaction.Status = "Completed";
                transaction.CompletedAtUtc = DateTime.UtcNow;
            }
            SetStatus(session, SessionStatuses.Completed, finalCompilation.Skipped
                ? "Development workflow completed; compilation was skipped because no project exists."
                : "Development workflow completed.");
            if (session.GitPolicy.Enabled)
                History(session, "GitWorkflowCompleted", git.Finish(session), session.GitState);
            History(session, "Completed", "Development workflow completed.");
        }
        session.CurrentStepId = null;
        session.ActiveTransaction.Clear();
        await CheckpointAsync(session);
        return session;
    }

    public async Task<DevelopmentSession> SkipCurrentStepAsync(DevelopmentSession session)
    {
        if (session.Plan is null || session.CurrentStepId is null ||
            session.PendingPatch is null)
            throw new InvalidOperationException("There is no patch awaiting approval.");
        var step = session.Plan.Steps.First(item => item.Id == session.CurrentStepId);
        step.Status = StepStatuses.Skipped;
        step.LastError = "Skipped by user.";
        session.PendingPatch = null;
        session.CurrentStepId = null;
        SetStatus(session, SessionStatuses.Ready, $"Skipped {step.Title}.");
        History(session, "StepSkipped", $"Skipped {step.Title}.");
        await CheckpointAsync(session);
        return session;
    }

    public async Task<DevelopmentSession> CancelSessionAsync(DevelopmentSession session)
    {
        foreach (var patch in session.ActiveTransaction.AsEnumerable().Reverse())
            workspace.Rollback(patch.Files);
        session.ActiveTransaction.Clear();
        MarkAppliedTransactionsRolledBack(session);
        session.PendingPatch = null;
        session.CurrentStepId = null;
        if (session.Plan is not null) session.Plan.Status = "Cancelled";
        SetStatus(session, SessionStatuses.Cancelled,
            "Development workflow cancelled; applied changes were rolled back.");
        History(session, "Cancelled", "Development workflow cancelled by user.");
        await CheckpointAsync(session);
        return session;
    }

    public async Task<DevelopmentSession> RollbackTransactionAsync(
        DevelopmentSession session, string transactionId)
    {
        EnsureSessionWorkspace(session);
        var transaction = session.Transactions.FirstOrDefault(item => item.Id == transactionId) ??
            throw new InvalidOperationException("Transaction was not found: " + transactionId);
        if (transaction.Status == "RolledBack")
            throw new InvalidOperationException("Transaction is already rolled back.");
        workspace.RollbackTransaction(transaction.Patches);
        transaction.Status = "RolledBack";
        transaction.RolledBackAtUtc = DateTime.UtcNow;
        var recoverableSession = session.Status is SessionStatuses.Failed or
            SessionStatuses.Cancelled or SessionStatuses.Repairing;
        if (recoverableSession)
            ResetRolledBackSteps(session);
        if (recoverableSession && session.Transactions.All(item =>
                item.Status is not ("FailedApplied" or "Applied")))
        {
            session.ActiveTransaction.Clear();
            session.PendingPatch = null;
            session.CurrentStepId = null;
            if (session.Plan is not null) session.Plan.Status = "Ready";
            SetStatus(session, SessionStatuses.Ready,
                "All retained transactions were rolled back. The workflow can be started again.");
        }
        History(session, "TransactionRolledBack",
            $"Rolled back transaction {transaction.Title}.", transaction);
        Log(session, "rollback", $"Rolled back transaction {transaction.Title}.");
        await CheckpointAsync(session);
        return session;
    }

    public async Task<DevelopmentSession> RollbackAllTransactionsAsync(
        DevelopmentSession session)
    {
        EnsureSessionWorkspace(session);
        var transactions = session.Transactions
            .Where(item => item.Status != "RolledBack")
            .OrderBy(item => item.CreatedAtUtc).ToArray();
        if (transactions.Length == 0)
            throw new InvalidOperationException("There are no transactions available for rollback.");
        workspace.RollbackTransaction(transactions.SelectMany(item => item.Patches));
        var rolledBackAt = DateTime.UtcNow;
        foreach (var transaction in transactions)
        {
            transaction.Status = "RolledBack";
            transaction.RolledBackAtUtc = rolledBackAt;
        }
        session.ActiveTransaction.Clear();
        session.PendingPatch = null;
        session.CurrentStepId = null;
        ResetRolledBackSteps(session);
        if (session.Plan is not null) session.Plan.Status = "Ready";
        SetStatus(session, SessionStatuses.Ready,
            $"Rolled back all {transactions.Length} transactions. The workflow can be started again.");
        History(session, "AllTransactionsRolledBack",
            $"Rolled back all {transactions.Length} transactions.", transactions);
        await CheckpointAsync(session);
        return session;
    }

    private static void RecordTransaction(DevelopmentSession session,
        DevelopmentStep step, PreparedPatch patch)
    {
        var transaction = session.Transactions.LastOrDefault(item =>
            item.StepId == step.Id && item.Status == "Applied");
        if (transaction is null)
        {
            transaction = new DevelopmentTransaction
            {
                StepId = step.Id,
                Title = step.Title
            };
            session.Transactions.Add(transaction);
        }
        transaction.Patches.Add(patch);
    }

    private void EnsureSessionWorkspace(DevelopmentSession session)
    {
        if (string.IsNullOrWhiteSpace(session.WorkspaceRoot))
            throw new InvalidOperationException(
                "This legacy session has no recorded workspace root and cannot safely modify files. " +
                "Create a new Developer Session.");
        if (!string.Equals(Path.GetFullPath(session.WorkspaceRoot),
                Path.GetFullPath(settings.WorkspaceRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Session workspace does not match the connected workspace. " +
                $"Session: {session.WorkspaceRoot} | Connected: {settings.WorkspaceRoot}");
    }

    private static void MarkAppliedTransactionsRolledBack(DevelopmentSession session)
    {
        foreach (var transaction in session.Transactions.Where(item => item.Status == "Applied"))
        {
            transaction.Status = "RolledBack";
            transaction.RolledBackAtUtc = DateTime.UtcNow;
        }
        ResetRolledBackSteps(session);
    }

    private static void MarkAppliedTransactionsFailed(DevelopmentSession session)
    {
        foreach (var transaction in session.Transactions.Where(item => item.Status == "Applied"))
            transaction.Status = "FailedApplied";
    }

    private static void ResetRolledBackSteps(DevelopmentSession session)
    {
        if (session.Plan is null) return;
        var rolledBackStepIds = session.Transactions
            .Where(item => item.Status == "RolledBack")
            .Select(item => item.StepId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var step in session.Plan.Steps.Where(item =>
                     item.Status == StepStatuses.Completed &&
                     rolledBackStepIds.Contains(item.Id)))
        {
            step.Status = StepStatuses.Pending;
            step.LastError = "";
        }
    }

    public BudgetSnapshot CalculateBudget(string prompt, string context) =>
        _budgets.Calculate(_profile, prompt, context);

    private PreparedPatch Prepare(DevelopmentSession session, PatchDocument patch,
        DevelopmentStep step, string kind)
    {
        var prepared = workspace.Prepare(patch, step.Id, step.Risk);
        prepared.Kind = kind;
        History(session, kind == "Repair" ? "RepairPatchGenerated" : "PatchGenerated",
            $"Generated {prepared.Files.Count} file change(s).", prepared);
        _ = notify("patchPreview", new
        {
            stepTitle = step.Title,
            preview = workspace.Preview(prepared)
        });
        return prepared;
    }

    private async Task<PreparedPatch> GeneratePreparedPatchAsync(
        DevelopmentSession session, DevelopmentStep step, string context,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        var requestContext = context;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            var raw = await llm.CreatePatchAsync(session.Goal, step,
                requestContext, cancellationToken);
            try
            {
                var prepared = Prepare(session, workspace.ParsePatch(raw), step, "Patch");
                ValidatePreparedSyntax(prepared);
                return prepared;
            }
            catch (Exception error) when (attempt < maximumAttempts &&
                error is InvalidOperationException or System.Text.Json.JsonException)
            {
                Log(session, "patch",
                    $"Generated patch was rejected before application ({attempt}/{maximumAttempts}): " +
                    error.Message);
                await CheckpointAsync(session);
                requestContext = context + "\n\nPREVIOUS PATCH REJECTED BEFORE APPLICATION\n" +
                    error.Message + "\nGenerate a corrected patch. For replace, use a longer exact search block " +
                    "that occurs exactly once in the supplied file. Do not repeat the rejected patch.";
                Log(session, "llm", $"Waiting for {settings.ProviderName} to regenerate a safer patch " +
                    $"({attempt + 1}/{maximumAttempts}, timeout: {settings.LlmRequestTimeoutSeconds} seconds).");
                await CheckpointAsync(session);
            }
        }
        throw new InvalidOperationException("Patch generation attempts were exhausted.");
    }

    private async Task<PreparedPatch> GeneratePreparedRepairAsync(
        DevelopmentSession session, DevelopmentStep step, string context,
        CompilationResult compilation, CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        var requestContext = context;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            var raw = await llm.CreateRepairAsync(session.Goal, step,
                requestContext, compilation, cancellationToken);
            try
            {
                var prepared = Prepare(session, workspace.ParsePatch(raw), step, "Repair");
                ValidatePreparedSyntax(prepared);
                return prepared;
            }
            catch (Exception error) when (error is InvalidOperationException or
                                           System.Text.Json.JsonException)
            {
                lastError = error;
                if (attempt == maximumAttempts) break;
                Log(session, "patch",
                    $"Generated repair was rejected before application ({attempt}/{maximumAttempts}): " +
                    error.Message);
                await CheckpointAsync(session);
                requestContext = context + "\n\nPREVIOUS REPAIR REJECTED BEFORE APPLICATION\n" +
                    error.Message + "\nGenerate a corrected, syntactically valid repair. " +
                    "Use exact unique search blocks and do not repeat the rejected repair.";
                Log(session, "llm", $"Waiting for {settings.ProviderName} to regenerate a safer repair " +
                    $"({attempt + 1}/{maximumAttempts}, timeout: {settings.LlmRequestTimeoutSeconds} seconds).");
                await CheckpointAsync(session);
            }
        }
        throw new InvalidOperationException(
            "Repair generation attempts were exhausted. Last rejection: " + lastError?.Message,
            lastError);
    }

    private void ValidatePreparedSyntax(PreparedPatch patch)
    {
        var errors = patch.Files
            .Where(file => file.Operation != "delete" &&
                           file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => roslyn.AnalyzeContent(file.Path, file.Content))
            .Where(item => item.Severity == "Error")
            .Take(12).ToArray();
        if (errors.Length == 0) return;
        throw new InvalidOperationException("Generated C# syntax is invalid: " +
            string.Join(" | ", errors.Select(item =>
                $"{item.Path}({item.Line},{item.Column}) {item.Id}: {item.Message}")));
    }

    private async Task<bool> AwaitApprovalAsync(DevelopmentSession session,
        DevelopmentStep step, PreparedPatch prepared, bool explicitlyApproved)
    {
        if (explicitlyApproved) return false;
        step.Status = StepStatuses.AwaitingApproval;
        session.PendingPatch = prepared;
        SetStatus(session, SessionStatuses.AwaitingApproval,
            $"Patch review required for {step.Title} (risk: {prepared.Risk}).");
        await CheckpointAsync(session);
        await notify("approvalRequired", new
        {
            sessionId = session.Id, stepTitle = step.Title, risk = prepared.Risk
        });
        return true;
    }

    private void Apply(DevelopmentSession session, DevelopmentStep step, PreparedPatch patch)
    {
        step.Status = StepStatuses.Applying;
        workspace.Apply(patch);
        session.PendingPatch = null;
        Log(session, "patch", $"Applied {patch.Files.Count} file change(s).");
        History(session, patch.Kind == "Repair"
            ? "RepairPatchApplied" : "PatchApplied",
            $"Applied {patch.Files.Count} file change(s).", patch);
    }

    private async Task<CompilationResult> CompileAsync(DevelopmentSession session,
        DevelopmentStep step, string kind, IEnumerable<string> changed,
        CancellationToken cancellationToken)
    {
        step.Status = StepStatuses.Compiling;
        SetStatus(session, SessionStatuses.Compiling, $"{kind}: {step.Title}");
        await CheckpointAsync(session);
        var result = await roslyn.CompileAsync(kind, changed, cancellationToken);
        Log(session, "compile", $"{kind} finished.", result);
        History(session, result.Success ? "CompilationSucceeded" : "CompilationFailed",
            $"{kind} finished.", result);
        if (!result.Success) step.LastError = result.Output;
        await CheckpointAsync(session);
        return result;
    }

    private void UpdateBudget(DevelopmentSession session, string prompt, string context)
    {
        session.Budget = _budgets.Calculate(_profile, prompt, context);
        if (session.Budget.ExceedsContextWindow)
            throw new InvalidOperationException(session.Budget.Warning);
        _ = notify("budgetUpdated", session.Budget);
    }

    private DevelopmentPlan ParsePlan(JsonObject raw)
    {
        var items = raw["steps"]?.AsArray() ??
                    throw new InvalidOperationException("Planner response requires steps.");
        if (items.Count == 0 || items.Count > settings.MaxPlanSteps)
            throw new InvalidOperationException(
                $"Plan must contain 1..{settings.MaxPlanSteps} steps.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var plan = new DevelopmentPlan { Summary = raw["summary"]?.GetValue<string>() ?? "" };
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index]?.AsObject() ?? throw new InvalidOperationException("Plan step is invalid.");
            var id = item["id"]?.GetValue<string>()?.Trim() ?? $"step-{index + 1}";
            if (!ids.Add(id)) throw new InvalidOperationException($"Duplicate step id: {id}");
            var kind = item["kind"]?.GetValue<string>() == "validation" ? "validation" : "patch";
            var risk = item["risk"]?.GetValue<string>() ?? "medium";
            if (risk is not ("low" or "medium" or "high")) risk = "medium";
            plan.Steps.Add(new DevelopmentStep
            {
                Id = id, Order = index + 1,
                Title = item["title"]?.GetValue<string>() ?? id,
                Description = item["description"]?.GetValue<string>() ?? "",
                Kind = kind, Risk = risk,
                Targets = item["targets"]?.AsArray()
                    .Select(node => node?.GetValue<string>() ?? "").Where(value => value.Length > 0)
                    .ToArray() ?? []
            });
        }
        NormalizePlanStepKinds(plan);
        return plan;
    }

    private static void NormalizePlanStepKinds(DevelopmentPlan plan)
    {
        string[] mutationTerms =
        [
            "add ", "create ", "write ", "update ", "modify ", "change ",
            "implement", "generate ", "remove ", "delete ", "fix ", "persist"
        ];
        foreach (var step in plan.Steps.Where(item => item.Kind == "validation"))
        {
            var intent = (step.Title + " " + step.Description).ToLowerInvariant();
            if (mutationTerms.Any(intent.Contains)) step.Kind = "patch";
        }
    }

    private async Task<DevelopmentSession> FailAsync(
        DevelopmentSession session, DevelopmentStep step, string message)
    {
        step.Status = StepStatuses.Failed;
        step.LastError = message;
        if (session.Plan is not null) session.Plan.Status = "Failed";
        SetStatus(session, SessionStatuses.Failed, message);
        History(session, "Failed", message);
        await CheckpointAsync(session);
        return session;
    }

    private async Task CheckpointAsync(DevelopmentSession session)
    {
        await store.SaveAsync(session);
        await notify("sessionUpdated", session);
    }

    private static void SetStatus(DevelopmentSession session, string status, string message)
    {
        session.Status = status;
        Log(session, "workflow", message);
    }

    private static void Log(DevelopmentSession session, string type,
        string message, object? details = null)
    {
        session.Logs.Add(new WorkflowLog
        {
            Type = type, Message = message, Details = details
        });
        session.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static void History(DevelopmentSession session, string type,
        string message, object? details = null)
    {
        session.History.Add(new DeveloperHistoryEvent
        {
            Type = type, Message = message,
            StepId = session.CurrentStepId ?? "", Details = details
        });
    }
}
