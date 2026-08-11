using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LocalAI.Developer.Backend;

public sealed class DependencyEdge
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Kind { get; set; } = "";
}

public sealed class DependencyGraphResult
{
    public List<string> OrderedPaths { get; set; } = [];
    public List<DependencyEdge> Edges { get; set; } = [];
}

public sealed class ProjectDependencyGraph(
    BackendSettings settings,
    WorkspaceService workspace)
{
    private static readonly string[] ScriptExtensions =
        [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".json"];
    private readonly Dictionary<string, List<string>> _csharpNamespaces =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _csharpTypes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _fileNames =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Prefix, string Directory)> _phpPrefixes = [];
    private readonly List<(string Alias, string Target)> _typescriptAliases = [];
    private readonly Dictionary<string, string> _packageRoots =
        new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _available = new(StringComparer.OrdinalIgnoreCase);

    public DependencyGraphResult ResolveTransitive(
        IEnumerable<string> roots, int maximumPaths = 250)
    {
        BuildIndexes();
        var result = new DependencyGraphResult();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (var root in roots.Select(NormalizeExisting)
                     .Where(path => path is not null).Cast<string>())
            if (queued.Add(root)) queue.Enqueue(root);

        while (queue.Count > 0 && result.OrderedPaths.Count < maximumPaths)
        {
            var source = queue.Dequeue();
            if (!visited.Add(source)) continue;
            result.OrderedPaths.Add(source);
            foreach (var dependency in Dependencies(source)
                         .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
            {
                result.Edges.Add(new DependencyEdge
                {
                    Source = source, Target = dependency.Path, Kind = dependency.Kind
                });
                if (queued.Add(dependency.Path)) queue.Enqueue(dependency.Path);
            }
        }
        return result;
    }

    private void BuildIndexes()
    {
        _available = workspace.EnumerateContextFiles(5000)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _csharpNamespaces.Clear();
        _csharpTypes.Clear();
        _fileNames.Clear();
        _phpPrefixes.Clear();
        _typescriptAliases.Clear();
        _packageRoots.Clear();

        foreach (var path in _available)
        {
            Add(_fileNames, Path.GetFileNameWithoutExtension(path), path);
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                IndexCSharp(path);
            else if (Path.GetFileName(path).Equals("composer.json",
                         StringComparison.OrdinalIgnoreCase))
                IndexComposer(path);
            else if (Path.GetFileName(path).Equals("tsconfig.json",
                         StringComparison.OrdinalIgnoreCase))
                IndexTypeScript(path);
            else if (Path.GetFileName(path).Equals("package.json",
                         StringComparison.OrdinalIgnoreCase))
                IndexPackage(path);
        }
    }

    private IEnumerable<(string Path, string Kind)> Dependencies(string source)
    {
        var content = SafeRead(source);
        if (content is null) yield break;
        var extension = Path.GetExtension(source).ToLowerInvariant();

        if (extension is ".py" or ".pyi" or ".pyw")
            foreach (var item in PythonDependencies(source, content)) yield return item;
        else if (extension is ".php" or ".phtml")
            foreach (var item in PhpDependencies(source, content)) yield return item;
        else if (ScriptExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            foreach (var item in ScriptDependencies(source, content)) yield return item;
        else if (extension == ".cs")
            foreach (var item in CSharpDependencies(source, content)) yield return item;
        else if (extension is ".html" or ".htm")
            foreach (var item in WebDependencies(source, content, HtmlReference, "html-reference"))
                yield return item;
        else if (extension is ".css" or ".scss" or ".less")
            foreach (var item in WebDependencies(source, content, CssReference, "css-import"))
                yield return item;
        else if (extension == ".xaml")
            foreach (var item in XamlDependencies(content)) yield return item;
        else if (extension is ".csproj" or ".props" or ".targets")
            foreach (var item in ProjectDependencies(source, content)) yield return item;
    }

    private IEnumerable<(string Path, string Kind)> PythonDependencies(
        string source, string content)
    {
        foreach (Match match in PythonImport.Matches(content))
        {
            var module = match.Groups["from"].Success
                ? match.Groups["from"].Value : match.Groups["import"].Value;
            foreach (var item in module.Split(',').Select(value =>
                         value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]))
            foreach (var resolved in ResolvePythonModule(source, item))
                yield return (resolved, "python-import");

            if (!match.Groups["from"].Success) continue;
            var imported = match.Groups["names"].Value;
            foreach (var name in imported.Split(',').Select(value =>
                         value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]))
            foreach (var resolved in ResolvePythonModule(source,
                         module.TrimEnd('.') + "." + name))
                yield return (resolved, "python-from-import");
        }
    }

    private IEnumerable<string> ResolvePythonModule(string source, string module)
    {
        if (string.IsNullOrWhiteSpace(module) || module == "*") yield break;
        var dots = module.TakeWhile(value => value == '.').Count();
        var plain = module[dots..].Replace('.', '/');
        var sourceDirectory = DirectoryName(source);
        var baseDirectory = dots == 0 ? "" : sourceDirectory;
        for (var index = 1; index < dots; index++)
            baseDirectory = DirectoryName(baseDirectory);
        var roots = dots == 0 && !string.IsNullOrWhiteSpace(sourceDirectory)
            ? new[] { "", sourceDirectory } : new[] { baseDirectory };
        foreach (var root in roots)
        {
            var stem = Combine(root, plain);
            foreach (var candidate in CandidateFiles(stem,
                         [".py", ".pyi", "/__init__.py", "/__init__.pyi"]))
                yield return candidate;
        }
    }

    private IEnumerable<(string Path, string Kind)> PhpDependencies(
        string source, string content)
    {
        foreach (Match match in PhpInclude.Matches(content))
            foreach (var resolved in ResolveRelative(source, match.Groups["path"].Value,
                         [".php", ".phtml"]))
                yield return (resolved, "php-include");

        foreach (Match match in PhpUse.Matches(content))
        {
            var value = match.Groups["name"].Value.TrimStart('\\');
            foreach (var mapping in _phpPrefixes.Where(item =>
                         value.StartsWith(item.Prefix, StringComparison.OrdinalIgnoreCase)))
            {
                var suffix = value[mapping.Prefix.Length..].Replace('\\', '/');
                foreach (var resolved in CandidateFiles(
                             Combine(mapping.Directory, suffix), [".php", ".phtml"]))
                    yield return (resolved, "composer-psr4");
            }
            var type = value.Split('\\').LastOrDefault();
            if (type is not null && _fileNames.TryGetValue(type, out var paths))
                foreach (var path in paths.Where(path =>
                             path.EndsWith(".php", StringComparison.OrdinalIgnoreCase) ||
                             path.EndsWith(".phtml", StringComparison.OrdinalIgnoreCase)))
                    yield return (path, "php-type");
        }
    }

    private IEnumerable<(string Path, string Kind)> ScriptDependencies(
        string source, string content)
    {
        foreach (Match match in ScriptImport.Matches(content))
        {
            var reference = match.Groups["path"].Value;
            if (reference.StartsWith('.'))
            {
                foreach (var resolved in ResolveRelative(source, reference,
                             ScriptExtensions.Concat(["/index.ts", "/index.tsx",
                                 "/index.js", "/index.jsx"]).ToArray()))
                    yield return (resolved, "script-import");
                continue;
            }
            foreach (var alias in _typescriptAliases.Where(item =>
                         AliasMatches(item.Alias, reference)))
            {
                var suffix = AliasSuffix(alias.Alias, reference);
                var target = alias.Target.Replace("*", suffix);
                foreach (var resolved in CandidateFiles(target,
                             ScriptExtensions.Concat(["/index.ts", "/index.tsx",
                                 "/index.js", "/index.jsx"]).ToArray()))
                    yield return (resolved, "tsconfig-path");
            }
            var packageName = PackageName(reference);
            if (_packageRoots.TryGetValue(packageName, out var root))
            {
                var suffix = reference.Length == packageName.Length ? "" :
                    reference[(packageName.Length + 1)..];
                foreach (var resolved in CandidateFiles(Combine(root, suffix),
                             ScriptExtensions.Concat(["/index.ts", "/index.tsx",
                                 "/index.js", "/index.jsx"]).ToArray()))
                    yield return (resolved, "workspace-package");
            }
        }
    }

    private IEnumerable<(string Path, string Kind)> CSharpDependencies(
        string source, string content)
    {
        foreach (Match match in CSharpUsing.Matches(content))
        {
            var value = match.Groups["namespace"].Value;
            foreach (var item in _csharpNamespaces.Where(item =>
                         item.Key.Equals(value, StringComparison.OrdinalIgnoreCase) ||
                         item.Key.StartsWith(value + ".", StringComparison.OrdinalIgnoreCase)))
            foreach (var path in item.Value.Where(path =>
                         !path.Equals(source, StringComparison.OrdinalIgnoreCase)))
                yield return (path, "csharp-namespace");
        }
        foreach (var item in _csharpTypes.Take(2000))
            if (WordContains(content, item.Key))
                foreach (var path in item.Value.Where(path =>
                             !path.Equals(source, StringComparison.OrdinalIgnoreCase)))
                    yield return (path, "csharp-type");
    }

    private IEnumerable<(string Path, string Kind)> WebDependencies(
        string source, string content, Regex pattern, string kind)
    {
        foreach (Match match in pattern.Matches(content))
        {
            var reference = match.Groups["path"].Value;
            if (reference.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                reference.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
                reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                reference.StartsWith('#')) continue;
            foreach (var path in ResolveRelative(source,
                         reference.Split('?', '#')[0], []))
                yield return (path, kind);
        }
    }

    private IEnumerable<(string Path, string Kind)> XamlDependencies(string content)
    {
        foreach (Match match in XamlClass.Matches(content))
        {
            var type = match.Groups["type"].Value.Split('.').Last();
            if (_csharpTypes.TryGetValue(type, out var paths))
                foreach (var path in paths) yield return (path, "xaml-code-behind");
        }
        foreach (Match match in XamlNamespace.Matches(content))
        {
            var value = match.Groups["namespace"].Value;
            if (_csharpNamespaces.TryGetValue(value, out var paths))
                foreach (var path in paths) yield return (path, "xaml-namespace");
        }
    }

    private IEnumerable<(string Path, string Kind)> ProjectDependencies(
        string source, string content)
    {
        XDocument document;
        try { document = XDocument.Parse(content); }
        catch { yield break; }
        foreach (var element in document.Descendants().Where(element =>
                     element.Name.LocalName is "ProjectReference" or "Compile" or "None" or
                         "Content" or "EmbeddedResource"))
        {
            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include) || include.Contains('*')) continue;
            foreach (var resolved in ResolveRelative(source, include, []))
                yield return (resolved, "msbuild-item");
        }
    }

    private void IndexCSharp(string path)
    {
        var content = SafeRead(path);
        if (content is null) return;
        var namespaceMatch = CSharpNamespace.Match(content);
        var name = namespaceMatch.Success ? namespaceMatch.Groups["name"].Value : "";
        if (!string.IsNullOrWhiteSpace(name)) Add(_csharpNamespaces, name, path);
        foreach (Match match in CSharpType.Matches(content))
            Add(_csharpTypes, match.Groups["name"].Value, path);
    }

    private void IndexComposer(string path)
    {
        try
        {
            using var document = ParseJson(SafeRead(path));
            if (document is null ||
                !document.RootElement.TryGetProperty("autoload", out var autoload) ||
                !autoload.TryGetProperty("psr-4", out var psr4)) return;
            foreach (var property in psr4.EnumerateObject())
            {
                var directory = property.Value.ValueKind == JsonValueKind.Array
                    ? property.Value.EnumerateArray().FirstOrDefault().GetString()
                    : property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(directory))
                    _phpPrefixes.Add((property.Name,
                        Combine(DirectoryName(path), directory)));
            }
        }
        catch { }
    }

    private void IndexTypeScript(string path)
    {
        try
        {
            using var document = ParseJson(SafeRead(path));
            if (document is null ||
                !document.RootElement.TryGetProperty("compilerOptions", out var options)) return;
            var baseUrl = options.TryGetProperty("baseUrl", out var baseValue)
                ? baseValue.GetString() ?? "" : "";
            var root = Combine(DirectoryName(path), baseUrl);
            if (!options.TryGetProperty("paths", out var paths)) return;
            foreach (var property in paths.EnumerateObject())
            foreach (var target in property.Value.EnumerateArray())
                if (target.GetString() is { } value)
                    _typescriptAliases.Add((property.Name, Combine(root, value)));
        }
        catch { }
    }

    private void IndexPackage(string path)
    {
        try
        {
            using var document = ParseJson(SafeRead(path));
            if (document is not null &&
                document.RootElement.TryGetProperty("name", out var name) &&
                !string.IsNullOrWhiteSpace(name.GetString()))
                _packageRoots[name.GetString()!] = DirectoryName(path);
        }
        catch { }
    }

    private static JsonDocument? ParseJson(string? content) =>
        content is null ? null : JsonDocument.Parse(content, new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

    private IEnumerable<string> ResolveRelative(
        string source, string reference, IReadOnlyList<string> extensions)
    {
        var stem = Combine(DirectoryName(source), reference.Replace('\\', '/'));
        return CandidateFiles(stem, extensions);
    }

    private IEnumerable<string> CandidateFiles(
        string stem, IReadOnlyList<string> extensions)
    {
        var clean = stem.Replace('\\', '/').TrimStart('/');
        var candidates = new List<string> { clean };
        candidates.AddRange(extensions.Select(extension =>
            extension.StartsWith('/') ? clean.TrimEnd('/') + extension :
            Path.HasExtension(clean) ? clean : clean + extension));
        foreach (var candidate in candidates)
        {
            var normalized = NormalizeExisting(candidate);
            if (normalized is not null) yield return normalized;
        }
    }

    private string? NormalizeExisting(string value)
    {
        try
        {
            var absolute = Path.GetFullPath(Path.Combine(settings.WorkspaceRoot,
                value.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(settings.WorkspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            var relative = Path.GetRelativePath(settings.WorkspaceRoot, absolute)
                .Replace('\\', '/');
            return _available.Contains(relative) ? relative : null;
        }
        catch { return null; }
    }

    private string? SafeRead(string path)
    {
        try { return workspace.Read(path); }
        catch { return null; }
    }

    private static string Combine(string left, string right) =>
        Path.GetFullPath(Path.Combine("C:\\context-root", left.Replace('/', '\\'),
                right.Replace('/', '\\')))
            .Replace("C:\\context-root\\", "", StringComparison.OrdinalIgnoreCase)
            .Replace('\\', '/');
    private static string DirectoryName(string path) =>
        (Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "").Trim('/');
    private static void Add(
        IDictionary<string, List<string>> index, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (!index.TryGetValue(key, out var paths)) index[key] = paths = [];
        if (!paths.Contains(value, StringComparer.OrdinalIgnoreCase)) paths.Add(value);
    }
    private static bool WordContains(string content, string value) =>
        Regex.IsMatch(content, $@"\b{Regex.Escape(value)}\b");
    private static bool AliasMatches(string alias, string reference) =>
        alias.Contains('*') ? reference.StartsWith(alias.Split('*')[0],
            StringComparison.OrdinalIgnoreCase) :
            alias.Equals(reference, StringComparison.OrdinalIgnoreCase);
    private static string AliasSuffix(string alias, string reference)
    {
        var prefix = alias.Split('*')[0];
        return reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? reference[prefix.Length..] : "";
    }
    private static string PackageName(string reference)
    {
        var parts = reference.Split('/');
        return reference.StartsWith('@') && parts.Length >= 2
            ? parts[0] + "/" + parts[1] : parts[0];
    }

    private static readonly Regex PythonImport = new(
        @"^\s*(?:from\s+(?<from>[.\w]+)\s+import\s+(?<names>[^#\r\n]+)|import\s+(?<import>[^#\r\n]+))",
        RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PhpInclude = new(
        @"\b(?:require|require_once|include|include_once)\s*\(?\s*['""](?<path>[^'""]+)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PhpUse = new(
        @"\buse\s+(?<name>[A-Za-z_\\][A-Za-z0-9_\\]+)",
        RegexOptions.Compiled);
    private static readonly Regex ScriptImport = new(
        @"(?:\b(?:import|export)\b[\s\S]*?\bfrom\s*|\brequire\s*\(|\bimport\s*\()\s*['""](?<path>[^'""]+)['""]",
        RegexOptions.Compiled);
    private static readonly Regex CSharpUsing = new(
        @"^\s*using\s+(?:static\s+)?(?<namespace>[A-Za-z_][A-Za-z0-9_.]+)\s*;",
        RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex CSharpNamespace = new(
        @"\bnamespace\s+(?<name>[A-Za-z_][A-Za-z0-9_.]+)",
        RegexOptions.Compiled);
    private static readonly Regex CSharpType = new(
        @"\b(?:class|struct|interface|record|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);
    private static readonly Regex HtmlReference = new(
        @"\b(?:src|href)\s*=\s*['""](?<path>[^'""]+)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CssReference = new(
        @"@import\s+(?:url\()?\s*['""](?<path>[^'""]+)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex XamlClass = new(
        @"\bx:Class\s*=\s*['""](?<type>[^'""]+)['""]", RegexOptions.Compiled);
    private static readonly Regex XamlNamespace = new(
        @"clr-namespace:(?<namespace>[A-Za-z_][A-Za-z0-9_.]+)",
        RegexOptions.Compiled);
}
