using Offramp.Core.Diagnostics;
using Offramp.Refactoring.Codemods;

namespace Offramp.Refactoring.Tests;

public sealed class CodemodNoticeTests
{
    [Fact]
    public void A_reason_repeated_across_a_project_is_one_notice_at_its_first_site()
    {
        // As on DotNetNuke: 367 HttpContext.Current sites in a System.Web project, one reason.
        const string reason = "the project does not reference ASP.NET Core (IHttpContextAccessor).";
        CodemodSite[] sites =
        [
            Site("src/Web/A.cs", 3, reason),
            Site("src/Web/B.cs", 7, reason),
            Site("src/Web/C.cs", 1, "assigns HttpContext.Current."),
            Site("src/Web/D.cs", 2, null, CodemodSiteOutcome.Rewritten),
        ];
        var bag = new DiagnosticBag();

        CodemodRunner.ReportSkipped(bag, "src/Web/Web.csproj", sites);

        var notices = bag.ToSortedList();
        Assert.Equal(2, notices.Count);
        var repeated = notices.Single(d => d.File == "src/Web/A.cs");
        Assert.Equal("http-context: " + reason + " (2 sites in src/Web/Web.csproj; the result lists each.)", repeated.Message);
        Assert.Equal(2, repeated.Data["sites"]!.GetValue<int>());
        Assert.Equal("http-context: assigns HttpContext.Current.", notices.Single(d => d.File == "src/Web/C.cs").Message);
    }

    private static CodemodSite Site(string file, int line, string? reason, CodemodSiteOutcome outcome = CodemodSiteOutcome.Skipped) => new()
    {
        Codemod = "http-context",
        File = file,
        Line = line,
        Column = 1,
        Outcome = outcome,
        Reason = reason,
    };
}
