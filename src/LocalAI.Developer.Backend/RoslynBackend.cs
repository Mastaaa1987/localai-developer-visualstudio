using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LocalAI.Developer.Backend;

public sealed class RoslynBackend(BackendSettings settings)
{
    public IReadOnlyList<RoslynDiagnostic> AnalyzeFiles(IEnumerable<string> paths)
    {
        var diagnostics = new List<RoslynDiagnostic>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path)) continue;
            diagnostics.AddRange(AnalyzeContent(path, File.ReadAllText(path)));
        }
        return diagnostics;
    }

    public IReadOnlyList<RoslynDiagnostic> AnalyzeContent(string path, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source ?? "", path: path);
        return tree.GetDiagnostics().Where(item =>
                item.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(ToContract).ToArray();
    }

    public async Task<CompilationResult> CompileAsync(
        string kind, IEnumerable<string>? changedPaths, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var paths = changedPaths?.Select(ToAbsolute).ToArray() ??
                    Directory.EnumerateFiles(settings.WorkspaceRoot, "*.cs",
                            SearchOption.AllDirectories)
                        .Where(path => !IsGeneratedDirectory(path)).ToArray();
        var roslynDiagnostics = AnalyzeFiles(paths).ToList();
        if (roslynDiagnostics.Any(item => item.Severity == "Error"))
            return new CompilationResult
            {
                Success = false, Kind = kind, ExitCode = 1,
                Output = string.Join(Environment.NewLine,
                    roslynDiagnostics.Select(Format)),
                DurationMs = started.ElapsedMilliseconds,
                Backend = "RoslynSyntax", RoslynDiagnostics = roslynDiagnostics
            };

        var buildArguments = settings.CompileArguments.ToList();
        var buildTarget = "";
        if (RequiresBuildTarget(buildArguments))
        {
            buildTarget = ResolveBuildTarget(paths);
            if (string.IsNullOrWhiteSpace(buildTarget))
                return new CompilationResult
                {
                    Success = true,
                    Skipped = true,
                    Kind = kind,
                    ExitCode = 0,
                    Output = "Compilation skipped: No .sln, .slnx or .csproj " +
                             $"exists below workspace root: {settings.WorkspaceRoot}",
                    DurationMs = started.ElapsedMilliseconds,
                    Backend = "RoslynSyntax+RoslynBuild",
                    RoslynDiagnostics = roslynDiagnostics
                };
            buildArguments.Insert(buildArguments.FindIndex(argument =>
                string.Equals(argument, "build", StringComparison.OrdinalIgnoreCase)) + 1,
                buildTarget);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = settings.CompileExecutable,
            WorkingDirectory = settings.WorkspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        foreach (var argument in buildArguments)
            startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch (InvalidOperationException) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await stdout) + (await stderr);
        var infrastructureFailure = IsInfrastructureFailure(output);
        return new CompilationResult
        {
            Success = process.ExitCode == 0,
            InfrastructureFailure = infrastructureFailure,
            BuildTarget = buildTarget,
            Kind = kind,
            ExitCode = process.ExitCode,
            Output = output.Length <= 2_000_000 ? output : output[^2_000_000..],
            DurationMs = started.ElapsedMilliseconds,
            Backend = "RoslynSyntax+RoslynBuild",
            RoslynDiagnostics = roslynDiagnostics
        };
    }

    private static bool RequiresBuildTarget(IReadOnlyList<string> arguments) =>
        arguments.Any(argument => string.Equals(argument, "build",
            StringComparison.OrdinalIgnoreCase)) &&
        !arguments.Any(IsBuildTargetPath);

    private static bool IsBuildTargetPath(string argument) =>
        argument.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        argument.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
        argument.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private string ResolveBuildTarget(IReadOnlyList<string> changedPaths)
    {
        var rootSolutions = EnumerateTopLevel(settings.WorkspaceRoot, "*.slnx")
            .Concat(EnumerateTopLevel(settings.WorkspaceRoot, "*.sln"))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (rootSolutions.Length > 0) return rootSolutions[0];

        var projects = changedPaths
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(FindNearestProject)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (projects.Length == 1) return projects[0]!;

        var rootProjects = EnumerateTopLevel(settings.WorkspaceRoot, "*.csproj")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (rootProjects.Length == 1) return rootProjects[0];

        var nestedSolutions = EnumerateBuildFiles("*.slnx")
            .Concat(EnumerateBuildFiles("*.sln"))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (nestedSolutions.Length == 1) return nestedSolutions[0];

        var nestedProjects = EnumerateBuildFiles("*.csproj")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        return nestedProjects.Length == 1 ? nestedProjects[0] : "";
    }

    private string? FindNearestProject(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        var root = Path.GetFullPath(settings.WorkspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(directory) &&
               IsBelowOrEqual(directory, root))
        {
            var projects = EnumerateTopLevel(directory, "*.csproj").ToArray();
            if (projects.Length == 1) return projects[0];
            if (string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar), root,
                    StringComparison.OrdinalIgnoreCase)) break;
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }

    private IEnumerable<string> EnumerateBuildFiles(string pattern) =>
        Directory.EnumerateFiles(settings.WorkspaceRoot, pattern, SearchOption.AllDirectories)
            .Where(path => !IsGeneratedDirectory(path));

    private static IEnumerable<string> EnumerateTopLevel(string directory, string pattern) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
            : [];

    private static bool IsBelowOrEqual(string path, string root)
    {
        var full = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(root + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInfrastructureFailure(string output) =>
        output.Contains("MSB1003", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("Specify a project or solution file", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("Projekt- oder Projektmappendatei", StringComparison.OrdinalIgnoreCase);

    public string BuildSemanticContext(IEnumerable<string> relativePaths)
    {
        var parts = new List<string>();
        foreach (var relative in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var absolute = ToAbsolute(relative);
            if (!absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(absolute)) continue;
            var source = File.ReadAllText(absolute);
            var root = CSharpSyntaxTree.ParseText(source, path: relative)
                .GetCompilationUnitRoot();
            var declarations = root.DescendantNodes()
                .Where(node => node is Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax or
                               Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax or
                               Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax)
                .Select(node => node.ToString().Split('{')[0].Trim())
                .Where(value => value.Length > 0)
                .Take(120);
            parts.Add($"FILE {relative}\nSHA256 {WorkspaceService.Sha256(source)}\n" +
                      string.Join("\n", declarations));
        }
        return string.Join("\n\n", parts);
    }

    private string ToAbsolute(string value) => Path.IsPathRooted(value)
        ? value : Path.GetFullPath(Path.Combine(settings.WorkspaceRoot, value));

    private bool IsGeneratedDirectory(string path)
    {
        var relative = Path.GetRelativePath(settings.WorkspaceRoot, path);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => segment.Equals("Library", StringComparison.OrdinalIgnoreCase) ||
                                       segment.Equals("Temp", StringComparison.OrdinalIgnoreCase) ||
                                       segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                                       segment.Equals("bin", StringComparison.OrdinalIgnoreCase));
    }

    private static RoslynDiagnostic ToContract(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return new RoslynDiagnostic
        {
            Id = diagnostic.Id,
            Severity = diagnostic.Severity.ToString(),
            Message = diagnostic.GetMessage(),
            Path = span.Path.Replace('\\', '/'),
            Line = span.StartLinePosition.Line + 1,
            Column = span.StartLinePosition.Character + 1
        };
    }

    private static string Format(RoslynDiagnostic item) =>
        $"{item.Path}({item.Line},{item.Column}): {item.Severity} {item.Id}: {item.Message}";
}
