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

post() {
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

SUMMARY=$(jq -r '.summary' "$DIGEST")

parent=$(post "$(jq -n --arg c "$CHANNEL" --arg t "$SUMMARY" '{channel:$c, text:$t}')")
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
