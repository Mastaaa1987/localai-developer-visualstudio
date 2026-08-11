using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LocalAI.Developer.Backend;

public sealed class CodeDiagnostic
{
    public string BackendId { get; set; } = "";
    public string Code { get; set; } = "";
    public string Severity { get; set; } = "Error";
    public string Message { get; set; } = "";
    public string Path { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
}

public interface ICodeAnalysisBackend
{
    string BackendId { get; }
    bool Supports(string path);
    string BuildContext(string path, string content);
    Task<IReadOnlyList<CodeDiagnostic>> ValidateAsync(
        string path, string content, CancellationToken cancellationToken);
}

public sealed class CodeAnalysisService
{
    private readonly BackendSettings _settings;
    private readonly List<ICodeAnalysisBackend> _backends;

    public CodeAnalysisService(
        BackendSettings settings,
        RoslynBackend roslyn,
        IEnumerable<ICodeAnalysisBackend>? additionalBackends = null)
    {
        _settings = settings;
        _backends =
        [
            new CSharpCodeAnalysisBackend(roslyn),
            new JsonCodeAnalysisBackend(),
            new XmlCodeAnalysisBackend(),
            new HtmlCodeAnalysisBackend(),
            new PythonCodeAnalysisBackend(settings),
            new PhpCodeAnalysisBackend(),
            new JavaScriptCodeAnalysisBackend(),
            new TypeScriptCodeAnalysisBackend(),
            new StructuredTextCodeAnalysisBackend()
        ];
        if (additionalBackends is not null)
            _backends.InsertRange(0, additionalBackends);
    }

    public IReadOnlyList<ICodeAnalysisBackend> Backends => _backends;

    public void Register(ICodeAnalysisBackend backend, bool prepend = true)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (_backends.Any(item => item.BackendId.Equals(
                backend.BackendId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Code analysis backend is already registered: {backend.BackendId}");
        if (prepend) _backends.Insert(0, backend);
        else _backends.Add(backend);
    }

    public bool Supports(string path) => _backends.Any(backend => backend.Supports(path));

    public string BuildContext(IEnumerable<string> relativePaths)
    {
        var parts = new List<string>();
        foreach (var relative in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var absolute = Path.IsPathRooted(relative)
                ? relative : Path.Combine(_settings.WorkspaceRoot, relative);
            if (!File.Exists(absolute)) continue;
            var backend = _backends.FirstOrDefault(item => item.Supports(relative));
            if (backend is null) continue;
            var context = backend.BuildContext(relative, File.ReadAllText(absolute));
            if (!string.IsNullOrWhiteSpace(context))
                parts.Add($"BACKEND {backend.BackendId}\n{context}");
        }
        return string.Join("\n\n", parts);
    }

    public async Task<IReadOnlyList<CodeDiagnostic>> ValidatePatchAsync(
        PreparedPatch patch, CancellationToken cancellationToken)
    {
        var diagnostics = new List<CodeDiagnostic>();
        foreach (var file in patch.Files.Where(file => file.Operation != "delete"))
        {
            var backend = _backends.FirstOrDefault(item => item.Supports(file.Path));
            if (backend is null) continue;
            diagnostics.AddRange(await backend.ValidateAsync(
                file.Path, file.Content, cancellationToken));
            if (diagnostics.Count >= 20) break;
        }
        return diagnostics.Take(20).ToArray();
    }

    public async Task<IReadOnlyList<CodeDiagnostic>> ValidateFilesAsync(
        IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var diagnostics = new List<CodeDiagnostic>();
        foreach (var value in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var absolute = Path.IsPathRooted(value)
                ? value : Path.Combine(_settings.WorkspaceRoot, value);
            if (!File.Exists(absolute)) continue;
            var backend = _backends.FirstOrDefault(item => item.Supports(value));
            if (backend is null) continue;
            diagnostics.AddRange(await backend.ValidateAsync(
                value, await File.ReadAllTextAsync(absolute, cancellationToken),
                cancellationToken));
            if (diagnostics.Count >= 50) break;
        }
        return diagnostics.Take(50).ToArray();
    }
}

internal sealed class CSharpCodeAnalysisBackend(RoslynBackend roslyn) : ICodeAnalysisBackend
{
    public string BackendId => "Roslyn";
    public bool Supports(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public string BuildContext(string path, string content) =>
        roslyn.BuildSemanticContext([path]);

    public Task<IReadOnlyList<CodeDiagnostic>> ValidateAsync(
        string path, string content, CancellationToken cancellationToken)
    {
        IReadOnlyList<CodeDiagnostic> result = roslyn.AnalyzeContent(path, content)
            .Select(item => new CodeDiagnostic
            {
                BackendId = BackendId, Code = item.Id, Severity = item.Severity,
                Message = item.Message, Path = item.Path,
                Line = item.Line, Column = item.Column
            }).ToArray();
        return Task.FromResult(result);
    }
}

internal sealed class JsonCodeAnalysisBackend : ICodeAnalysisBackend
{
    public string BackendId => "System.Text.Json";
    public bool Supports(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                                         path.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase);
    public string BuildContext(string path, string content) =>
        $"FILE {path}\nJSON document · {content.Length} characters";

    public Task<IReadOnlyList<CodeDiagnostic>> ValidateAsync(
        string path, string content, CancellationToken cancellationToken)
    {
        try
        {
            JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = path.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase),
                CommentHandling = path.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase)
                    ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow
            });
            return Empty();
        }
        catch (JsonException error)
        {
            return One(BackendId, "InvalidJson", path, error.Message,
                (int)(error.LineNumber ?? 0) + 1, (int)(error.BytePositionInLine ?? 0) + 1);
        }
    }

    private static Task<IReadOnlyList<CodeDiagnostic>> Empty() =>
        Task.FromResult<IReadOnlyList<CodeDiagnostic>>([]);
    private static Task<IReadOnlyList<CodeDiagnostic>> One(
        string backend, string code, string path, string message, int line = 0, int column = 0) =>
        Task.FromResult<IReadOnlyList<CodeDiagnostic>>([new CodeDiagnostic
        {
            BackendId = backend, Code = code, Path = path, Message = message,
            Line = line, Column = column
        }]);
}

internal sealed class XmlCodeAnalysisBackend : ICodeAnalysisBackend
{
    private static readonly string[] Extensions =
        [".xml", ".xaml", ".config", ".csproj", ".props", ".targets", ".resx", ".svg"];
    public string BackendId => "System.Xml.Linq";
    public bool Supports(string path) => Extensions.Contains(
        Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public string BuildContext(string path, string content)
    {
        try
        {
            var root = XDocument.Parse(content).Root;
            return $"FILE {path}\nROOT {root?.Name.LocalName ?? "<empty>"}";
        }
        catch { return $"FILE {path}\nXML document"; }
    }
    public Task<IReadOnlyList<CodeDiagnostic>> ValidateAsync(
        string path, string content, CancellationToken cancellationToken)
    {
        try { XDocument.Parse(content, LoadOptions.SetLineInfo); return Empty(); }
        catch (Exception error) { return Error(BackendId, "InvalidXml", path, error.Message); }
    }
    internal static Task<IReadOnlyList<CodeDiagnostic>> Empty() =>
        Task.FromResult<IReadOnlyList<CodeDiagnostic>>([]);
    internal static Task<IReadOnlyList<CodeDiagnostic>> Error(
        string backend, string code, string path, string message) =>
        Task.FromResult<IReadOnlyList<CodeDiagnostic>>([new CodeDiagnostic
            { BackendId = backend, Code = code, Path = path, Message = message }]);
}

internal sealed partial class HtmlCodeAnalysisBackend : ICodeAnalysisBackend
{
    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta",
          "param", "source", "track", "wbr", "!doctype" };
    public string BackendId => "HtmlStructure";
    public bool Supports(string path) =>
        Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".htm", StringComparison.OrdinalIgnoreCase);
    public string BuildContext(string path, string content) =>
        $"FILE {path}\nELEMENTS {TagRegex().Matches(content).Count}";
    public Task<IReadOnlyList<CodeDiagnostic>> ValidateAsync(
        string path, string content, CancellationToken cancellationToken)
    {
        var stack = new Stack<string>();
        foreach (Match match in TagRegex().Matches(RemoveComments().Replace(content, "")))
        {
            var closing = match.Groups[1].Success;
            var name = match.Groups[2].Value;
            if (VoidTags.Contains(name) || match.Value.EndsWith("/>", StringComparison.Ordinal)) continue;
            if (!closing) { stack.Push(name); continue; }
            if (stack.Count == 0 || !stack.Pop().Equals(name, StringComparison.OrdinalIgnoreCase))
                return XmlCodeAnalysisBackend.Error(BackendId, "InvalidHtml", path,
                    $"Unexpected closing tag: {name}");
        }
        return stack.Count == 0 ? XmlCodeAnalysisBackend.Empty() :
            XmlCodeAnalysisBackend.Error(BackendId, "InvalidHtml", path,
                $"Unclosed tag: {stack.Peek()}");
    }

    [GeneratedRegex(@"<\s*(/)?\s*([A-Za-z][A-Za-z0-9:-]*|!doctype)\b[^>]*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();
    [GeneratedRegex(@"<!--[\s\S]*?-->")]
    private static partial Regex RemoveComments();
}

internal abstract class ExternalLanguageBackend : ICodeAnalysisBackend
{
    public abstract string BackendId { get; }
    public abstract bool Supports(string path);
    protected abstract IEnumerable<(string Executable, string[] Arguments)> Commands(
        string sourcePath, string temporaryFile);
    public virtual string BuildContext(string path, string content) =>
        GenericContext(path, content);

    public async Task<IReadOnlyList<CodeDiagnostic>> ValidateAsync(
        string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "localai-validation-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, Path.GetFileName(path));
        await File.WriteAllTextAsync(file, content, cancellationToken);
        try
        {
            foreach (var command in Commands(path, file))
            {
                try
                {
                    var info = new ProcessStartInfo(command.Executable)
                    {
                        WorkingDirectory = directory, UseShellExecute = false,
                        CreateNoWindow = true, RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
                    using var process = Process.Start(info);
                    if (process is null) continue;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        try { if (!process.HasExited) process.Kill(true); } catch { }
                        try
                        {
                            await process.WaitForExitAsync(CancellationToken.None)
                                .WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
                            await Task.WhenAll(outputTask, errorTask)
                                .WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
                        }
                        catch { }
                        if (cancellationToken.IsCancellationRequested) throw;
                        continue;
                    }
                    var output = (await outputTask + await errorTask).Trim();
                    if (process.ExitCode == 0) return [];
                    if (ExternalToolAvailability.IsUnavailableOutput(output)) continue;
                    return [new CodeDiagnostic
                    {
                        BackendId = BackendId, Code = "InvalidSyntax", Path = path,
                        Message = string.IsNullOrWhiteSpace(output)
                            ? $"{BackendId} syntax validation failed." : output
                    }];
                }
                catch (System.ComponentModel.Win32Exception) { }
            }
            return ValidateStructure(path, content, BackendId);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    internal static string GenericContext(string path, string content)
    {
        var declarations = content.Split('\n')
            .Select(line => line.Trim())
            .Where(line => Regex.IsMatch(line,
                @"^(import|from|require|include|use|class|interface|trait|def|function|export)\b"))
            .Take(80);
        return $"FILE {path}\n" + string.Join("\n", declarations);
    }

    internal static IReadOnlyList<CodeDiagnostic> ValidateStructure(
        string path, string content, string backend)
    {
        var stack = new Stack<char>();
        var pairs = new Dictionary<char, char> { [')'] = '(', [']'] = '[', ['}'] = '{' };
        char quote = '\0';
        var escaped = false;
        for (var index = 0; index < content.Length; index++)
        {
            var value = content[index];
            if (quote != '\0')
            {
                if (escaped) { escaped = false; continue; }
                if (value == '\\') { escaped = true; continue; }
                if (value == quote) quote = '\0';
                continue;
            }
            if (value is '\'' or '"' or '`') { quote = value; continue; }
            if (value is '(' or '[' or '{') stack.Push(value);
            else if (pairs.TryGetValue(value, out var expected) &&
                     (stack.Count == 0 || stack.Pop() != expected))
                return [new CodeDiagnostic
                {
                    BackendId = backend, Code = "UnbalancedDelimiter", Path = path,
                    Message = $"Unexpected delimiter '{value}'."
                }];
        }
        if (quote != '\0' || stack.Count > 0)
            return [new CodeDiagnostic
            {
                BackendId = backend, Code = "UnclosedStructure", Path = path,
                Message = quote != '\0' ? "Unterminated string literal." :
                    $"Unclosed delimiter '{stack.Peek()}'."
            }];
        return [];
    }
}

public static class ExternalToolAvailability
{
    private static readonly string[] UnavailableMessages =
    [
        "python was not found",
        "python wurde nicht gefunden",
        "microsoft store",
        "app execution aliases",
        "app-ausführungsaliase",
        "app-ausf",
        "is not recognized as an internal or external command",
        "command not found",
        "no such file or directory",
        "the application to execute does not exist",
        "could not execute because the specified command or file was not found"
    ];

    public static bool IsUnavailableOutput(string output) =>
        !string.IsNullOrWhiteSpace(output) && UnavailableMessages.Any(message =>
            output.Contains(message, StringComparison.OrdinalIgnoreCase));
}

internal sealed class PythonCodeAnalysisBackend(BackendSettings settings) : ExternalLanguageBackend
{
    public override string BackendId => "Python";
    public override bool Supports(string path) =>
        new[] { ".py", ".pyi", ".pyw" }.Contains(
            Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    protected override IEnumerable<(string Executable, string[] Arguments)> Commands(
        string sourcePath, string temporaryFile)
    {
        foreach (var executable in PythonInterpreterResolver.ResolveProjectInterpreters(
                     settings.WorkspaceRoot, sourcePath))
            yield return (executable, ["-m", "py_compile", temporaryFile]);
        yield return ("py", ["-3", "-m", "py_compile", temporaryFile]);
        yield return ("python", ["-m", "py_compile", temporaryFile]);
        yield return ("python3", ["-m", "py_compile", temporaryFile]);
    }
}

public static class PythonInterpreterResolver
{
    private static readonly string[] EnvironmentNames = ["env", ".venv", "venv"];

    public static IReadOnlyList<string> ResolveProjectInterpreters(
        string workspaceRoot, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return [];
        var root = Path.GetFullPath(workspaceRoot);
        var source = Path.IsPathRooted(sourcePath)
            ? Path.GetFullPath(sourcePath)
            : Path.GetFullPath(Path.Combine(root, sourcePath));
        var directory = Path.GetDirectoryName(source) ?? root;
        var candidates = new List<string>();

        while (IsWithinRoot(root, directory))
        {
            foreach (var environmentName in EnvironmentNames)
            {
                var executable = Path.Combine(directory, environmentName, "Scripts", "python.exe");
                if (File.Exists(executable)) candidates.Add(executable);
            }
            if (directory.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
            var parent = Directory.GetParent(directory)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || parent.Equals(
                    directory, StringComparison.OrdinalIgnoreCase)) break;
            directory = parent;
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) +
                             Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               Path.TrimEndingDirectorySeparator(path).Equals(
                   Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class PhpCodeAnalysisBackend : ExternalLanguageBackend
{
    public override string BackendId => "PHP";
    public override bool Supports(string path) =>
        new[] { ".php", ".phtml" }.Contains(
            Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    protected override IEnumerable<(string Executable, string[] Arguments)> Commands(
        string sourcePath, string temporaryFile)
    {
        yield return ("php", ["-l", temporaryFile]);
    }
}

internal sealed class JavaScriptCodeAnalysisBackend : ExternalLanguageBackend
{
    private static readonly string[] Extensions = [".js", ".mjs", ".cjs"];
    public override string BackendId => "JavaScript";
    public override bool Supports(string path) => Extensions.Contains(
        Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    protected override IEnumerable<(string Executable, string[] Arguments)> Commands(
        string sourcePath, string temporaryFile)
    {
        yield return ("node", ["--check", temporaryFile]);
    }
}

internal sealed class TypeScriptCodeAnalysisBackend : ExternalLanguageBackend
{
    private static readonly string[] Extensions = [".ts", ".tsx"];
    public override string BackendId => "TypeScript";
    public override bool Supports(string path) => Extensions.Contains(
        Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    protected override IEnumerable<(string Executable, string[] Arguments)> Commands(
        string sourcePath, string temporaryFile)
    {
        yield return ("tsc", ["--noEmit", "--pretty", "false", "--skipLibCheck",
            "--jsx", "preserve", temporaryFile]);
    }
}

internal sealed class StructuredTextCodeAnalysisBackend : ICodeAnalysisBackend
{
    private static readonly string[] Extensions =
        [".jsx", ".css", ".scss", ".less", ".yaml", ".yml", ".toml", ".md",
         ".editorconfig", ".gitignore"];
    public string BackendId => "StructuredText";
    public bool Supports(string path) => Extensions.Contains(
        Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ||
        Extensions.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
    public string BuildContext(string path, string content) =>
        ExternalLanguageBackend.GenericContext(path, content);
    public Task<IReadOnlyList<CodeDiagnostic>> ValidateAsync(
        string path, string content, CancellationToken cancellationToken) =>
        Task.FromResult(ExternalLanguageBackend.ValidateStructure(path, content, BackendId));
}
