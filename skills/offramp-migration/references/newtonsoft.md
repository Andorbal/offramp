# Upgrading Newtonsoft.Json with Offramp

Old Newtonsoft.Json (7 to 12) is usually why a solution has several versions of one package and
binding redirects that hide the mismatch. Redirects work at run time only, so moving code between
projects on different versions fails with CS1705, and raising the version changes JSON nobody
tested. The `newtonsoft` commands upgrade in three reviewable steps.
`docs/spec/commands/newtonsoft.md` is the contract.

## 1. See what would change: `newtonsoft scan`

Read-only. For every project that uses Newtonsoft.Json it reports:

- How the project references Newtonsoft.Json, and the binding redirects in its own config file.
- Every *site* (a call that serializes, deserializes, or parses JSON), with:
  - its payload types;
  - where its settings come from (`none`, `inline`, a static `member`, or `unknown`);
  - its *entry point*: the nearest method a test can call, or the reason there is none (`notAccessible`, `notConstructible`, `typeNotGeneratable`, `nonHermetic`, `notObservable`, `inputNotParameter`, `depthExceeded`).
- **API changes**: the project compiled against the target. OFR3701 is an error, meaning the upgrade will not build until the member is replaced. OFR3702 marks members that become obsolete.
- **Behavior changes**: OFR3703–3713 point at code, and OFR3719 lists changes that depend on data. Each one was measured against the real releases, and fires only where the release in use and the target behave differently.

Check findings before reporting them (`checking-results.md`). For OFR3703, look at the
`[JsonProperty]` name. For OFR3709, check whether real payloads nest past 64 levels.

## 2. Record what the code does today: `newtonsoft tests`

1. `newtonsoft tests` (dry run) shows the probes it would write into each test project:
   - **entry probes** call the entry point;
   - **type probes** call Newtonsoft.Json with the site's own settings, where there is no entry point;
   - **reading probes** are fed real payloads and what the code's own writing probes record.

   Uncovered sites are listed with their reason.
2. Give real payloads for sites that read external JSON. Put them in a folder as
   `<payload type full name>[.<label>].json` and pass `--samples DIR`. Payloads can hold sensitive
   data: the dry run lists every file it would copy, so ask the user before copying.
3. `newtonsoft tests --record --apply` writes the probes, builds, runs them twice, and writes the
   baselines.
   - **Where to run it.** Test projects that target only .NET Framework run on Windows only (OFR3739). Record on Windows, or on a CI agent there.
   - **OFR3745** means the test project loaded another Newtonsoft.Json than production. With `Microsoft.NET.Test.Sdk` 17.x on .NET targets, update it to 18.x.
   - **OFR3738** quarantines probes whose output changes between runs.
4. The user reviews and commits the probes and baselines as their own change. `newtonsoft tests --verify` should then report every probe `same`.

## 3. Upgrade: `newtonsoft upgrade`

1. Run the dry run and read the diff. It covers:
   - PackageReference and central versions;
   - the `packages.config` version, edited in place;
   - the `Reference` identity and the `HintPath` folders;
   - redirects synced over the upgraded model.

   OFR3764 (`allowedVersions`) and OFR3765 (a package whose range leaves out the target) name projects that need a decision first.
2. `--apply` applies the edits, then restores, builds, and verifies the probes.
   - **A failed build or restore rolls back** (OFR3761, OFR3766). The message names the OFR3701 findings that predicted it.
   - **Probes that observe something new are OFR3762**, with a line diff each, and the command exits 1. The upgrade stays applied (`--on-diff keep`), so the user can judge each difference. Use `--on-diff rollback` to undo it automatically.
3. **For each difference**, decide with the user:
   - Accept it, and re-record the baseline with `newtonsoft tests --record --apply`.
   - Or restore the old behavior in code, for example `[JsonProperty]` for a camel-case change, `[Serializable]` for an `ISerializable` type, or `MaxDepth` for deep payloads.

   Never re-record a baseline just to make the run pass.
4. **If no baselines were recorded** (OFR3767), only the compiler checked the upgrade. Say so plainly.

Undo any applied step with `move rollback --journal <journal>`. Recording has a journal of its own
(`baselineJournal`).
