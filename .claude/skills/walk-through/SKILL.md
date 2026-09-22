---
name: walk-through
description: Reconstruct the full execution path that produced an observation — a log line, a Datadog URL, an alert, a trace, a wrong value on a screen, or "how does X actually run" — as a step ladder from trigger to observation, where every step carries its logic, its branches, a dummy input/output pair, and a re-runnable Datadog query that proves it happened. Ends in a published artifact. Use when asked to walk through, trace through, explain the logic tree or journey behind something, or to show each step of how a result came about.
---

# Walk-through

A walk-through answers **"how did this actually get produced?"** — not "what is broken."
The output is a ladder: trigger at the top, the observation at the bottom, every rung
carrying the code that ran, the branches it chose between, a worked example with made-up
numbers, and the query that proves the rung was real.

Three things make it a walk-through rather than a code tour:

1. **Every step is evidenced.** A step with no supporting query is a claim, and it must be
   labelled as one. The evidence must be a query the reader can paste and re-run.
2. **Every branch is named, not just the taken one.** The five ways out of a loop matter as
   much as the one that fired — that is where the next bug lives.
3. **Dummy data throughout.** The reader learns the shape by following one synthetic member
   end to end. Real values are read during the investigation and never published.

This skill is the *method*. Retrieval belongs to `datadog-pup` — user-level, not in this
repo: curl against the Datadog REST API, `DD_API_KEY`/`DD_APP_KEY`, no CLI. What
CardiTrack's telemetry means belongs to
[`carditrack-trace-triage`](../carditrack-trace-triage/SKILL.md) — service names, the
`@otel.trace_id` facet, what a missing trace means, which log levels ship in which
environment. Load both; do not restate their contents here.

## Scope check before you start

A walk-through is expensive. It is right when:

- the observation is a *symptom* whose cause is several hops upstream;
- someone needs to understand a pipeline they did not write;
- a behaviour is intermittent and you need to see which branch varies;
- a fix is being designed and the blast radius matters.

It is the wrong tool for a stack trace that names its own cause, a one-line config
question, or a failing test you can read in a minute. Say so and answer directly instead.

## Procedure

### 1. Anchor the observation

Pin down exactly one occurrence before generalising.

- A Datadog URL carries its own window: `from_ts`/`to_ts` are **epoch milliseconds**, and
  `query=` is URL-encoded. Decode both and state the window in UTC in the write-up —
  `live=true` means the window slides, so quote the frozen one you actually queried.
- A bare id may be a trace id, a W3C traceparent, or an `ErrorResponse.TraceId`. See
  `carditrack-trace-triage` before assuming.
- Get the *whole* log event, not the rendered message. `attributes.attributes.otel.scope.name`
  names the emitting class, `version` names the deployed build, `deployment.environment`
  separates dev from prod. That scope name is the fastest route into the source there is.

### 2. Find the emitting statement in the **deployed** source

Search for the message *template*, not the rendered text — the rendered line has ids
substituted into it and will not match.

```bash
git fetch origin --quiet
git grep -n "nothing raised" origin/main -- src/
```

> **Read `origin/main`, not the working tree.** A local checkout drifts, and a checkout
> sitting on a feature branch may not contain the code that is running at all. Pull the
> file out with `git show origin/main:<path> > "$SD/<name>.cs"` and read that copy. If the
> log's `version` tag is older than `origin/main`, say so — you are reading a superset.

### 3. Walk *up* to the trigger

From the emitting method, climb until you reach something that starts on its own: an HTTP
endpoint, a Cloud Scheduler cron, a queue consumer, a hosted service, an app tap. Do not
stop at the first caller. For pipeline work the top of the ladder is usually a
`google_cloud_scheduler_job` in `infrastructure/deployments/cloud_run.tf` plus the `case`
arm it dispatches in `src/Pipeline/CardiTrack.PipelineJobs/Program.cs` — quote the cron
expression and check it against the observed cadence. A mismatch between the two is itself
a finding.

### 4. Walk *down* through every branch

Now descend, and at each fork record: the condition, the source line, where each side goes,
and which side the evidence says was taken. Guard clauses and early returns are steps —
"nothing happened here" is a step someone needs to see.

Watch for these, which recur across this codebase:

- **Preference and enablement gates** that skip work wholesale.
- **Cooldown / dedup / idempotency layers** — and specifically whether a *negative* outcome
  is persisted. If it is not, the work repeats on every pass; that is usually the
  explanation for a line that recurs on a fixed cadence.
- **Fail-closed paths around a model call.** A parse that does not map, a verdict for the
  wrong key, copy a register guard rejects — each is a distinct exit with its own log line.
- **Best-effort tails** (push enqueue, status line, explanations) that log and continue.

### 5. Build the dummy dataset

Invent one synthetic subject and carry it through every step. Pick figures that *just* trip
the rule under study, so the reader can see the threshold working. Show, per step, the input
it receives and the output it emits — the actual shapes: the row, the DTO, the JSON, the
prompt, the parsed response.

> **Synthesise, always.** Member readings, ages, caregiver-reported context and free-text
> answers are health data, and this repo is public. Investigate locally with the real
> payload; publish invented figures and a fake id. Never paste a raw span or log body into
> an artifact, a PR, an issue, or a commit message.

### 6. Show the prompt, if a model is in the path

Reproduce it as assembled: the fixed instruction block, the context block, the payload, and
the reply schema appended by `StructuredOutputSchema.PromptWithSchema`. Then show what came
back. In dev, the clinical-inspection logs carry both in full:

```
service:pipeline-jobs "clinical inspection"
```

Prompt and completion are separate events, so pair them by timestamp — and note that these
lines ship unreliably. If the completion for your occurrence is missing, find a pass whose
pair *did* both land and say which occurrence you are showing.

### 7. Gather evidence per step

Each rung gets a query. Prefer one query answering several rungs to many narrow ones.

- Service-wide over the window shows the whole pass in order and is usually the single most
  valuable query in the walk-through:
  `service:pipeline-jobs` over a four-minute window around one occurrence.
- Then the outcome lines together, over a wider window, to establish frequency:
  `service:pipeline-jobs ("no verdict" OR "not worth attention" OR "pass complete")`.
- Use `logs/analytics/aggregate` for counts. Never fetch rows and count them.

Mark each rung: **observed** (a query returned it), **inferred** (the code says so and
nothing contradicts it), or **unlogged** (see below).

### 8. The extra passes

These are what turns a trace into a walk-through worth publishing.

- **Observability coverage.** List the rungs that emit nothing at all. A step that cannot be
  seen from Datadog is a blind spot, and naming them is often the most actionable output —
  the next incident on this path will stall exactly there.
- **Cadence and cost.** Per step: how often it runs, and what it costs when it does
  (inference calls, DB round-trips, external requests). A recurring warning usually has a
  wasted-work number attached; compute it.
- **Determinism.** Is this every pass or some? Compare occurrences across the window, and
  compare against a sibling subject that took the other branch. "12 of 12 passes, and a
  second member on the same rule succeeded every time" is a far stronger finding than one
  failing sample, and it distinguishes a prompt-sensitivity problem from a flaky one.
- **Fix candidates**, ranked, each naming the step it attaches to and what it would cost.
  Prefer a mechanism the codebase already has over a new one — check whether an existing
  attribute, guard or helper already solves it somewhere else before proposing anything.

### 9. Publish

Output an artifact. A walk-through is a reference someone returns to and forwards, and it
does not survive as terminal scrollback. Build it per the `artifact-design` skill.

Structure that works:

- A header stating the observation, the frozen UTC window, the environment, the deployed
  version, and the verdict in one sentence.
- The ladder. One card per step, numbered, with four panes: **logic** (what runs, with a
  `path:line` reference), **branches** (a table: condition → outcome, taken path marked),
  **dummy in / out**, **evidence** (the query, collapsed, plus what it returned).
- The prompt and the reply side by side where a model is in the path, with the mismatch
  called out.
- Coverage, cadence, determinism and fix candidates as closing sections.

Close the terminal reply with the link and a two-line summary — the fault and the step it
lives on. Do not re-narrate the ladder in chat.

## Reporting rules

- **Observed beats inferred, and the difference is stated.** "No verdict matched the rule"
  is observed. "The model paraphrased the rule name" is observed only if you have the
  completion; otherwise it is inferred and must say so.
- **Quote error strings and log lines exactly.** Paraphrasing a log line makes it
  ungreppable for the next reader.
- **Absence of a log is not absence of execution.** Prod runs all three services at
  `Warning`, and `pipeline-jobs` lines ship unreliably. Check the level before concluding
  the code did not run.
- **Name the blast radius.** Say plainly whether anyone was affected. "The verdict was low,
  so nothing would have been raised either way — but a high verdict would have been dropped
  identically" is the shape: what happened, and what the same defect would cost on a worse
  day.
