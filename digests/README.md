# `digests/`

One file per run of the digest routine (`.claude/commands/digest.md`), named
`YYYY-MM-DD.json` for the run's own date (Europe/London).

These files do two jobs:

1. **They are the Slack payload.** A push to `main` that adds one triggers
   `.github/workflows/post-digest.yml`, which posts it.
2. **They are the dedup state.** There is no separate log. Sessions share no
   filesystem — every run starts in a fresh container with only what is
   committed — so this directory is the routine's entire memory. A run that does
   not commit re-reports the same news the next morning.

## Shape

```json
{
  "date": "2026-09-18",
  "summary": "*CardiTrack digest — 2026-09-18* · 2 items · 1 CRITICAL, 1 FYI",
  "empty_reason": null,
  "items": [
    {
      "slug": "medgemma-licence-change",
      "title": "Health AI Developer Foundations terms updated",
      "url": "https://developers.google.com/health-ai-developer-foundations/terms",
      "severity": "CRITICAL",
      "category": "models",
      "brief": "research/queue/2026-09-18-medgemma-licence-change.md",
      "text": "*CRITICAL* — Health AI Developer Foundations terms updated\n…"
    }
  ]
}
```

## Fields

| Field | Notes |
|---|---|
| `date` | `YYYY-MM-DD`, Europe/London. Matches the filename. |
| `summary` | The parent Slack message — a one-line severity roll-up. Required. |
| `empty_reason` | Short string when `items` is empty and it is worth saying why (e.g. `"nothing cleared the bar"`), otherwise absent or `null`. Distinguishes a quiet morning from a run that failed before it published. |
| `items` | Array, possibly empty. One entry per published item. |

### `items[]`

| Field | Notes |
|---|---|
| `slug` | Kebab-case; matches the brief filename after the date. |
| `title` | One line, as published. Used for the issue button's title. |
| `url` | **The dedup key.** Compared exactly, so store the canonical primary source — not a redirect, not an aggregator, and without tracking parameters. An item with no URL does not qualify for publication in the first place. |
| `severity` | `CRITICAL` \| `HIGH` \| `FYI`. `CRITICAL` also sets `reply_broadcast`, surfacing the item in the main channel. |
| `category` | One of `models`, `dependencies`, `regulation`, `grants`, `devices`, `competition` — the section of `digest.md` that surfaced it. |
| `brief` | Repo-relative path to the research brief under `research/queue/`. |
| `text` | The threaded reply, Slack mrkdwn, ending with the Claude Code pickup line. |

No `blocks` key: `scripts/post-digest.sh` builds the blocks, including the
issue-button URL, which it derives from the fields above and URL-encodes. An
unencoded space or `#` breaks a button silently, so that encoding lives in one
place rather than in the routine's output.

## Rules

- **Append only, in the sense that files are never rewritten.** A digest that
  has been pushed has been posted. The workflow only posts files a push *adds*
  (`--diff-filter=A`), so editing one after the fact changes the dedup record
  without changing what Slack said — the two would silently disagree.
- **Match on `url`.** A URL already present is not republished, unless the
  underlying item has materially changed — in which case publish a *new* entry
  in a *new* day's file, same `url`, with a title describing the change rather
  than the original news. Duplicate URLs across days are expected and meaningful.
- **Commit the digest and its briefs together**, so the record and the briefs
  can never disagree.

## History

Runs before 2026-09-13 were recorded in `research/log.json` under a design where
the routine posted to Slack itself. That file was migrated into this directory
(2026-09-04, 2026-09-09) and removed; the `text` fields for those two days were
reconstructed from the log's titles and URLs, since the original message text was
not retained. Posting moved to a GitHub Actions runner so the routine, which
ingests untrusted web content, holds no Slack credential.
