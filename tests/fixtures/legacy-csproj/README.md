# legacy-csproj fixture

Two legacy (non-SDK) C# projects as Visual Studio 2017 wrote them, for `csproj modernize`
and `config convert`. They build on any OS: `Directory.Build.props` gives legacy projects
the .NET Framework reference assemblies as a package, and the tests fill `packages/` the way
`nuget restore` would (`PackagesConfigRestore`).

| Project | What it exercises |
|---|---|
| `src/Billing` (library) | packages.config (`Newtonsoft.Json`) with a HintPath reference; a Compile list equal to the SDK glob; AssemblyInfo attributes the SDK generates, plus ones it does not (`InternalsVisibleTo`, `Guid`); a `.resx` with its designer file; a PostBuildEvent (OFR4302); a `ConfigurationSection` |
| `src/Billing.Tool` (exe) | a Compile list the glob would change (`Legacy/Old.cs` is left out: OFR4301); a linked file (`../Shared/Version.cs`); a ProjectReference with GUID metadata; `App.config` with appSettings, a connection string, a custom section, and binding redirects |

Converted, both compile the same sources, references (plus the SDK's implicit framework
references, and `Newtonsoft.Json` flowing from Billing's PackageReference), and resources.
