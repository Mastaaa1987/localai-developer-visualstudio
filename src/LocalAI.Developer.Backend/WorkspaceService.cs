using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAI.Developer.Backend;

public sealed class WorkspaceService(BackendSettings settings)
{
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "Library", "Temp", "obj", "bin", ".unityai-vscode", "node_modules"
    };

    public string Normalize(string value)
    {
        var source = (value ?? "").Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(source) || Path.IsPathRooted(source) ||
            source.Split('/').Contains(".."))
            throw new InvalidOperationException($"Unsafe workspace path: {value}");
        var segments = source.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(Blocked.Contains))
            throw new InvalidOperationException($"Path is protected: {value}");
        return string.Join('/', segments.Where(segment => segment != "."));
    }

    public string Resolve(string relative)
    {
        var normalized = Normalize(relative);
        var root = Path.GetFullPath(settings.WorkspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var absolute = Path.GetFullPath(Path.Combine(root, normalized));
        if (!absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path escapes workspace: {relative}");
        AssertNoSymbolicLinks(root, normalized);
        return absolute;
    }

    public string? Read(string relative)
    {
        var path = Resolve(relative);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public string Describe(IEnumerable<string> paths, RoslynBackend roslyn)
    {
        var entries = paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(relative =>
        {
            var normalized = Normalize(relative);
            var content = Read(normalized);
            return new
            {
                path = normalized,
                exists = content is not null,
                sha256 = content is null ? "" : Sha256(content),
                content = content is null ? "" : content[..Math.Min(30000, content.Length)],
                truncated = content is not null && content.Length > 30000
            };
        });
        return JsonSerializer.Serialize(entries, JsonDefaults.Options) +
               "\n\nROSLYN SYMBOLS\n" + roslyn.BuildSemanticContext(paths);
    }

    public string WorkspaceSummary(int maximumFiles = 400)
    {
        return string.Join('\n', Directory.EnumerateFiles(settings.WorkspaceRoot, "*",
                SearchOption.AllDirectories)
            .Where(path => !ContainsBlockedSegment(path))
            .Where(path => new[] { ".cs", ".js", ".json", ".csproj", ".sln", ".asmdef", ".md" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Take(maximumFiles)
            .Select(path => Path.GetRelativePath(settings.WorkspaceRoot, path).Replace('\\', '/')));
    }

    public PatchDocument ParsePatch(JsonObject value)
    {
        var patch = value.Deserialize<PatchDocument>(JsonDefaults.Options) ??
                    throw new InvalidOperationException("Patch JSON is invalid.");
        if (patch.Files.Count == 0) throw new InvalidOperationException("Patch contains no files.");
        if (patch.Files.Count > settings.MaxFilesPerPatch)
            throw new InvalidOperationException(
                $"Patch exceeds the {settings.MaxFilesPerPatch} file safety limit.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in patch.Files)
        {
            file.Path = Normalize(file.Path);
            if (!seen.Add(file.Path))
                throw new InvalidOperationException($"Duplicate patch path: {file.Path}");
            if (IsCombinedCreateOrUpdate(file.Operation))
                file.Operation = Read(file.Path) is null ? "create" : "update";
            if (file.Operation is not ("create" or "replace" or "update" or "delete"))
                throw new InvalidOperationException($"Unsupported operation: {file.Operation}");
            if (file.Operation is "create" or "update" && string.IsNullOrWhiteSpace(file.Content))
                throw new InvalidOperationException(
                    $"Empty file content is not allowed for {file.Operation}: {file.Path}. " +
                    "Use operation 'delete' only when deletion is explicitly intended.");
            if (file.Operation == "replace" && string.IsNullOrEmpty(file.Search))
                throw new InvalidOperationException($"Search is required for replace: {file.Path}");
            if (file.Operation != "create" && string.IsNullOrWhiteSpace(file.ExpectedSha256))
                throw new InvalidOperationException($"expectedSha256 is required: {file.Path}");
        }
        return patch;
    }

    private static bool IsCombinedCreateOrUpdate(string operation)
    {
        var normalized = (operation ?? "").Trim().ToLowerInvariant()
            .Replace("/", " or ").Replace("|", " or ");
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(value => value != "or")
            .ToHashSet(StringComparer.Ordinal)
            .SetEquals(["create", "update"]);
    }

    public PreparedPatch Prepare(PatchDocument patch, string stepId, string risk)
    {
        var prepared = new PreparedPatch
        {
            StepId = stepId,
            Risk = ClassifyRisk(patch, risk),
            Summary = patch.Summary
        };
        foreach (var source in patch.Files)
        {
            var current = Read(source.Path);
            if (source.Operation == "create" && current is not null)
                throw new InvalidOperationException($"Create target exists: {source.Path}");
            if (source.Operation != "create" && current is null)
                throw new InvalidOperationException($"Patch target is missing: {source.Path}");
            if (source.Operation != "create" &&
                !string.Equals(Sha256(current!), source.ExpectedSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Stale patch rejected: {source.Path}");
            var operation = source.Operation;
            var content = source.Content;
            if (operation == "replace")
            {
                var occurrences = CountOccurrences(current!, source.Search);
                if (occurrences != 1)
                    throw new InvalidOperationException(
                        $"Replace search must occur exactly once in {source.Path}; found {occurrences}.");
                content = current!.Replace(source.Search, source.Replacement,
                    StringComparison.Ordinal);
                operation = "update";
            }
            prepared.Files.Add(new PatchFile
            {
                Path = source.Path, Operation = operation,
                ExpectedSha256 = source.ExpectedSha256,
                Content = content, Search = source.Search,
                Replacement = source.Replacement, Before = current
            });
        }
        return prepared;
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    public string Preview(PreparedPatch patch) => string.Join("\n\n", patch.Files.Select(file =>
        $"--- a/{file.Path}\n+++ b/{file.Path}\n" +
        BuildUnifiedDiff(file.Before ?? "", file.Operation == "delete" ? "" : file.Content)));

    private static string BuildUnifiedDiff(string before, string after)
    {
        var left = NormalizeLines(before);
        var right = NormalizeLines(after);
        if ((long)left.Length * right.Length > 4_000_000)
            return string.Join('\n', left.Select(line => "- " + line)
                .Concat(right.Select(line => "+ " + line)));
        var lengths = new int[left.Length + 1, right.Length + 1];
        for (var leftIndex = left.Length - 1; leftIndex >= 0; leftIndex--)
        for (var rightIndex = right.Length - 1; rightIndex >= 0; rightIndex--)
            lengths[leftIndex, rightIndex] = left[leftIndex] == right[rightIndex]
                ? lengths[leftIndex + 1, rightIndex + 1] + 1
                : Math.Max(lengths[leftIndex + 1, rightIndex], lengths[leftIndex, rightIndex + 1]);
        var output = new List<string>();
        var i = 0;
        var j = 0;
        while (i < left.Length || j < right.Length)
        {
            if (i < left.Length && j < right.Length && left[i] == right[j])
            {
                output.Add("  " + left[i++]);
                j++;
            }
            else if (j < right.Length && (i == left.Length ||
                     lengths[i, j + 1] >= lengths[i + 1, j]))
                output.Add("+ " + right[j++]);
            else
                output.Add("- " + left[i++]);
        }
        return string.Join('\n', output);
    }

    private static string[] NormalizeLines(string value) => value
        .Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    public void Apply(PreparedPatch patch)
    {
        var applied = new List<PatchFile>();
        try
        {
            foreach (var file in patch.Files)
            {
                var target = Resolve(file.Path);
                if (file.Operation == "delete") File.Delete(target);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var temporary = target + ".unityai-" + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(temporary, file.Content, new UTF8Encoding(false));
                    if (file.Operation == "create") File.Move(temporary, target);
                    else
                    {
                        var backup = target + ".unityai-" + Guid.NewGuid().ToString("N") + ".backup";
                        File.Move(target, backup);
                        try
                        {
                            File.Move(temporary, target);
                            File.Delete(backup);
                        }
                        catch
                        {
                            File.Delete(temporary);
                            File.Move(backup, target);
                            throw;
                        }
                    }
                }
                applied.Add(file);
            }
        }
        catch
        {
            Rollback(applied);
            throw;
        }
    }

    public void Rollback(IEnumerable<PatchFile> files)
    {
        foreach (var file in files.Reverse())
        {
            var target = Resolve(file.Path);
            if (file.Before is null) File.Delete(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, file.Before, new UTF8Encoding(false));
            }
        }
    }

    public void RollbackTransaction(IEnumerable<PreparedPatch> patches)
    {
        var states = new Dictionary<string, (string? Before, string? After)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in patches.SelectMany(patch => patch.Files))
        {
            var after = string.Equals(file.Operation, "delete",
                StringComparison.OrdinalIgnoreCase) ? null : file.Content;
            if (states.TryGetValue(file.Path, out var state))
                states[file.Path] = (state.Before, after);
            else
                states[file.Path] = (file.Before, after);
        }

        foreach (var item in states)
        {
            var current = Read(item.Key);
            if (!string.Equals(current, item.Value.After, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Rollback conflict: {item.Key} changed after this transaction.");
        }

        foreach (var item in states)
        {
            var target = Resolve(item.Key);
            if (item.Value.Before is null) File.Delete(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, item.Value.Before, new UTF8Encoding(false));
            }
        }
    }

    public bool RequiresApproval(PreparedPatch patch) =>
        settings.ApprovalMode.Equals("manual", StringComparison.OrdinalIgnoreCase) ||
        patch.Risk != "low";

    public static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ClassifyRisk(PatchDocument patch, string stepRisk)
    {
        if (patch.Files.Any(file => file.Operation == "delete") || stepRisk == "high") return "high";
        if (patch.Files.Any(file => file.Operation is "replace" or "update") || stepRisk == "medium") return "medium";
        return "low";
    }

    private static bool ContainsBlockedSegment(string path) => path.Split(
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
        StringSplitOptions.RemoveEmptyEntries).Any(Blocked.Contains);

    private static void AssertNoSymbolicLinks(string root, string relative)
    {
        var current = root.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Symbolic links are not valid patch targets: {relative}");
        }
    }
}

public sealed class SessionStore(BackendSettings settings)
{
    public async Task SaveAsync(DevelopmentSession session)
    {
        Directory.CreateDirectory(settings.StorageDirectory);
        session.UpdatedAtUtc = DateTime.UtcNow;
        var target = Path.Combine(settings.StorageDirectory, session.Id + ".json");
        var temporary = target + ".tmp";
        await File.WriteAllTextAsync(temporary,
            JsonSerializer.Serialize(session, JsonDefaults.Options));
        File.Move(temporary, target, true);
    }

    public async Task<DevelopmentSession?> LoadAsync(string id)
    {
        var path = Path.Combine(settings.StorageDirectory, id + ".json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<DevelopmentSession>(
                await File.ReadAllTextAsync(path), JsonDefaults.Options) : null;
    }

    public async Task<DevelopmentSession?> LoadLatestAsync()
    {
        if (!Directory.Exists(settings.StorageDirectory)) return null;
        var latest = Directory.EnumerateFiles(settings.StorageDirectory, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return latest is null ? null : JsonSerializer.Deserialize<DevelopmentSession>(
            await File.ReadAllTextAsync(latest), JsonDefaults.Options);
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync()
    {
        if (!Directory.Exists(settings.StorageDirectory)) return [];
        var result = new List<SessionSummary>();
        foreach (var path in Directory.EnumerateFiles(settings.StorageDirectory, "*.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var session = JsonSerializer.Deserialize<DevelopmentSession>(
                    await File.ReadAllTextAsync(path), JsonDefaults.Options);
                if (session is null) continue;
                result.Add(new SessionSummary
                {
                    Id = session.Id, Goal = session.Goal, Status = session.Status,
                    ProviderName = session.ProviderName, ModelName = session.ModelName,
                    CreatedAtUtc = session.CreatedAtUtc, UpdatedAtUtc = session.UpdatedAtUtc,
                    HistoryEventCount = session.History.Count
                });
            }
            catch
            {
                // A corrupt session is isolated and does not hide valid history.
            }
        }
        return result;
    }

    public Task<bool> DeleteAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Session id is invalid.");
        var path = Path.Combine(settings.StorageDirectory, id + ".json");
        if (!File.Exists(path)) return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }
}
