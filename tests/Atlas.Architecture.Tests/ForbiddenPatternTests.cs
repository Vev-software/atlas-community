using System.Text.RegularExpressions;
using Xunit;

namespace Vev.Atlas.Architecture.Tests;

/// <summary>
/// Source-level fitness checks for the rules that are about *how code is written*, not just assembly
/// references (AGENTS.md §1.4/§1.5, §4 auto-reject). These scan the product source tree and fail the
/// build on a planted violation.
/// </summary>
public sealed class ForbiddenPatternTests
{
    private static readonly string SrcRoot = LocateSrcRoot();

    // `if (plan == "enterprise")` and friends — the free/paid line must be entitlement data, not code.
    private static readonly Regex PlanCheck = new(
        """\bplan\b\s*==\s*["']""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Direct AI-provider calls — must go through the Fabric AI contract instead (handbook 10, §1.5).
    private static readonly Regex DirectProviderCall = new(
        @"api\.openai\.com|using\s+OpenAI|new\s+(OpenAI|AzureOpenAI|Anthropic)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Bypassing the tenant global query filter — allowed only as an explicit, audited opt-out (atlas#35).
    private static readonly Regex IgnoreQueryFilters = new(
        @"IgnoreQueryFilters\s*\(",
        RegexOptions.Compiled);

    // The marker that makes a cross-tenant read a deliberate, greppable decision rather than a silent one.
    private const string CrossTenantOptOutMarker = "cross-tenant:";

    [Fact]
    public void No_plan_equality_checks_anywhere_in_the_source()
    {
        var offenders = ScanFor(PlanCheck);
        Assert.True(offenders.Count == 0,
            "The free/paid line must be an entitlement decision, never `if (plan == …)`:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void No_direct_ai_provider_calls_in_the_source()
    {
        var offenders = ScanFor(DirectProviderCall);
        Assert.True(offenders.Count == 0,
            "AI must go through the Fabric AI contract, never a provider SDK directly:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Bypassing_the_tenant_filter_requires_an_explicit_audited_opt_out()
    {
        // The global query filter (atlas#35) is the default. EF's only escape hatch is
        // IgnoreQueryFilters(); every use in the product source must be annotated with the
        // `cross-tenant:` marker so a reviewer can see — and grep — each deliberate cross-tenant read.
        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);
            var inBlockComment = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var code = StripCommentsPreservingStrings(lines[i], ref inBlockComment);
                if (!IgnoreQueryFilters.IsMatch(code))
                {
                    continue;
                }

                var audited = lines[i].Contains(CrossTenantOptOutMarker, StringComparison.OrdinalIgnoreCase)
                    || (i > 0 && lines[i - 1].Contains(CrossTenantOptOutMarker, StringComparison.OrdinalIgnoreCase));
                if (!audited)
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Cross-tenant reads must be a deliberate, audited opt-out — annotate each IgnoreQueryFilters() " +
            $"with a `{CrossTenantOptOutMarker}` marker on the same or preceding line:\n" + string.Join('\n', offenders));
    }

    /// <summary>Every product source file under <c>src/</c>, skipping build output.</summary>
    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SrcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static List<string> ScanFor(Regex pattern)
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);
            var inBlockComment = false;
            for (var i = 0; i < lines.Length; i++)
            {
                // Match against code only — comments describe the very rules we forbid, so scanning
                // raw text would flag the documentation. String literals are kept so a provider
                // hostname or a real `plan == "…"` in code is still caught.
                var code = StripCommentsPreservingStrings(lines[i], ref inBlockComment);
                if (pattern.IsMatch(code))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        return offenders;
    }

    /// <summary>Return the line with // and /* */ comments removed but string-literal contents kept.</summary>
    private static string StripCommentsPreservingStrings(string line, ref bool inBlockComment)
    {
        var result = new System.Text.StringBuilder(line.Length);
        var inString = false;
        var i = 0;

        while (i < line.Length)
        {
            var c = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';

            if (inBlockComment)
            {
                if (c == '*' && next == '/') { inBlockComment = false; i += 2; }
                else { i++; }
            }
            else if (inString)
            {
                result.Append(c);
                if (c == '\\' && next != '\0') { result.Append(next); i += 2; }
                else { if (c == '"') { inString = false; } i++; }
            }
            else
            {
                if (c == '/' && next == '/') { break; }
                if (c == '/' && next == '*') { inBlockComment = true; i += 2; }
                else if (c == '"') { inString = true; result.Append(c); i++; }
                else { result.Append(c); i++; }
            }
        }

        return result.ToString();
    }

    private static string LocateSrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var src = Path.Combine(dir.FullName, "src");
            var hasSolution = File.Exists(Path.Combine(dir.FullName, "Atlas.sln"))
                || File.Exists(Path.Combine(dir.FullName, "Atlas.slnx"));
            if (Directory.Exists(src) && hasSolution)
            {
                return src;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Atlas 'src' directory from the test output path.");
    }
}
