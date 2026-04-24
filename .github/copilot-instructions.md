# Copilot Instructions

## Read this first

Before answering ANY question or doing ANY work in this repository, read
the file `AGENT_CONTEXT.md` at the repo root. It is the persistent memory
across Copilot sessions and contains: project facts, user preferences,
architectural pitfalls, in-progress workstreams, recent commits, and the
"how to update this file" maintenance protocol.

UPDATE `AGENT_CONTEXT.md` during your session whenever any of these happen:
- a user preference or convention is established or changed
- a non-obvious production behavior is discovered
- a benchmark is run that produces a measurable design-relevant number
- a workstream completes, blocks, or pivots
- a commit lands that future sessions need to know about
- a "do not do this" lesson is learned

The file's last section is a dated maintenance log; add one line per
session that touches it.

## Terminal command execution

When running PowerShell commands via the terminal tool, always emit the
command on a single physical line with no line breaks. Chain multiple
commands with `;` on the same line. Length and readability are not
concerns; correctness and single-line execution are. This rule is
absolute -- no multi-line here-strings, no backtick continuations, no
splitting long pipelines across lines.

## Git commit policy

When code changes reach a logical stopping point in a git repository,
commit the modifications immediately with a meaningful message. Rules:

- Use `git commit -m "..."` with one or more `-m` flags, one per
  paragraph. Never write the commit message to a file. Never use
  `git commit -F <file>`.
- Never include emoji or non-ASCII characters in the commit message.
- Escape `"` and `\` properly so the git invocation parses cleanly.
- Run the entire `git add` + `git commit` sequence on a single line,
  using `;` to chain them. Length and readability are not concerns.
- **Never run `git push` under any circumstances.** Pushing is a
  manual step the user performs. Leave commits local; do not invoke
  `git push`, `git push origin <branch>`, or any equivalent.

## Documentation trust policy

Treat every Markdown documentation file in this repo (including
`README.md` if one exists, `AGENT_CONTEXT.md`, anything under `docs/`,
any `SETUP.md` / `USER_GUIDE.md` / `CONTRIBUTING.md` / similar) as
**stale until proven current**. Setup instructions, build commands,
feature descriptions, and architectural claims in those files may be
outdated or incomplete.

Before relying on a statement from any doc file as ground truth:

- Verify it against the actual code, project files, configuration, or
  a fresh build/test run. Do not paraphrase a doc claim into your
  response without that verification.
- If the statement is wrong or out of date, update the doc file in
  the same commit as the code change that reveals the discrepancy.
  Do not leave a known-wrong doc file in place.

When committing code that changes anything user-facing, build-related,
or architecturally significant, audit the affected doc files in the
same commit and update or remove stale content. Documentation that is
not maintained alongside the code becomes a liability, not an asset.
