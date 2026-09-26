using Accounts.Directory;

namespace Accounts.Reports
{
    /// <summary>Clean: uses the portable entry type only.</summary>
    public sealed class ReportService
    {
        public string Header(DirectoryEntryInfo entry)
        {
            return "Report for " + entry.DisplayName;
        }
    }
}
