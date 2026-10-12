# 0068. Describe each Newtonsoft.Json behavior as version segments, from measurements

- Status: accepted
- Date: 2026-10-09
- Spec section: `docs/spec/commands/newtonsoft.md#newtonsoft-scan`

## Context

The spec first gave each behavior rule a `since` release and fired it when the
version in use was below `since` and the target at or above it. Before writing
the rules, the candidate changes from the release notes of 8.0.1 through 13.0.4
were measured: one program of 38 experiments, built against the `net45`
assembly of 14 releases (7.0.1, 8.0.3, 9.0.1, 10.0.1, 10.0.3, 11.0.1, 11.0.2,
12.0.1, 12.0.2, 12.0.3, 13.0.1, 13.0.2, 13.0.3, 13.0.4) and run on .NET 10.

What changed, by the first release that showed it:

| Experiment | Outputs |
|---|---|
| `ISerializable` without `[Serializable]`, written | `{"a":1.5,"c":"EUR"}` until 11.0.1, then `{"Amount":1.5,"Currency":"EUR"}` |
| enum dictionary key with `[EnumMember]`, written / read | names / throws until 11.0.1, then member values / reads |
| `decimal` `1.10`, read | `1.10`; `1.1` from 10.0.1; `1.10` again from 11.0.1 |
| `decimal` `0.00`, read | `0.00`; `0` in 10.x; `0.00`; `0` in 13.0.2; `0.00` from 13.0.3 |
| `JToken.ToObject<JObject>()` of an array | `InvalidCastException` until 13.0.1, then `JsonSerializationException` |
| `StringEnumConverter { AllowIntegerValues = false }`, `"1"` read / unnamed value written | accepted until 10.0.1, then throws |
| 70 levels deep, read or parsed | read until 13.0.1, then `JsonReaderException` |
| `ToObject` with `DateTimeZoneHandling.Unspecified` | `Utc` until 12.0.1, then `Unspecified` |
| `[DataContract]` collection, written | an array, except an object in 11.0.1 |
| exception without `[Serializable]`, written | written; throws in 11.x; written from 12.0.1 |
| `IImmutableSet`/`IImmutableDictionary`, read | sorted variants until 12.0.3, then hash variants |
| `JArray.Parse("[1,/*c*/2]").Count` | 3 until 8.x, then 2 |
| invalid `\u` escape | `FormatException` until 10.0.1, then `JsonReaderException` |
| camel-cased names with spaces | `"urL VALUE"` until 11.0.1, then `"url VALUE"`; `"a b"` until 8.x, then `"a B"` |
| `-0.0`, read | negative zero except in 13.0.2 |
| decimal overflow | throws, except a wrong value in 10.x and 11.x |

What did not change for the inputs tried: camel casing of names without spaces
(`URLValue`, `ID`, `IOStream`, `SKUCode`, `ID2`), plain `DateTime` kinds from ISO
strings, `Uri` values in a `JValue`, empty strings read into `byte[]`,
`JValue` of a null string, double parsing precision, `Value<int>` of an array.

Four behaviors changed and changed back. Under `since`, a project on 7.0.1 would
be warned that decimals lose their trailing zeros, which they do not in 13.x.

## Decision

A rule describes one behavior as ordered, contiguous version segments with
labels (`[, 10.0.1) keep`, `[10.0.1, 11.0.1) drop`, `[11.0.1, 13.0.2) keep`, ...).
It fires when the label of the version in use differs from the label of the
target. The differential test (ADR 0066) runs the rule's example under every
release from 7.0.1 to the newest 13.x and requires equal outputs exactly where
the labels are equal, so a wrong boundary fails the test as surely as a wrong
claim. Experiments that showed no change are not rules. Behaviors that depend
on data rather than code (comments inside parsed JSON, dictionary keys) are
reported once per project (OFR3719), not at a site.

## Alternatives considered

- **`since` plus an `until` for reverted changes.** It describes one excursion.
  The `0.00` decimal has two, and labels say which outputs are expected to be
  equal.
- **Measure only 7.0.1 against 13.x.** That is the user who prompted this, but
  projects sit at every version in between. A project on 10.0.3 does lose
  trailing zeros today and gets them back on 13.x.

## Consequences

- The rule pack is a set of measured facts that can be remeasured when 13.x
  moves.
- The test suite restores 17 Newtonsoft.Json releases. They are small, and
  nuget.org is already needed.
- A behavior that changes between two releases not in the test's list cannot
  be described. The list is every release from 7.0.1 on, so that cannot happen
  in the supported range.
