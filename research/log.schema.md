# `research/log.json`

Dedup state for the digest routine (`.claude/commands/digest.md`).

Sessions share no filesystem: every run starts in a fresh container with only
what is committed to the repo. This file is therefore the routine's entire
memory. If a run does not commit it, the next morning re-reports the same news.

## Shape

```json
{
  "runs": [
    {
      "date": "2026-09-01",
      "slack_ts": "1756713600.000100",
      "item_count": 2,
      "empty_reason": null
    }
  ],
  "items": [
    {
      "date": "2026-09-01",
      "slug": "medgemma-licence-change",
      "title": "Health AI Developer Foundations terms updated",
      "url": "https://developers.google.com/health-ai-developer-foundations/terms",
      "severity": "CRITICAL",
      "category": "models",
      "brief": "research/queue/2026-09-01-medgemma-licence-change.md"
    }
  ]
}
```

## Fields

### `runs[]` — one entry per session, appended even when nothing was published

| Field | Notes |
|---|---|
| `date` | `YYYY-MM-DD`, Europe/London, the run's own date. |
| `slack_ts` | The parent message ts, as written to `run-ts.txt` by `scripts/slack-post.sh`. This is what threads the replies, and what lets a later run link back to a morning. `null` when the run could not attempt the Slack post at all (e.g. `SLACK_CHANNEL`/`SLACK_BOT_TOKEN` unset in the environment) — distinct from a string ts, which means the post was actually sent. |
| `item_count` | Number of `items[]` entries added by this run. `0` is legitimate. |
| `empty_reason` | Short string when `item_count` is 0 and it is worth saying why (e.g. `"nothing cleared the bar"`), otherwise `null`. Distinguishes a quiet morning from a run that failed before it published. |

### `items[]` — one entry per published item, append-only

| Field | Notes |
|---|---|
| `date` | Date first reported. It does not change if the item is later re-reported. |
| `slug` | Kebab-case; matches the brief filename after the date. |
| `title` | One line, as published. |
| `url` | **The dedup key.** Compared exactly, so store the canonical primary source — not a redirect, not an aggregator, and without tracking parameters. An item with no URL does not qualify for publication in the first place. |
| `severity` | `CRITICAL` \| `HIGH` \| `FYI`, as published. |
| `category` | One of `models`, `dependencies`, `regulation`, `grants`, `devices`, `competition` — the section of `digest.md` that surfaced it. |
| `brief` | Repo-relative path to the research brief. |

## Rules

- **Append only.** Never rewrite or drop an entry; the file is the audit trail
  of what was already said.
- **Match on `url`.** A URL already in `items[]` is not republished, unless the
  underlying item has materially changed — in which case append a *new* entry
  with the same `url`, a new `date`, and a title describing the change rather
  than the original news. Duplicate URLs are therefore expected and meaningful.
- **Commit in the same commit as the briefs** the run wrote under
  `research/queue/`, so the log and the briefs can never disagree.
