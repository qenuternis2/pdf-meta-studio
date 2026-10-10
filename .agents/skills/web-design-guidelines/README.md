# Vendored Vercel Web Design Guidelines

This directory contains the complete `skills/web-design-guidelines/` directory from
[Vercel Agent Skills](https://github.com/vercel-labs/agent-skills), copied as regular
repository files. `SKILL.md` is preserved byte for byte. At the pinned revision,
the upstream skill contains only `SKILL.md`; there are no auxiliary files.
This README is local provenance and update documentation.

## Source

- Repository: https://github.com/vercel-labs/agent-skills
- Requested directory: https://github.com/vercel-labs/agent-skills/tree/main/skills/web-design-guidelines
- Upstream commit: `063bee94c3f4df8453406c830b0a7df0f2860278`
- Pinned directory: https://github.com/vercel-labs/agent-skills/tree/063bee94c3f4df8453406c830b0a7df0f2860278/skills/web-design-guidelines
- `SKILL.md` SHA-256: `f4647ca866a3accf763777f83e7682954f0187cd6bea7eea0399796652414e8f`

## Live rules and network access

The skill fetches current rules before each review from:

https://raw.githubusercontent.com/vercel-labs/web-interface-guidelines/main/command.md

This URL was checked on 2026-10-10 with TLS verification enabled: HTTP 200,
8,055 bytes. This is an availability check, not an interface audit. The live rules
can change independently of the pinned skill; always fetch them again for a review.
Use WebFetch when available, or an HTTPS retrieval tool such as `curl` with normal
TLS verification. If retrieval fails, report that limitation instead of claiming
compliance with current rules.

Restricted environments need `raw.githubusercontent.com` for reviews and
`github.com` for retrieving or updating the skill. No Vercel deployment, account,
package installation, application dependency or symlink is required.

## Reproduce or update

From the repository root, obtain a temporary checkout at the desired upstream
commit. The following reproduces the current import:

```bash
skill_source_dir="$(mktemp -d)"
git clone --no-checkout https://github.com/vercel-labs/agent-skills.git "$skill_source_dir"
git -C "$skill_source_dir" checkout --detach 063bee94c3f4df8453406c830b0a7df0f2860278
mkdir -p .agents/skills/web-design-guidelines
cp -R "$skill_source_dir/skills/web-design-guidelines/." .agents/skills/web-design-guidelines/
git -C "$skill_source_dir" ls-tree -r HEAD skills/web-design-guidelines
sha256sum .agents/skills/web-design-guidelines/SKILL.md
git diff -- .agents/skills/web-design-guidelines
```

For an update, replace the checkout SHA with the reviewed upstream commit, copy
the entire skill directory including any new auxiliary files, and inspect the
upstream tree for deleted files. Remove only confirmed obsolete upstream files;
preserve this local README. Reject symlinks and preserve the upstream instructions
and file modes. Update the source SHA, pinned URL, file checksum and availability
check above. Validate the YAML front matter (`name`, `description`, `metadata`),
compare all imported files to the pinned source, inspect the diff, and commit the
update through a PR. Do not replace the live rules URL with a stale local snapshot.
