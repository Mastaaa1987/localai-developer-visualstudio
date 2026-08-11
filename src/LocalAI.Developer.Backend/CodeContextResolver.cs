using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAI.Developer.Backend;

public sealed partial class CodeContextResolver(
    BackendSettings settings,
    WorkspaceService workspace,
    CodeAnalysisService analysis,
    int maximumContextFiles)
{
    private static readonly string[] ManifestNames =
    [
        "package.json", "composer.json", "pyproject.toml", "requirements.txt",
        "Pipfile", "setup.py", "setup.cfg", "tsconfig.json"
    ];

    public string Resolve(IEnumerable<string> targets, string description)
    {
        var explicitTargets = targets.Select(workspace.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var available = workspace.EnumerateContextFiles(2500).ToArray();
        var selected = new List<string>(explicitTargets);

        foreach (var target in explicitTargets)
            AddNearbyManifests(target, available, selected);

        var graph = new ProjectDependencyGraph(settings, workspace)
            .ResolveTransitive(selected, 250);
        var contextLimit = Math.Max(maximumContextFiles, explicitTargets.Count);
        foreach (var dependency in graph.OrderedPaths)
        {
            if (selected.Contains(dependency, StringComparer.OrdinalIgnoreCase)) continue;
            if (selected.Count >= contextLimit) break;
            selected.Add(dependency);
        }

        var terms = SearchTerms(description, explicitTargets);
        foreach (var candidate in available
                     .Where(path => !selected.Contains(path, StringComparer.OrdinalIgnoreCase))
                     .Select(path => new { Path = path, Score = Score(path, terms, explicitTargets) })
                     .Where(item => item.Score > 0)
                     .OrderByDescending(item => item.Score)
                     .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (selected.Count >= contextLimit) break;
            selected.Add(candidate.Path);
        }

        var entries = selected.Select(relative =>
        {
            var content = workspace.Read(relative);
            return new
            {
                path = relative,
                exists = content is not null,
                sha256 = content is null ? "" : WorkspaceService.Sha256(content),
                content = content is null ? "" : content[..Math.Min(30000, content.Length)],
                truncated = content is not null && content.Length > 30000
            };
        });
        var omittedDependencies = graph.OrderedPaths.Where(path =>
            !selected.Contains(path, StringComparer.OrdinalIgnoreCase)).ToArray();
        return JsonSerializer.Serialize(entries, JsonDefaults.Options) +
               "\n\nDEPENDENCY GRAPH\n" + JsonSerializer.Serialize(new
               {
                   edges = graph.Edges,
                   omittedDueToContextFileBudget = omittedDependencies
               }, JsonDefaults.Options) +
               "\n\nLANGUAGE ANALYSIS\n" + analysis.BuildContext(selected);
    }

    private HashSet<string> SearchTerms(string description, IEnumerable<string> targets)
    {
        var terms = Identifier().Matches(description ?? "").Select(match => match.Value)
            .Concat(targets.SelectMany(target => Identifier().Matches(
                Path.GetFileNameWithoutExtension(target)).Select(match => match.Value)))
            .Where(value => value.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            string? content;
            try { content = workspace.Read(target); }
            catch { continue; }
            if (content is null) continue;
            foreach (var line in content.Split('\n').Select(value => value.Trim())
                         .Where(IsDependencyLine).Take(100))
            foreach (Match match in Identifier().Matches(line))
                if (match.Value.Length >= 3) terms.Add(match.Value);
        }
        return terms;
    }

    private static bool IsDependencyLine(string line) =>
        line.StartsWith("using ", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("import ", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("from ", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("require", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("include", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("use ", StringComparison.OrdinalIgnoreCase) ||
        line.Contains(" src=", StringComparison.OrdinalIgnoreCase) ||
        line.Contains(" href=", StringComparison.OrdinalIgnoreCase);

    private int Score(string candidate, HashSet<string> terms, IEnumerable<string> targets)
    {
        var score = terms.Count(term => candidate.Contains(term, StringComparison.OrdinalIgnoreCase)) * 8;
        var candidateDirectory = Path.GetDirectoryName(candidate)?.Replace('\\', '/') ?? "";
        score += targets.Count(target =>
            string.Equals(Path.GetDirectoryName(target)?.Replace('\\', '/'),
                candidateDirectory, StringComparison.OrdinalIgnoreCase)) * 3;
        try
        {
            var content = workspace.Read(candidate);
            if (content is not null)
            {
                content = content[..Math.Min(30000, content.Length)];
                score += terms.Take(30).Count(term =>
                    content.Contains(term, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { }
        return score;
    }

    private static void AddNearbyManifests(
        string target, IEnumerable<string> available, ICollection<string> selected)
    {
        var directory = Path.GetDirectoryName(target)?.Replace('\\', '/') ?? "";
        foreach (var path in available.Where(path =>
                     IsManifest(path) &&
                     (string.IsNullOrEmpty(directory) ||
                      directory.StartsWith(
                          Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "",
                          StringComparison.OrdinalIgnoreCase))))
            if (!selected.Contains(path, StringComparer.OrdinalIgnoreCase)) selected.Add(path);
    }

    private static bool IsManifest(string path) =>
        ManifestNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) ||
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_.-]*")]
    private static partial Regex Identifier();
}
