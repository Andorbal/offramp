using Reports;

namespace Legacy.Cycle
{
    /// <summary>(d) Needs Reports, which depends on Core: moving this to Core would be a cycle.</summary>
    public static class Reporter
    {
        public static string Describe(string name) => ReportWriter.Title(name);
    }
}
