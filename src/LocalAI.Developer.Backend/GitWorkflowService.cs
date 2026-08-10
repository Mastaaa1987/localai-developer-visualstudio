using System.Diagnostics;
using System.Text;

namespace LocalAI.Developer.Backend;

public sealed class GitWorkflowService(BackendSettings settings)
{
    public async Task<string> StartAsync(DevelopmentSession session,
        CancellationToken cancellationToken)
    {
        if (!session.GitPolicy.Enabled) return "Git workflow is disabled.";
        if (session.GitState.Active || session.GitState.Completed)
        {
            await ValidateBranchAsync(session, cancellationToken);
            return "Git workflow resumed on " + session.GitState.WorkflowBranch + ".";
        }

        var rootResult = await RunAsync(settings.WorkspaceRoot, cancellationToken,
            "rev-parse", "--show-toplevel");
        RequireSuccess(rootResult, "Git repository not available");
        var repositoryRoot = Path.GetFullPath(rootResult.Output.Trim());
        var workspaceRoot = Path.GetFullPath(settings.WorkspaceRoot);
        if (!IsWithin(repositoryRoot, workspaceRoot))
            throw new InvalidOperationException("Workspace is outside the detected Git repository.");

        var status = await RunAsync(repositoryRoot, cancellationToken,
            "status", "--porcelain=v1", "--untracked-files=all");
        RequireSuccess(status, "Git status failed");
        var dirty = ParseStatusPaths(status.Output);
        if (session.GitPolicy.RequireCleanStart && dirty.Count > 0)
            throw new InvalidOperationException(
                "Git workflow requires a clean working tree. Existing user changes were not touched.");

        var branch = await RunAsync(repositoryRoot, cancellationToken,
            "branch", "--show-current");
        var head = await RunAsync(repositoryRoot, cancellationToken, "rev-parse", "HEAD");
        RequireSuccess(branch, "Could not determine current Git branch");
        RequireSuccess(head, "Could not determine current Git commit");
        var originalBranch = branch.Output.Trim();
        var workflowBranch = originalBranch;
        if (session.GitPolicy.CreateBranch)
        {
            workflowBranch = BuildBranchName(session.GitPolicy.BranchPrefix,
                session.Goal, session.Id);
            var create = await RunAsync(repositoryRoot, cancellationToken,
                "switch", "-c", workflowBranch);
            RequireSuccess(create, "Could not create Git workflow branch");
        }

        session.GitState = new GitWorkflowState
        {
            Active = true, RepositoryRoot = repositoryRoot,
            OriginalBranch = originalBranch, WorkflowBranch = workflowBranch,
            BaseCommit = head.Output.Trim(), LastCommit = head.Output.Trim(),
            StartedAtUtc = DateTime.UtcNow, BaselineDirtyFiles = dirty
        };
        return "Git workflow started on branch " + workflowBranch + ".";
    }

    public async Task ValidateBeforeApplyAsync(DevelopmentSession session,
        PreparedPatch patch, CancellationToken cancellationToken)
    {
        if (!session.GitPolicy.Enabled) return;
        await ValidateBranchAsync(session, cancellationToken);
        var conflicts = await RunAsync(session.GitState.RepositoryRoot,
            cancellationToken, "diff", "--name-only", "--diff-filter=U");
        RequireSuccess(conflicts, "Git conflict check failed");
        if (!string.IsNullOrWhiteSpace(conflicts.Output))
            throw new InvalidOperationException("Git merge conflicts block patch application.");

        var protectedPaths = new HashSet<string>(session.GitState.BaselineDirtyFiles,
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in patch.Files)
        {
            var path = RepositoryPath(session.GitState.RepositoryRoot, file.Path);
            if (protectedPaths.Contains(path))
                throw new InvalidOperationException(
                    "Patch targets a file that was already dirty before the workflow: " + file.Path);
        }
    }

    public async Task<string> CommitStepAsync(DevelopmentSession session,
        DevelopmentStep step, IEnumerable<PreparedPatch> patches,
        CancellationToken cancellationToken)
    {
        if (!session.GitPolicy.Enabled || !session.GitPolicy.CommitEachStep)
            return "Git auto-commit is disabled.";
        await ValidateBranchAsync(session, cancellationToken);
        var paths = patches.SelectMany(patch => patch.Files)
            .Select(file => RepositoryPath(session.GitState.RepositoryRoot, file.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) return "No Git changes to commit.";

        var addArguments = new List<string> { "add", "--" };
        addArguments.AddRange(paths);
        RequireSuccess(await RunAsync(session.GitState.RepositoryRoot,
            cancellationToken, addArguments.ToArray()), "Git staging failed");

        var staged = await RunAsync(session.GitState.RepositoryRoot,
            cancellationToken, "diff", "--cached", "--quiet", "--");
        if (staged.ExitCode == 0) return "No Git changes to commit.";
        if (staged.ExitCode != 1) RequireSuccess(staged, "Git staged-change check failed");

        var message = "unity-ai: " + step.Title;
        var commitArguments = new List<string> { "commit", "-m", message, "--" };
        commitArguments.AddRange(paths);
        RequireSuccess(await RunAsync(session.GitState.RepositoryRoot,
            cancellationToken, commitArguments.ToArray()), "Git commit failed");
        var head = await RunAsync(session.GitState.RepositoryRoot,
            cancellationToken, "rev-parse", "HEAD");
        RequireSuccess(head, "Git commit was created but HEAD could not be read");
        var hash = head.Output.Trim();
        session.GitState.LastCommit = hash;
        session.GitState.Commits.Add(new GitCommitRecord
        {
            Hash = hash, StepId = step.Id, Message = message
        });
        return "Git commit created: " + hash;
    }

    public async Task UnstageAsync(DevelopmentSession session,
        IEnumerable<PreparedPatch> patches, CancellationToken cancellationToken)
    {
        if (!session.GitPolicy.Enabled || string.IsNullOrWhiteSpace(session.GitState.RepositoryRoot))
            return;
        var paths = patches.SelectMany(patch => patch.Files)
            .Select(file => RepositoryPath(session.GitState.RepositoryRoot, file.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) return;
        var arguments = new List<string> { "reset", "--" };
        arguments.AddRange(paths);
        var result = await RunAsync(session.GitState.RepositoryRoot,
            cancellationToken, arguments.ToArray());
        RequireSuccess(result, "Git index cleanup failed");
    }

    public string Finish(DevelopmentSession session)
    {
        if (!session.GitPolicy.Enabled) return "Git workflow is disabled.";
        session.GitState.Active = false;
        session.GitState.Completed = true;
        return "Git workflow completed on branch " + session.GitState.WorkflowBranch + ".";
    }

    public async Task<object> StatusAsync(CancellationToken cancellationToken)
    {
        var root = await RunAsync(settings.WorkspaceRoot, cancellationToken,
            "rev-parse", "--show-toplevel");
        RequireSuccess(root, "Git repository not available");
        var repositoryRoot = root.Output.Trim();
        var branch = await RunAsync(repositoryRoot, cancellationToken,
            "branch", "--show-current");
        var status = await RunAsync(repositoryRoot, cancellationToken,
            "status", "--porcelain=v1", "--untracked-files=all");
        RequireSuccess(branch, "Git branch query failed");
        RequireSuccess(status, "Git status failed");
        return new
        {
            repositoryRoot, branch = branch.Output.Trim(),
            clean = string.IsNullOrWhiteSpace(status.Output), status = status.Output.Trim()
        };
    }

    public async Task<string> PushAsync(DevelopmentSession session,
        bool explicitApproval, CancellationToken cancellationToken)
    {
        if (!explicitApproval)
            throw new InvalidOperationException("Git push requires explicit approval.");
        EnsureGitSession(session);
        var remote = string.IsNullOrWhiteSpace(session.GitPolicy.RemoteName)
            ? "origin" : session.GitPolicy.RemoteName;
        var push = await RunAsync(session.GitState.RepositoryRoot,
            cancellationToken, "push", "-u", remote, session.GitState.WorkflowBranch);
        RequireSuccess(push, "Git push failed");
        return "Branch pushed to " + remote + "/" + session.GitState.WorkflowBranch + ".";
    }

    public async Task<string> CreatePullRequestAsync(DevelopmentSession session,
        bool explicitApproval, CancellationToken cancellationToken)
    {
        if (!explicitApproval)
            throw new InvalidOperationException("GitHub pull request creation requires explicit approval.");
        EnsureGitSession(session);
        var result = await RunProcessAsync("gh", session.GitState.RepositoryRoot,
            cancellationToken, "pr", "create", "--fill", "--head",
            session.GitState.WorkflowBranch);
        RequireSuccess(result, "GitHub pull request creation failed");
        return result.Output.Trim();
    }

    private async Task ValidateBranchAsync(DevelopmentSession session,
        CancellationToken cancellationToken)
    {
        EnsureGitSession(session);
        var branch = await RunAsync(session.GitState.RepositoryRoot,
            cancellationToken, "branch", "--show-current");
        RequireSuccess(branch, "Git branch query failed");
        if (!string.Equals(branch.Output.Trim(), session.GitState.WorkflowBranch,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Git workflow branch changed unexpectedly. Expected " +
                session.GitState.WorkflowBranch + ".");
    }

    private void EnsureGitSession(DevelopmentSession session)
    {
        if (!session.GitPolicy.Enabled || string.IsNullOrWhiteSpace(session.GitState.RepositoryRoot) ||
            string.IsNullOrWhiteSpace(session.GitState.WorkflowBranch))
            throw new InvalidOperationException("Git workflow is not initialized for this session.");
    }

    private string RepositoryPath(string repositoryRoot, string workspaceRelative)
    {
        var absolute = Path.GetFullPath(Path.Combine(settings.WorkspaceRoot,
            workspaceRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(repositoryRoot, absolute))
            throw new InvalidOperationException("Patch path is outside the Git repository.");
        return Path.GetRelativePath(repositoryRoot, absolute).Replace('\\', '/');
    }

    private static string BuildBranchName(string prefix, string goal, string id)
    {
        var normalized = new string(goal.ToLowerInvariant().Select(character =>
            char.IsLetterOrDigit(character) ? character : '-').ToArray());
        while (normalized.Contains("--", StringComparison.Ordinal))
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        normalized = normalized.Trim('-');
        if (normalized.Length > 42) normalized = normalized[..42].TrimEnd('-');
        if (normalized.Length == 0) normalized = "development";
        var safePrefix = string.IsNullOrWhiteSpace(prefix) ? "unity-ai/" : prefix.Trim();
        if (!safePrefix.EndsWith('/')) safePrefix += "/";
        return safePrefix + normalized + "-" + id[..Math.Min(8, id.Length)];
    }

    private static List<string> ParseStatusPaths(string output) => output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Length > 3 ? line[3..].Trim() : "")
        .Select(path => path.Contains(" -> ", StringComparison.Ordinal)
            ? path[(path.LastIndexOf(" -> ", StringComparison.Ordinal) + 4)..] : path)
        .Where(path => path.Length > 0).ToList();

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                             Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedPath, normalizedRoot.TrimEnd(Path.DirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static Task<ProcessResult> RunAsync(string directory,
        CancellationToken cancellationToken, params string[] arguments) =>
        RunProcessAsync("git", directory, cancellationToken, arguments);

    private static async Task<ProcessResult> RunProcessAsync(string executable,
        string directory, CancellationToken cancellationToken, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new InvalidOperationException(executable + " could not be started.");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(executable + " is not available: " + error.Message, error);
        }
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorOutput = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await output, await errorOutput);
    }

    private static void RequireSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException(operation + ": " +
                (string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error).Trim());
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
