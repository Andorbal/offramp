# Skills for AI agents

Guides that an AI agent loads to use Offramp well on someone else's codebase. They are for the
people *migrating* a .NET Framework codebase, not for contributors to Offramp: contributors
follow [`CLAUDE.md`](../CLAUDE.md).

| Skill | What it is for |
|---|---|
| [`offramp-migration`](offramp-migration/SKILL.md) | guiding a team through an incremental migration with Offramp: the phases, the ground rules, how to check Offramp's answers, and which decisions belong to people. It is drawn from the field tests in [`docs/field-tests/`](../docs/field-tests/) |

## Using a skill

The folders follow the Agent Skills layout: a `SKILL.md` with a name and a description, plus
reference files the agent reads when it needs them.

With Claude Code, copy the folder into the repository being migrated, and commit it, so everyone
working on the migration gets the same guide:

```bash
mkdir -p .claude/skills
cp -r path/to/offramp/skills/offramp-migration .claude/skills/
```

To use it only for yourself, copy it into `~/.claude/skills/` instead. Other agents can be
pointed at `SKILL.md` directly.

The skill works best with Offramp's MCP server registered (`claude mcp add offramp -- offramp mcp
serve`). Without it, the agent runs the CLI with `--json`.

## Keeping them current

A skill names commands, options, and diagnostic codes, and repeats numbers from the field tests.
When a pull request changes one of those, or fixes something a skill lists as a known gap, update
the skill in the same pull request.
