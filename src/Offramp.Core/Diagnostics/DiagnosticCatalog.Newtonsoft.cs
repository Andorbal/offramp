namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string NewtonsoftArea = "newtonsoft";

    public static readonly DiagnosticDescriptor OFR3701 = new(
        "OFR3701", Severity.Error,
        "API removed or changed in the target Newtonsoft.Json",
        "The project's own compilation, with the target release's Newtonsoft.Json.dll in place of the one it uses, has an error the original does not. The message names the member and the compiler's first message; the finding lists every place.",
        "Members removed in 10.0.1 (JsonConvert's async methods, JsonConverter.GetSchema, the JsonContract callbacks, DefaultContractResolver's obsolete constructor), or an override of a member whose signature changed.",
        "Replace the member before upgrading; the message names the replacement where the rule pack knows one.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3702 = new(
        "OFR3702", Severity.Warning,
        "API obsolete in the target Newtonsoft.Json",
        "The project's compilation against the target release reports CS0618 or CS0612 for a member that is not obsolete in the release it uses.",
        "TypeNameAssemblyFormat, the Binder properties, SerializationBinder, and Newtonsoft.Json.Bson, obsolete from 10.0.1.",
        "Move to the replacement (TypeNameAssemblyFormatHandling, SerializationBinder of type ISerializationBinder, the Newtonsoft.Json.Bson package); with warnings as errors the upgrade does not build until then.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3703 = new(
        "OFR3703", Severity.Warning,
        "camel-cased JSON name changes",
        "A camel-case contract resolver or naming strategy writes a payload member's name differently in the target than in the release in use (names with spaces after capitals: 'URL VALUE' became 'url VALUE' in 11.0.1, 'A B' became 'a B' in 8.0.3). The finding lists each old and new name.",
        "[JsonProperty] names with spaces under CamelCasePropertyNamesContractResolver.",
        "Pin the name ([JsonProperty] with the naming strategy's OverrideSpecifiedNames off) if consumers depend on it, or update the consumers.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3704 = new(
        "OFR3704", Severity.Warning,
        "ISerializable type without [Serializable] changes shape",
        "A payload type implements ISerializable without [Serializable]. Before 11.0.1 it is written and read through GetObjectData and its serialization constructor; from 11.0.1 through its public members, so the JSON and what is read back change.",
        "Types that implement ISerializable for BinaryFormatter or remoting and are also sent as JSON.",
        "Add [Serializable] to keep the ISerializable shape, or accept the member shape and convert stored data.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3705 = new(
        "OFR3705", Severity.Warning,
        "enum dictionary keys use [EnumMember] values",
        "A payload member is a dictionary keyed by an enum with [EnumMember]. From 11.0.1 keys are written as the [EnumMember] values instead of the names (and reading those values stops throwing).",
        "Enums with [EnumMember] for their string form, used as dictionary keys.",
        "Make stored JSON and consumers accept the [EnumMember] values, or remove [EnumMember].",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3706 = new(
        "OFR3706", Severity.Info,
        "decimal scale when read differs",
        "A deserialize site reads decimal members, and the release in use and the target disagree on whether 1.10 (or 0.00) keeps its scale: 10.x drops it, 13.0.2 drops it for zero, the others keep it.",
        "Projects on 10.x or 13.0.2.",
        "Compare decimals numerically and format them explicitly where the text matters.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3707 = new(
        "OFR3707", Severity.Warning,
        "caught exception is no longer the one thrown",
        "A catch clause catches the exception the release in use throws from a call in its try block, and the target throws another (JToken.ToObject of a mismatched token: InvalidCastException became JsonSerializationException in 13.0.1; an invalid \\u escape: FormatException became JsonReaderException in 10.0.1).",
        "Code that relies on the exception type to detect bad input.",
        "Catch the type the target throws (both while both versions run).",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3708 = new(
        "OFR3708", Severity.Warning,
        "stricter integer handling in StringEnumConverter",
        "A StringEnumConverter with AllowIntegerValues = false. From 10.0.1 it also rejects integer strings (\"1\") when reading and enum values without a name when writing.",
        "Payloads that carry enums as numeric strings, or flags and out-of-range values.",
        "Accept the values explicitly or clean the data before upgrading.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3709 = new(
        "OFR3709", Severity.Warning,
        "reading stops at a depth of 64",
        "A deserialize site reads a type that contains itself, and its settings do not set MaxDepth. From 13.0.1 readers stop at 64 levels with a JsonReaderException.",
        "Trees, threads of replies, nested categories.",
        "Set MaxDepth on the settings if real payloads nest deeper than 64 levels.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3710 = new(
        "OFR3710", Severity.Info,
        "serializer's DateTimeZoneHandling applies in ToObject",
        "JToken.ToObject with a serializer, for a type with DateTime members. From 12.0.1 the serializer's DateTimeZoneHandling applies, so the DateTime.Kind read back can change.",
        "ToObject(JsonSerializer.Create(settings)) with DateTimeZoneHandling set.",
        "Check the Kind the code expects after ToObject.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3711 = new(
        "OFR3711", Severity.Info,
        "[DataContract] collection written as an object in 11.0.1",
        "A payload type with [DataContract] implements IEnumerable. 11.0.1 alone writes it as an object; other releases as an array.",
        "Projects on 11.0.1.",
        "JSON written by 11.0.1 has the object shape; the target writes an array again.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3712 = new(
        "OFR3712", Severity.Warning,
        "exception without [Serializable] could not be written in 11.x",
        "A payload type is an exception without [Serializable]. 11.x throws when serializing it; other releases write it.",
        "Projects on 11.x that serialize exceptions.",
        "Code that worked around the 11.x failure can be removed on the target.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3713 = new(
        "OFR3713", Severity.Info,
        "immutable interfaces deserialize to the hash variants",
        "A payload member is IImmutableSet<T> or IImmutableDictionary<K,V>. From 12.0.3 they deserialize to ImmutableHashSet and ImmutableDictionary instead of the sorted variants, so iteration order changes.",
        "Code that iterates a deserialized immutable set or dictionary in order.",
        "Sort explicitly where order matters.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3719 = new(
        "OFR3719", Severity.Info,
        "behavior change that depends on data",
        "The release in use and the target behave differently for some input, and no code pattern shows whether the project's data has it: comments inside parsed JSON arrays (before 8.0.1), negative zero (13.0.2), decimal overflow (10.x, 11.x). Reported once per project and change.",
        "Projects that parse or read JSON whose content Offramp cannot see.",
        "Record characterization probes with real payloads (`offramp newtonsoft tests --samples`) before upgrading.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3720 = new(
        "OFR3720", Severity.Warning,
        "payload type unknown",
        "A serialization site's payload type could not be determined: an object, interface, or type-parameter argument that does not come from a parameter, or a non-generic deserialize without a typeof or a cast.",
        "Generic helpers, values held in object fields, DeserializeObject(string) used as a DOM.",
        "The behavior rules cannot judge the site; give it a typed overload, or record probes through its callers.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3721 = new(
        "OFR3721", Severity.Info,
        "settings cannot be reproduced",
        "A site's settings come from a parameter, an instance member, a local assigned more than once, or anything else a test cannot recreate; or the project assigns JsonConvert.DefaultSettings, which changes every call without settings.",
        "Settings built in several statements or injected.",
        "Probes of the site use the settings they can reproduce; move the settings to a static member to make them reproducible.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3722 = new(
        "OFR3722", Severity.Warning,
        "several Newtonsoft.Json versions in the solution",
        "Projects in the solution use different releases of Newtonsoft.Json; each project is judged against its own.",
        "packages.config projects updated one at a time, binding redirects hiding the difference.",
        "Upgrade them together (`offramp newtonsoft upgrade`).",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3723 = new(
        "OFR3723", Severity.Warning,
        "loose Newtonsoft.Json DLL",
        "The project references a Newtonsoft.Json.dll that no package provides; its version is read from the assembly, and the upgrade cannot edit it.",
        "A lib/ folder checked into the repository.",
        "Turn it into a package reference first (`offramp deps resolve-dlls`).",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3724 = new(
        "OFR3724", Severity.Info,
        "already at the target Newtonsoft.Json",
        "The project already uses the target release; nothing changes for it.",
        "Projects upgraded earlier.",
        "None.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3725 = new(
        "OFR3725", Severity.Warning,
        "target Newtonsoft.Json unavailable; API changes not computed",
        "Neither the global packages folder nor the configured feeds have the target release, so the trial compilation (OFR3701, OFR3702) did not run.",
        "No network, or feeds that do not mirror nuget.org.",
        "Restore the target once (any project referencing it), or configure a feed that has it.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3760 = new(
        "OFR3760", Severity.Error,
        "no target Newtonsoft.Json release",
        "No stable, listed release of Newtonsoft.Json in newtonsoft.target (or the --version given) supports every target framework of the projects that use it, or the release asked for does not exist.",
        "A range or version with a typo, or feeds that do not mirror nuget.org.",
        "Fix newtonsoft.target or --version, or configure a feed that has the release.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3731 = new(
        "OFR3731", Severity.Warning,
        "site has no probe",
        "No characterization probe exercises the site: no member a test can call reaches it (the scan's reason), its payload or settings cannot be written down, or its probe did not compile. The message gives the reason; the result lists every uncovered site.",
        "Sites behind file or network access, in classes that need injected dependencies, or with settings built in several statements.",
        "Give the site a hermetic, public entry point (or settings in a static member), or accept that the upgrade checks it by compilation only.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3732 = new(
        "OFR3732", Severity.Warning,
        "reading site without a payload to read",
        "A deserialize or DOM site has no real payload (--samples) for its type and nothing in the solution writes that type with the same settings, so a probe would read JSON the code never sees.",
        "JSON that arrives from outside: an API response, a file, a queue message.",
        "Put a real payload in the samples folder as <payload type full name>[.<label>].json and run again.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3733 = new(
        "OFR3733", Severity.Error,
        "no test project covers the production project",
        "No test project references the production project or is named after it, so its probes have nowhere to go. Test projects are not created.",
        "Production projects without tests.",
        "Create a test project that references it, or name one with --to or newtonsoft.tests.projects.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3734 = new(
        "OFR3734", Severity.Error,
        "several test projects cover the production project",
        "More than one test project references the production project (or is named after it); the candidates are listed.",
        "A unit and an integration test project for the same library.",
        "Choose one with --to (with a single --project) or newtonsoft.tests.projects.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3735 = new(
        "OFR3735", Severity.Warning,
        "generated probe does not compile; dropped",
        "A probe compiled into the test project's recorded compilation has an error; it is left out (with probes that read what it writes), and the first error is shown.",
        "Members the test project cannot see, types the generated code names ambiguously, or a C# version older than 6.",
        "Report the error with the probe; the site stays uncovered.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3736 = new(
        "OFR3736", Severity.Warning,
        "test framework not supported",
        "The test project uses none of xunit, NUnit, or MSTest (v1 or v2), so no probes are generated into it.",
        "TUnit, MbUnit, or a framework Offramp cannot see in the project's references.",
        "Name another test project with --to or newtonsoft.tests.projects.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3737 = new(
        "OFR3737", Severity.Warning,
        "sample file matches no payload type or is not JSON",
        "A file in the samples folder is not named <payload type full name>[.<label>].json for a type some deserialize site reads, or does not parse as JSON; it is not copied.",
        "A typo in the type name, a payload of a type no site reads, or a file that is not JSON.",
        "Rename or fix the file.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3742 = new(
        "OFR3742", Severity.Info,
        "generated file taken over; left alone",
        "A file in the generated folder no longer starts with Offramp's <auto-generated> header, so it belongs to the user: it is not rewritten or deleted, and the probes it would hold are not written.",
        "Probes edited by hand.",
        "None; restore the header (or delete the file) to have Offramp manage it again.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3743 = new(
        "OFR3743", Severity.Warning,
        "test project binds another Newtonsoft.Json; type probes skipped",
        "Type probes call Newtonsoft.Json from the test project, so they need it to compile against the same assembly as the production project; it does not (or has none), and only entry probes are generated.",
        "A test project that references another Newtonsoft.Json version, or none.",
        "Reference the production project's Newtonsoft.Json from the test project, or rely on entry probes.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3738 = new(
        "OFR3738", Severity.Warning,
        "probe output differs between runs; quarantined",
        "Recording ran every probe more than once and this probe observed something different each time, so no baseline is written for it; the differing lines are shown.",
        "Hash-ordered collections (HashSet<string> of strings, whose order changes per process), the clock or random numbers the hermetic check could not see, shared static state.",
        "Make the output deterministic (sort, inject the clock), or accept that the site is checked by compilation only.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3739 = new(
        "OFR3739", Severity.Warning,
        ".NET Framework tests cannot run on this OS",
        "The test project targets only .NET Framework, which runs on Windows; its probes were not run here. The message gives the command for a Windows machine or agent.",
        "Recording or verifying a legacy solution on macOS or Linux.",
        "Run the same `offramp newtonsoft tests` on Windows, or add a modern .NET target to the test project.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3740 = new(
        "OFR3740", Severity.Error,
        "test build or run failed",
        "The test projects did not build, or a test run timed out, could not start, or failed while recording; the command and the end of its output are shown.",
        "A test project that does not build, a missing test adapter, or a Newtonsoft.Json assembly that does not load (7.x on .NET needs System.Security.Permissions).",
        "Fix the build or the run the message shows, then run again.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3741 = new(
        "OFR3741", Severity.Warning,
        "probe did not run",
        "A generated probe wrote no result: the test run did not reach it (a filter, a crash, a configured command that runs fewer tests).",
        "A newtonsoft.tests.command that runs a subset, or a test host that stopped early.",
        "Run the probes the command left out, or regenerate them.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3744 = new(
        "OFR3744", Severity.Warning,
        "probe has no baseline",
        "Verifying found a probe with nothing to compare with: it was generated after the last recording.",
        "New sites or regenerated probes since `--record`.",
        "Record it on the current release (`offramp newtonsoft tests --record --apply`) before upgrading.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3745 = new(
        "OFR3745", Severity.Error,
        "probe ran another Newtonsoft.Json than the production project uses",
        "The test project loaded a different Newtonsoft.Json than its production project references, so its baselines would describe another release; nothing is written for it.",
        "Microsoft.NET.Test.Sdk 17.x, whose test host brings Newtonsoft.Json 13 to .NET targets; a test project that references another version.",
        "Use Microsoft.NET.Test.Sdk 18.x or later, or reference the production project's Newtonsoft.Json from the test project.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3747 = new(
        "OFR3747", Severity.Warning,
        "test project has no VSTest adapter",
        "A legacy test project runs as `dotnet test <assembly> --test-adapter-path <output folder>`, and VSTest finds tests only through an adapter there (a `*.testadapter.dll`). Its output folder has none, so no probe would run; it is not run. The message names the package that brings the adapter for its framework.",
        "NUnit 2 and MSTest v1 projects that ran under Visual Studio's own runner or a console runner (`NUnit.Runners`) and never had an adapter package.",
        "Add the named package to the test project (`NUnitTestAdapter` 2.x for NUnit 2), or set `newtonsoft.tests.command` to run its tests another way.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3746 = new(
        "OFR3746", Severity.Error,
        "generated probes are out of date",
        "Recording or verifying runs the probes on disk, but generating them again would change them (the code or the configuration changed since).",
        "Sites added or changed since `newtonsoft tests --apply`.",
        "Run `offramp newtonsoft tests --apply` (or `--record --apply`, which writes them first).",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3762 = new(
        "OFR3762", Severity.Error,
        "probe observes a behavior change",
        "A probe's observation differs from its baseline: another value, or it throws where it returned (or the reverse). The first difference is shown as removed (baseline) and added (now) lines.",
        "A behavior change between the recorded release and the one running, as `newtonsoft scan` predicts, or a change in the code under test.",
        "Decide whether the change is acceptable; if it is, record the new baseline (`--record --apply`).",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3763 = new(
        "OFR3763", Severity.Info,
        "only an exception message changed",
        "The probe throws the same exception type as its baseline, with another message.",
        "Reworded messages between releases.",
        "None, unless code parses the message.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3761 = new(
        "OFR3761", Severity.Error,
        "build failed after the Newtonsoft.Json upgrade; rolled back",
        "The solution does not build with every project on the target; every edit was undone from the journal. The first error is shown, with the OFR3701 findings that predicted it.",
        "A member removed in 10.0.1, or an override of a member whose signature changed (`newtonsoft scan` lists them as OFR3701).",
        "Fix what OFR3701 lists on the old release, then upgrade again.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3764 = new(
        "OFR3764", Severity.Warning,
        "allowedVersions in packages.config leaves out the target",
        "The project's packages.config constrains Newtonsoft.Json with allowedVersions, and the target is outside it; the project is not upgraded.",
        "A constraint added long ago to stop an accidental upgrade.",
        "Widen or remove allowedVersions, then upgrade again.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3765 = new(
        "OFR3765", Severity.Warning,
        "a package's dependency leaves out the target Newtonsoft.Json",
        "A package the project uses depends on Newtonsoft.Json with a range that excludes the target, so restore reports NU1608 or fails.",
        "Old releases of packages built on Newtonsoft.Json with an upper bound.",
        "Upgrade that package as well (`offramp deps audit` lists releases that allow the target).",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3766 = new(
        "OFR3766", Severity.Error,
        "restore failed after the Newtonsoft.Json upgrade; rolled back",
        "`dotnet restore` (or the packages.config restore) failed with every project on the target; every edit was undone from the journal.",
        "A feed without the target, a package whose dependencies conflict with it, or no network.",
        "Fix what restore reports, then upgrade again.",
        NewtonsoftArea);

    public static readonly DiagnosticDescriptor OFR3767 = new(
        "OFR3767", Severity.Warning,
        "no characterization baselines; the upgrade was checked by the compiler only",
        "The solution builds on the target, but no test project has recorded probe baselines, so behavior changes were not looked for.",
        "Upgrading without `offramp newtonsoft tests --record --apply` first.",
        "Roll back (`offramp move rollback`), record baselines on the old release, and upgrade again.",
        NewtonsoftArea);
}
