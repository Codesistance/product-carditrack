---
name: test-catalogue
description: Sweep the mobile user test catalogue (#567) against what has merged since the last sweep — add tests for newly shipped app-observable behaviour, update tests whose assertions the code has moved past, trim assertions a tester cannot make from the phone, and reconcile the indexes. Use when asked to update the test tickets, add tests for recent work, check the catalogue for drift, or when the daily catalogue Routine fires.
---

# Sweeping the mobile test catalogue

[#567](https://github.com/Codesistance/product-carditrack/issues/567) is the mobile user
test catalogue: one issue per test, grouped under an area parent, executed **by hand** by a
tester holding a phone. There is no UI automation, so nothing else catches a test that has
drifted from what the app actually does. A tester following a stale assertion reports a
correct screen as a bug, or passes a screen that is wrong.

This skill keeps the catalogue level with `main`.

## The governing rule

**The line is where the assertion is read, not where the data comes from** (#567's own
words). Judge a change by whether a caregiver holding the phone can see it — never by which
files it touched.

Both halves of that bite:

- A **backend-only** PR whose composed copy the app renders **is in scope.** #1291 changed
  no mobile file and changed the alert card's left label from a date to "Longest still
  stretch". That is an on-screen assertion.
- A PR touching **many mobile files** whose result is invisible **is not.** #1283 added
  Datadog tags naming the guard that withheld a chat reply, and says so itself: "No
  behaviour change: the caregiver sees exactly what they saw before."

Seeding an account, sending a test push or forcing a 500 are **preconditions** and stay in
scope. An assertion that can only be read in a database row, a proxy capture, a Datadog
query or the on-device log file does not.

## 1. Establish the window

Read the sweep marker in #567's body:

```
<!-- sweep: last=YYYY-MM-DD main=<short-sha> prs=<lo>..<hi> -->
```

That marker is the only trustworthy record of what has been covered. **Do not infer the
window from the creation dates of recent test issues** — that breaks the moment anyone adds
a test by hand, and it silently under- or over-scans.

```
git fetch origin main
```

Then list PRs merged after the marker's date. If the marker is missing (first run, or
someone removed it), fall back to the newest test issue's creation date, say so in the
notification, and write a marker at the end.

## 2. Classify every PR in the window

Three outcomes. Record the verdict for each PR; a PR you skipped silently is
indistinguishable from one you missed.

| Verdict | Action |
|---|---|
| App-observable | Add a new test, or update the test it contradicts |
| Backend-only, mobile UI stated as a later PR | **No test.** Add to the pending-UI checklist (§5) |
| No caregiver-visible change | Skip, and note the reason in the run summary |

A PR body that says "the mobile screen follows in a separate PR", "backend half", or "UI
only" is telling you which row it belongs in. Believe it, then confirm against the diff.

Writing a test for behaviour that has no UI yet produces a ticket the tester must mark
Blocked — the precise failure this catalogue exists to avoid.

## 3. Update what has drifted

Drift is the highest-value find, and the easiest to miss: the test still reads plausibly,
so nothing flags it. Look for a shipped change that contradicts an existing **Expected**.

Worked example. CHAT-09 asserted a cycler of three fixed lines rotating every 3.6 s.
#1262 replaced it with real streamed steps, #1265 numbered them, #1266 added a progress
bar. A tester running the old test would have filed a bug against correct behaviour.

When you update a test, append a dated note saying what shipped and why the old assertion
would now fail:

```markdown
---

**Changed YYYY-MM-DD.** <what the test used to assert>. #NNNN <what shipped>, so the old
assertion would now fail against correct behaviour.
```

Rename the test's title when its subject has moved on ("Waiting copy" →
"Progress while an answer is written"), keep the ID, and **fix the row in the area parent's
index table too** — the table carries the old title until you do.

## 4. Trim assertions a tester cannot make

Scan every open test body for out-of-scope assertions: `Datadog`, `proxy`, `traceparent`,
`header`, `database`, `log file`, `rooted`, `API client`, `span`, `trace`.

Prefer **trimming the clause** to closing the test. SET-09 had two unreachable assertions
(no `traceparent` header reaching the API; a crash "reaching the log") inside an otherwise
fully runnable test. Cut those two, point them at #126, keep the rest:

```markdown
**Trimmed YYYY-MM-DD to keep this runnable from the phone.** <clause> — <why a tester
cannot read it>. Neither behaviour is abandoned; both belong to the non-UI suite in #126.
```

## 5. The pending-UI checklist

Keep a checklist on #567 of backend-only work whose mobile screen has not shipped. Check it
**every run**: when a PR lands the UI for a parked item, that is when its tests get written,
and the item is ticked with the PR that closed it.

Without this, backend work whose UI arrives weeks later is remembered nowhere.

## 6. Closing

Closing is authorised, and every closure must be **evidenced and reversible**:

- Comment on the issue with the reason and the PR or commit that invalidated it, then close
  as `not_planned` (a test that never passed was not "completed").
- Remove its row from the area parent's index and detach the sub-issue.
- **List every close in the run summary.** A wrong close removes a tester's coverage
  silently; the summary is what makes it visible the next morning.

Close for: an exact duplicate of another test's ID or subject, or a test for a feature
demonstrably removed from the code. Where it is a judgement call, prefer §3 or §4 — a
reworded test keeps its coverage, a closed one does not. Today's equivalent sweep found
nothing worth closing, which is a normal result, not a failure to look.

## 7. Reconcile, or the counts lie

Every new or closed test touches four places. Miss one and the catalogue misreports itself:

1. The test issue's own labels — `testing` **and** `mobile`.
2. The area parent: a row in its index table **and** a real GitHub sub-issue link.
3. The parent's "Each of the N tests below" line.
4. #567: the area's row count, the "**A grandchild per test** — N of them" line, and the
   "**N tests across M areas.**" footer.

**Check the arithmetic against itself:** the per-area deltas must sum to the number of tests
added minus those closed. That cross-check is what catches a slip — an earlier sweep wrote
"9 tests" for an area where removing two from ten left eight, and only the reconciliation
found it.

## Mechanics that will cost you an hour each if you rediscover them

- **Reach GitHub through `curl` with `$GH_TOKEN`, not the MCP tools.** A Routine-fired
  session may carry no `mcp__*` tools at all (a Routine created without connectors fires
  without them), so a procedure built on `mcp__github__*` breaks on a schedule while working
  by hand. Every REST call this skill needs works as
  `curl -H "Authorization: bearer $GH_TOKEN" https://api.github.com/repos/...`. Check
  `$GH_TOKEN` is set at the start of a run and say so in the summary if it is not, rather
  than failing silently.
- **The GitHub search API is blocked in these sessions** ("sessions are bound to their
  configured repositories"). Paginate `repos/{owner}/{repo}/issues?labels=testing&state=all`
  and filter locally. The list endpoint returns bodies, so one pass gets everything.
- **`state:open` is not search syntax** — it is `is:open`. A query using the wrong one
  returns `total_count: null`, which reads exactly like "no findings". Never accept a
  zero-result scan without a control term you know matches.
- **Compute the next test ID over `state=all`.** Closed tests still hold their IDs.
- **Keep index tables contiguous.** A blank line between rows ends the markdown table and
  the rows after it render as plain text.
- **Use quoted heredocs** (`<<'EOF'`) for test bodies. Unquoted, the shell executes
  backticks: `` `token_expired` `` becomes empty parens.
- **PATCH bodies from a file** via `jq -n --rawfile`, not an inline shell string.
- **Never quote a wearer health value** into an issue. Name the field, redact the value.

## Boundaries

This skill maintains the catalogue. It does not decide whether a feature should exist
([product-manager](../product-manager/SKILL.md)), triage the backlog
([issue-triage](../issue-triage/SKILL.md)), or ship code
([last-mile](../last-mile/SKILL.md)). It writes no repository code and opens no PR — its
whole output is GitHub issues, so it needs no branch and no merge.

It does not execute tests. Nobody can from a cloud session: the catalogue is run by hand on
a real handset, and mobile UI work is verified on the Android emulator per
[CLAUDE.md](../../../CLAUDE.md) and
[docs/technical/android_emulator_runbook.md](../../../docs/technical/android_emulator_runbook.md).
