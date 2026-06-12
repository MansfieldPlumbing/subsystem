using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Subsystem.Analyzers;

// MSBuild must be located BEFORE any Microsoft.CodeAnalysis.MSBuild type loads, so the real work lives
// in Runner (a separate type) invoked only after RegisterDefaults.
if (!MSBuildLocator.IsRegistered)
    MSBuildLocator.RegisterDefaults();

return await Runner.Run(args);

static class Runner
{
    // Located after MSBuild registration so the workspace assembly binds to the SDK's MSBuild.
    public static async Task<int> Run(string[] args)
    {
        var repoRoot = FindRepoRoot();
        var projectPath = Path.Combine(repoRoot, "src", "runspace", "Subsystem.csproj");

        string? refsSymbol = null;
        for (int i = 0; i < args.Length; i++)
            if (args[i] is "--refs" or "-r") { refsSymbol = i + 1 < args.Length ? args[i + 1] : null; }

        Console.Error.WriteLine($"check: loading {Path.GetFileName(projectPath)} (semantic load — first run is slow)…");
        using var ws = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create();
        ws.WorkspaceFailed += (_, e) =>
        {
            // Diagnostics from the design-time load (missing targets etc.) — show only failures.
            if (e.Diagnostic.Kind == Microsoft.CodeAnalysis.WorkspaceDiagnosticKind.Failure)
                Console.Error.WriteLine("  load: " + e.Diagnostic.Message);
        };

        var project = await ws.OpenProjectAsync(projectPath);

        // MSBuildWorkspace attaches analyzer references (ours + the android workload's) that don't resolve
        // as analyzer assemblies in THIS process — they become UnresolvedAnalyzerReference and crash the
        // solution serialization SymbolFinder relies on. We run analyzers ourselves, so strip them.
        var sol = project.Solution;
        foreach (var pid in sol.ProjectIds)
            sol = sol.WithProjectAnalyzerReferences(pid, Array.Empty<AnalyzerReference>());
        project = sol.GetProject(project.Id)!;

        var compilation = await project.GetCompilationAsync();
        if (compilation is null) { Console.Error.WriteLine("check: no compilation."); return 2; }

        return refsSymbol is null
            ? await CheckMode(project, compilation)
            : await RefsMode(project, compilation, refsSymbol);
    }

    // Default: run SS001-009 over the whole project, grouped report.
    static async Task<int> CheckMode(Microsoft.CodeAnalysis.Project project, Microsoft.CodeAnalysis.Compilation compilation)
    {
        var analyzers = LoadSubsystemAnalyzers();
        Console.Error.WriteLine($"check: running {analyzers.Length} analyzers (SS001-009)…");

        var withAnalyzers = compilation.WithAnalyzers(analyzers);
        var diags = await withAnalyzers.GetAnalyzerDiagnosticsAsync();

        var ss = diags.Where(d => d.Id.StartsWith("SS"))
                      .OrderBy(d => d.Id)
                      .ThenBy(d => d.Location.GetLineSpan().Path)
                      .ToList();

        foreach (var group in ss.GroupBy(d => d.Id).OrderBy(g => g.Key))
        {
            Console.WriteLine($"\n=== {group.Key} ({group.Count()}) — {group.First().Descriptor.Title} ===");
            foreach (var d in group)
            {
                var ls = d.Location.GetLineSpan();
                var file = Path.GetFileName(ls.Path);
                Console.WriteLine($"  {file}:{ls.StartLinePosition.Line + 1}  {d.GetMessage()}");
            }
        }
        Console.WriteLine($"\n--- {ss.Count} findings across {ss.Select(d => d.Location.GetLineSpan().Path).Distinct().Count()} files ---");
        return 0;
    }

    // --refs <Symbol>: every real reference to a named type/member, via SymbolFinder (NOT a text scan).
    static async Task<int> RefsMode(Microsoft.CodeAnalysis.Project project, Microsoft.CodeAnalysis.Compilation compilation, string symbolName)
    {
        var solution = project.Solution;
        var matches = new List<Microsoft.CodeAnalysis.ISymbol>();

        foreach (var type in AllTypes(compilation.GlobalNamespace))
        {
            if (type.Name == symbolName) matches.Add(type);
            foreach (var m in type.GetMembers())
                if (m.Name == symbolName) matches.Add(m);
        }
        if (matches.Count == 0) { Console.WriteLine($"refs: no symbol named '{symbolName}' in the compilation."); return 1; }

        foreach (var sym in matches.Distinct(SymbolEqualityComparer.Default).Cast<Microsoft.CodeAnalysis.ISymbol>())
        {
            Console.WriteLine($"\n=== {sym.Kind} {sym.ToDisplayString()} ===");
            var found = await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindReferencesAsync(sym, solution);
            int n = 0;
            foreach (var r in found)
                foreach (var loc in r.Locations)
                {
                    var ls = loc.Location.GetLineSpan();
                    Console.WriteLine($"  {Path.GetFileName(ls.Path)}:{ls.StartLinePosition.Line + 1}");
                    n++;
                }
            if (n == 0) Console.WriteLine("  (declared, no references)");
        }
        return 0;
    }

    static IEnumerable<Microsoft.CodeAnalysis.INamedTypeSymbol> AllTypes(Microsoft.CodeAnalysis.INamespaceSymbol ns)
    {
        foreach (var t in ns.GetTypeMembers()) yield return t;
        foreach (var child in ns.GetNamespaceMembers())
            foreach (var t in AllTypes(child)) yield return t;
    }

    static ImmutableArray<Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer> LoadSubsystemAnalyzers()
    {
        var asm = typeof(SS001PowerShellStringAnalyzer).Assembly;
        var list = asm.GetTypes()
            .Where(t => !t.IsAbstract && typeof(Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer).IsAssignableFrom(t))
            .Select(t => (Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer)Activator.CreateInstance(t)!)
            .ToImmutableArray();
        return list;
    }

    static string FindRepoRoot()
    {
        // Walk up from the executable to the dir containing 'src\runspace\Subsystem.csproj'.
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "runspace", "Subsystem.csproj")))
                return d.FullName;
        // Fallback: assume <drive>\subsystem relative to a <drive>\bin install.
        var driveRoot = Path.GetPathRoot(dir)
            ?? throw new InvalidOperationException("cannot resolve the repo root or a drive root from " + dir);
        return Path.Combine(driveRoot, "subsystem");
    }
}
