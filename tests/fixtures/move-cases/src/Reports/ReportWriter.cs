namespace Reports
{
    /// <summary>Reports depends on Core, so Core can never depend on Reports.</summary>
    public static class ReportWriter
    {
        public static string Title(string name) => "Report: " + name;
    }
}
