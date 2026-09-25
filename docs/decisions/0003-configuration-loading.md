# 0003. Load configuration as merged JSON trees validated by the published schema

- Status: accepted
- Date: 2026-09-25
- Spec section: `docs/spec/03-configuration.md`

## Context

The specification fixes the precedence (defaults, `offramp.yml`, `OFFRAMP_*`,
flags) and the diagnostics for unknown keys and missing reasons, but not the
built-in defaults for keys whose example values are clearly illustrative, how
environment variable names map to nested camelCase keys, how YAML scalars are
typed, what happens to unknown keys, or how the echoed `effectiveConfig` is
ordered.

## Decision

- **One representation.** Every layer becomes a `JsonNode` tree: defaults
  (serialized from the `OfframpConfig` record, so the typed model and the defaults
  cannot drift), the YAML file (YAML 1.2 core schema for plain scalars; quoted
  scalars are strings; numeric literals keep their text so `version: 2.0` binds as
  `"2.0"`), environment variables, and flags. Objects merge key by key; arrays and
  scalars replace.
- **Validation** uses `schemas/v1/config.json` (embedded in `Offramp.Core`).
  Unknown keys are `OFR0050` (warning, with a "did you mean" suggestion within
  edit distance 2) and are removed before merging, so they never appear in
  `effectiveConfig`. Any other violation is `OFR0053` (error) and the file is
  ignored as a whole; YAML syntax errors are `OFR0054`. Every file diagnostic
  carries the line and column.
- **Environment variables**: `OFFRAMP_` + segments separated by `__`, each
  `UPPER_SNAKE` segment camel-cased (`OFFRAMP_VERIFY__TIMEOUT_SECONDS` →
  `verify.timeoutSeconds`). The single-underscore names the spec lists are
  aliases: `OFFRAMP_TARGET`, `OFFRAMP_STATE` (`paths.state`),
  `OFFRAMP_LLM_PROVIDER|URL|MODEL`. Values are typed by the default at that path:
  integers, booleans (`true/false/1/0/yes/no`), lists split on `;` or `,`.
  Variables that name no setting are ignored (`OFFRAMP_LLM_API_KEY` is a secret,
  never configuration). Invalid values are `OFR0056`. Variables apply in ordinal
  name order.
- **Defaults** that the example shows only as illustrations stay empty:
  `verify.properties`, `deps.families`, `deps.pins`, `rules`. `deps.cpm.file`
  defaults to `Directory.Packages.props`; `report.title` defaults to null
  (derived later from the repository name). `deadCode.externalConsumers` is added
  now because `audit dead-code` refers to it.
- **`effectiveConfig`** is the merged tree with every object's keys in ordinal
  order, so the same inputs always produce the same bytes.

## Alternatives considered

- Binding YAML straight to typed records with YamlDotNet: rejected; unknown-key
  detection, positions, and precedence merging all become per-type code.
- Treating unknown keys as errors: rejected; the spec says warning, and a newer
  config read by an older tool should still run.
- Schema order for `effectiveConfig`: rejected; user-supplied maps (`rules`,
  `verify.properties`) need sorting anyway, and one rule is simpler to test.

## Consequences

- Adding a setting means adding it to the record and to the schema; a test
  asserts the defaults validate against the schema.
- The file on disk and the embedded schema are the same file, linked at build time.
