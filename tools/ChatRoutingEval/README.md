# Chat Routing Eval

The labelling instrument for **rollout step 1** of
[`docs/technical/member_chat_routing.md`](../../docs/technical/member_chat_routing.md) — the step
that asks for the eval set to be *"labelled blind by two people"* before the ladder is trusted.

Not in `CardiTrack.sln` and not built by CI, the same convention `tools/AiSplitEvaluator` follows.
Unlike that tool it needs **no database, no model endpoint and no network** — it shuffles a fixture
into blind labelling sheets and scores the sheets people hand back.

## Read this before you read any number it prints

The bundled fixture (`cases.json`) is the §11 **seed** table, extracted from the design document
rather than retyped. It is 35 questions written by **one author**, phrasing failures that were
already on record. It is explicitly **not** the eval set step 1 asks for, on two counts:

- it was not labelled blind — the author knew the intended answer while writing each question;
- the phrasings are not real caregiver messages.

This is the weakness the product-manager review already logged in §13: *"Eval set validates our
taxonomy against itself, not against caregivers."* Running this tool on the seed measures whether
the taxonomy is **teachable from its own definitions** — worth knowing, and a genuine dry run of
the process — but it is not evidence about how caregivers write. The tool prints that caveat above
every report so a number never travels without it.

The decisive version needs real phrasings, and those arrive from step 4's `MemberChatTurn.Workflow`
stamp once it has been live long enough to collect them. Which means step 1 as written in §10
("before any code") cannot fully precede step 4 — the seed is writable now, the eval set proper is
not. That ordering conflict is recorded in the document's status line.

## Why the labels come from the catalogue

The vocabulary is built from `ChatWorkflowCatalogue.All`, not from a list typed into this tool, so a
new workflow cannot reach a labelling sheet unnamed. Two deliberate differences from what the
routing prompt renders:

- **`investigation` is offered**, though `IsImplemented: false` keeps it out of `Routable`. The eval
  set is what decides whether that handler gets built, so a labeller has to be able to choose it.
- **`Steer` is not offered.** It is retired — it exists so already-stamped turns keep their meaning
  (see `MemberChatWorkflow.Steer`), and offering it would collect labels nothing routes.

`clarify` **is** offered, meaning "genuinely ambiguous, the app should ask". A labeller reaching for
it is real signal about the ladder even though the router never returns it.

## Running

```bash
# 1. Generate one answer-free, independently shuffled sheet per labeller.
dotnet run --project tools/ChatRoutingEval -- sheet \
    --labeller alice --labeller bob --out ./labelling

# 2. Both people fill in the `label` column. Separately. Then:
dotnet run --project tools/ChatRoutingEval -- score \
    --labels ./labelling/labels-alice.csv \
    --labels ./labelling/labels-bob.csv
```

`sheet` also writes `LABELLING.md` into the output directory — the vocabulary, each label's meaning
taken from the catalogue's own purpose line, and the rules. Hand that to the labellers with their
sheet; it is the only briefing they need.

Options:

- `--out <dir>` — where sheets land (default: current directory).
- `--seed <int>` — changes the shuffle. Same `(labeller, seed)` always produces the same order, on
  any machine, so a re-issued sheet lines up with one already filled in.
- `--cases <file>` — score or shuffle a different fixture. This is how the real eval set gets used
  once it exists: same instrument, different cases.
- `score` accepts more than two `--labels` files and reports every pair.

Sheets are ordinary CSV and open in Excel; CRLF comes back fine.

## What `score` reports

1. **Inter-labeller agreement**, against the ~20 % disagreement gate. Above it, §10's claim is that
   the ladder is wrong and no router will fix it.
2. **Every disagreement, classified.** §11 draws the distinction and the tool applies it:
   - `tolerable` — the `analysis`↔`inference` superset boundary, which §2's tie-break absorbs.
   - `SERIOUS` — one side said `advise`. That is the boundary where a reply starts recommending
     things, and §11 says confusion there is not tolerable.
   - `other` — everything else.
3. **A confusion matrix** per pair, not an accuracy figure — §11 is explicit that the matrix is the
   artefact and a single percentage hides the only thing worth seeing.
4. **Each labeller against the seed key.** Disagreeing with the key is not automatically wrong: the
   key is one author's expectation.
5. **Cases where every labeller agreed and all of them departed from the key.** The strongest signal
   the fixture can produce, because it points at the key or the ladder rather than at a person.

## What it will not do

- Decide anything. It produces the numbers a human reads.
- Call a model, or route a message. Nothing here touches `MemberChatService`.
- Read or write a database, or leave the machine it runs on.
- Score one sheet against itself — `score` refuses a single `--labels` file, because agreement
  between one person and themselves is not the measurement step 1 asks for.

## Regenerating the fixture

`cases.json` was extracted from §11's table rather than retyped, and should be regenerated the same
way if that table changes, so the two cannot drift. Each case carries the `guards` column from the
document, which is what makes a key disagreement legible: it says what the case was written to
catch.
