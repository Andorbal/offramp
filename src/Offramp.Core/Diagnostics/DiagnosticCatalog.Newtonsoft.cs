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
}
