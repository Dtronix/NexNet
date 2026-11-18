# NexNet LLM Release Procedure

Steps for an agent to release NexNet vX.Y.Z. Follow them when the user asks for a release. Architecture reference:
[llm-dev.md](llm-dev.md).

A pushed `vX.Y.Z` tag builds, creates the GitHub release and publishes four NuGet packages (NexNet, NexNet.Asp,
NexNet.Generator, NexNet.Quic). A published NuGet version can't be replaced.

**Gate:** run steps 1-7 without stopping. Stop at the end of step 7 and wait for the user's go-ahead before pushing
anything (step 8). Stop sooner only when a step below says to.

In the commands, `PREV` is the last release tag (`git tag --sort=-v:refname | head -1`) and `<scratch>` is the
session's scratchpad directory.

---

## 1. Pre-flight

Check all of these before changing anything. If one fails, report it and stop.

```
git branch --show-current                  # master
git fetch origin
git status -sb                             # "## master...origin/master" with no ahead/behind
git status --short                         # no tracked changes (untracked .claude/ is fine)
git tag -l vX.Y.Z                          # empty
git ls-remote --tags origin vX.Y.Z         # empty
gh run list --branch master --limit 3      # latest "Build, Pack & Publish" on master: success
```

## 2. Version

- If a `docs/articles/releases/release-notes-vX.Y.Z.md` page is marked `*Unreleased*`, release that version.
- Otherwise, propose a version from the changes since `PREV` and ask the user to confirm it:
  - **minor** (`0.Y+1.0`) for a new public API or a behaviour change
  - **patch** (`0.Y.Z+1`) for fixes only

## 3. Release notes

### Find every change

```
git log PREV..HEAD --oneline
gh pr view N --json title,body -q '.title + "\n" + .body'    # for each PR number in the log
```

Squash-merge subjects are often cut short, so read the PR body for the detail. Check that each PR is on the notes
page and add any that are missing. A PR merged without updating the page is the usual gap: for v0.20.0, #93, #102 and
#103 were missing. CI-only and docs-only PRs still get an entry, under Build or Documentation.

### Page layout

`docs/articles/releases/release-notes-vX.Y.Z.md`, with sections in this order. Leave out any section that would be
empty.

```markdown
# NexNet vX.Y.Z

*Released YYYY-MM-DD*

## Highlights

- **Short title.** One or two sentences ([#N](https://github.com/Dtronix/NexNet/pull/N)).

---

## Breaking Changes        <!-- or "Behaviour Changes" when the public API is unchanged -->
## New Features
## Fixes
## Performance
## Diagnostics
## Documentation
## Build
```

- Highlights: 3-5 bullets, each one restating a change from a section below.
- End every entry with its PR link. Use relative links to other docs pages (`../channels.md#relaying`); the release
  script rewrites them to the docs site.
- When the public API is unchanged, say so in one line, naming any new interface members that have default
  implementations.
- Performance: give the measured numbers from the PR, with the machine and the conditions.
- Writing style: state the fact and what follows from it. Don't rank or dramatize changes.
- Use today's date in the `*Released ...*` line.

### Index and table of contents

- `docs/articles/releases/index.md`: add a row at the top of the table: `| [vX.Y.Z](release-notes-vX.Y.Z.md) | YYYY-MM-DD | <one-line summary> |`.
- `docs/articles/releases/toc.yml`: add the entry below `Overview` if it isn't there yet.

### Check the release body

```
python .github/scripts/release-notes.py vX.Y.Z <scratch>/body.md
```

The script must succeed. Its output must start at `## Highlights` (the title and Released lines are removed).

## 4. Version bump

Set `<Version>X.Y.Z</Version>` in:

- `src/NexNet/NexNet.csproj`
- `src/NexNet.Asp/NexNet.Asp.csproj`
- `src/NexNet.Generator/NexNet.Generator.csproj`
- `src/NexNet.Quic/NexNet.Quic.csproj`

Then grep the tracked sources for the old version. Matches under `bin/`, `obj/` or `.claude/worktrees/` are build
output; ignore them.

```
git grep -n "OLD.VERSION" -- src ':!docs/articles/releases'
```

## 5. Build and test

```
dotnet build src -c Release                                   # 0 warnings, 0 errors
dotnet test src/NexNet.Serialization.Tests -c Release --no-build
dotnet test src/NexNet.Generator.Tests -c Release --no-build
dotnet test src/NexNet.IntegrationTests -c Release --no-build # about 4 min; use a 10 min timeout
```

Record each suite's pass count for step 7. If a test fails, rerun only that test to tell a flaky test from a real
failure (NUnit suites: `--filter "FullyQualifiedName~Name"`; TUnit suites: `--treenode-filter "/*/*/*/Name"`, or
`"/*/*/ClassName/*"` for a class; see `llm-dev.md`). Report the result and stop; don't release while a test fails.

## 6. Commit

Stage only the files the release changed; never `git add -A` (`.claude/` is untracked):

```
git add docs/articles/releases/release-notes-vX.Y.Z.md docs/articles/releases/index.md \
        docs/articles/releases/toc.yml src/NexNet/NexNet.csproj src/NexNet.Asp/NexNet.Asp.csproj \
        src/NexNet.Generator/NexNet.Generator.csproj src/NexNet.Quic/NexNet.Quic.csproj
git commit -F <scratch>/commit.txt          # subject: "Release X.Y.Z"; written with the Write tool
git show --numstat --format= HEAD
```

Each `.csproj` must show `1 1`. More than that means the line endings changed; fix the file before continuing.
"LF will be replaced by CRLF" warnings alone are fine.

## 7. Confirm with the user

Show the user:

- the version and the commit hash
- the release notes diff, or the Highlights and a list of the sections
- the build result and each suite's pass count

Then wait. Don't push until the user says to go ahead.

## 8. Push and tag

```
git push origin master
git tag -a vX.Y.Z -m "NexNet vX.Y.Z"
git push origin vX.Y.Z
```

Push master first, so the tagged commit is on master when the tag build starts.

## 9. Verify

```
gh run list --limit 4                       # "Build, Pack & Publish" for the tag; also master and "Deploy Documentation"
gh run watch <tag run id> --exit-status     # about 5 min
gh release view vX.Y.Z                      # title "NexNet vX.Y.Z Released"; body matches the notes page
```

NuGet: each package's version list must include X.Y.Z. A package can take a few minutes to show up after the run
finishes.

```
curl -s https://api.nuget.org/v3-flatcontainer/nexnet/index.json
curl -s https://api.nuget.org/v3-flatcontainer/nexnet.asp/index.json
curl -s https://api.nuget.org/v3-flatcontainer/nexnet.generator/index.json
curl -s https://api.nuget.org/v3-flatcontainer/nexnet.quic/index.json
```

Report: the run result, the release URL, and the NuGet version for each package. Also report the master build and
the docs deploy runs.

## 10. If the tag build fails

Report the failed step and the log excerpt (`gh run view <id> --log-failed`). What to do next depends on whether any
package reached NuGet. Check the `Push` step's log and the NuGet version lists from step 9.

**No package published** (failed in build, tests, release notes script or creating the release):

1. Fix the cause on master: commit, then push after the user confirms.
2. If a GitHub release was created, delete it: `gh release delete vX.Y.Z`.
3. With the user's confirmation, move the tag to the fixed commit:
   ```
   git tag -d vX.Y.Z
   git push origin :refs/tags/vX.Y.Z
   git tag -a vX.Y.Z -m "NexNet vX.Y.Z"
   git push origin vX.Y.Z
   ```
4. Go back to step 9.

**Any package published:** never move or delete the tag. NuGet can't replace a published version, and
`--skip-duplicate` would skip it silently. Ship the fix as the next patch version, starting again from step 1. The
notes page for the patch says what the failed release left out.
