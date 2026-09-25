#!/usr/bin/env bash
# Posts a digest file to Slack: one parent summary, then each item as a threaded
# reply carrying a button that opens a prefilled GitHub issue.
#
# Runs on a GitHub Actions runner, never in the digest routine's sandbox. The
# routine ingests untrusted web content, so it is not given a Slack credential;
# it only commits digests/YYYY-MM-DD.json and this script does the posting.
#
# Usage: scripts/post-digest.sh digests/2026-09-18.json
#
# Reads:  $1                the digest file (see digests/README.md for the shape)
#         SLACK_CHANNEL     channel ID, e.g. C0XXXXXXX — not "#name"
#         SLACK_BOT_TOKEN   bot token with chat:write
#         GITHUB_REPOSITORY owner/repo, for the issue button URL

set -euo pipefail

DIGEST="${1:?usage: post-digest.sh <digest-file>}"
[ -r "$DIGEST" ] || { echo "cannot read digest file: $DIGEST" >&2; exit 1; }

# Checked here at top level, not at the point of use: a ${VAR:?} inside a $(...)
# aborts only the subshell, so the guard would print its message and the script
# would carry on and post an empty parent message.
CHANNEL="${SLACK_CHANNEL:?set SLACK_CHANNEL to the channel ID, e.g. C0XXXXXXX}"
: "${SLACK_BOT_TOKEN:?set SLACK_BOT_TOKEN (bot token with chat:write)}"
REPO="${GITHUB_REPOSITORY:?set GITHUB_REPOSITORY to owner/repo}"
API="https://slack.com/api/chat.postMessage"

jq -e 'has("summary") and (.items | type == "array")' "$DIGEST" >/dev/null 2>&1 \
  || { echo "malformed digest: $DIGEST needs .summary and .items[]" >&2; exit 1; }

# `notes` is optional. When present it is an ordered list of sections, each a
# heading and its lines; the parent renders one bulleted block per section so
# the standing context (deadlines, what was checked and found clean, what was
# held back) reads as a list rather than one run-on paragraph.
jq -e '
  (.notes // []) as $n
  | ($n | type == "array")
  and all($n[]; (.heading | type == "string") and (.lines | type == "array")
                and all(.lines[]; type == "string"))
' "$DIGEST" >/dev/null 2>&1 \
  || { echo "malformed digest: .notes must be [{heading, lines[]}]" >&2; exit 1; }

# Slack caps a section block at 3000 characters and rejects the whole message
# past it. Failing here, with the section named, beats a silent lost post.
over=$(jq -r '
  def block: "*" + .heading + "*\n" + (.lines | map("• " + .) | join("\n"));
  [ (.summary | select(length > 3000) | "summary"),
    ((.notes // [])[] | select((block | length) > 3000) | "notes: " + .heading) ]
  | .[]' "$DIGEST")
[ -z "$over" ] || { echo "digest section over Slack's 3000-char block limit: $over" >&2; exit 1; }

post() {
  # DIGEST_DRY_RUN=1 prints each payload instead of sending it, so the rendering
  # can be checked locally without a token. Slack's own limits (block count, the
  # 3000-character section cap) are still only enforced by Slack.
  if [ "${DIGEST_DRY_RUN:-}" = "1" ]; then
    echo "$1" | jq . >&2
    echo '{"ok":true,"ts":"0.0"}'
    return 0
  fi

  # Timeouts matter: this runs unattended, and a hung connection with no cap
  # would stall the job rather than fail it.
  curl -sS --connect-timeout 10 --max-time 30 -X POST "$API" \
    -H "Authorization: Bearer $SLACK_BOT_TOKEN" \
    -H 'Content-type: application/json; charset=utf-8' \
    --data "$1"
}

# Slack answers 200 with ok:false on failure, so check the payload not the code.
#
# The body is not guaranteed to be JSON at all: a proxy or gateway failure arrives
# as HTML or plain text. jq cannot parse that, so `.error // "..."` yields nothing
# and writes a parse error to stderr — the fallback string never reaches the
# operator. Read the error out separately and default it here, where it works for
# both shapes.
check() {
  if echo "$1" | jq -e '.ok' >/dev/null 2>&1; then
    return 0
  fi

  local err
  err=$(echo "$1" | jq -r '.error // empty' 2>/dev/null) || true

  echo "slack error: ${err:-unparseable response}" >&2
  echo "$1" >&2
  return 1
}

# --- parent ------------------------------------------------------------------

# The parent is the one-line roll-up as its first block, then one block per
# `notes` section. `text` stays the bare summary: it is what notifications and
# the channel preview show, and what the thread replies hang off.
parent_payload=$(jq -c --arg c "$CHANNEL" '
  def block: "*" + .heading + "*\n" + (.lines | map("• " + .) | join("\n"));
  {
    channel: $c,
    text: .summary,
    blocks: (
      [ { type: "section", text: { type: "mrkdwn", text: .summary } } ]
      + [ (.notes // [])[] | { type: "section", text: { type: "mrkdwn", text: block } } ]
    )
  }' "$DIGEST")

parent=$(post "$parent_payload")
check "$parent"

TS=$(echo "$parent" | jq -r '.ts')
echo "parent posted: $TS"

count=$(jq '.items | length' "$DIGEST")
if [ "$count" -eq 0 ]; then
  echo "no items - parent only"
  exit 0
fi

# --- children ----------------------------------------------------------------

failed=0
for i in $(seq 0 $((count - 1))); do
  # The issue URL is built here, with jq's @uri, rather than written into the
  # digest file by the routine: an unencoded space or '#' breaks a button URL
  # silently, and this is the one place that can guarantee the encoding.
  #
  # reply_broadcast surfaces CRITICAL items in the main channel; nothing else.
  payload=$(jq -c \
    --arg c "$CHANNEL" \
    --arg ts "$TS" \
    --arg repo "$REPO" \
    --argjson i "$i" \
    '
    .items[$i] as $it
    | ($it.severity == "CRITICAL") as $crit
    | ("https://github.com/" + $repo + "/issues/new"
       + "?labels=research"
       + "&title=" + ($it.title | @uri)
       + "&body="  + (
           ($it.text // $it.title)
           + "\n\nSource: " + ($it.url // "n/a")
           + (if $it.brief then "\n\nBrief: `" + $it.brief + "`" else "" end)
           | @uri
         )) as $url
    | {
        channel: $c,
        thread_ts: $ts,
        reply_broadcast: $crit,
        text: $it.text,
        blocks: [
          { type: "section",
            text: { type: "mrkdwn", text: $it.text } },
          { type: "actions",
            elements: [
              { type: "button",
                text: { type: "plain_text", text: "Open issue" },
                url: $url }
              + (if $crit then {style: "danger"} else {} end)
            ] }
        ]
      }
    ' "$DIGEST")

  slug=$(jq -r ".items[$i].slug // \"item-$i\"" "$DIGEST")
  child=$(post "$payload")
  if check "$child"; then
    echo "posted: $slug"
  else
    echo "FAILED: $slug" >&2
    failed=1
  fi
done

exit $failed
