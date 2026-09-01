#!/usr/bin/env bash
# Posts a summary message, then each item as a threaded reply under it.
#
# Reads:  items/*.json  (each: {"text": "...", "blocks": [...]})
#         SUMMARY       (env var, the parent message text)
#         SLACK_BOT_TOKEN (vault placeholder, substituted at egress)
# Writes: run-ts.txt    (parent ts, for research/log.json)

set -euo pipefail

CHANNEL="${SLACK_CHANNEL:?set SLACK_CHANNEL to the channel ID, e.g. C0XXXXXXX}"
# Checked here, at top level, and not at the point of use below: a ${VAR:?} inside a
# $(...) aborts only the subshell, so the guard would print its message and the script
# would carry on and post an empty parent message.
SUMMARY="${SUMMARY:?set SUMMARY to the parent message text}"
# Same reason, and the same top-level placement: post() reads this inside a command
# substitution, so under `set -u` an unset token would kill only that subshell and the
# run would report an unparseable response instead of missing configuration. Only
# presence is checkable here — the sandbox value is a vault placeholder, substituted
# at the network boundary — but presence is the failure worth catching.
: "${SLACK_BOT_TOKEN:?set SLACK_BOT_TOKEN (vault credential, substituted at egress)}"
API="https://slack.com/api/chat.postMessage"

post() {
  # Timeouts matter here: this runs unattended on a schedule, and a hung
  # connection with no cap would stall the session rather than fail it.
  curl -sS --connect-timeout 10 --max-time 30 -X POST "$API" \
    -H "Authorization: Bearer $SLACK_BOT_TOKEN" \
    -H 'Content-type: application/json; charset=utf-8' \
    --data "$1"
}

# Slack answers 200 with ok:false on failure, so check the payload not the code.
#
# The body is not guaranteed to be JSON at all: a proxy or gateway failure arrives as
# HTML or plain text. jq cannot parse that, so `.error // "..."` yields nothing and
# writes a parse error to stderr — the fallback string never reaches the operator. Read
# the error out separately and default it here instead, where it works for both shapes.
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

parent=$(post "$(jq -n --arg c "$CHANNEL" --arg t "$SUMMARY" \
  '{channel:$c, text:$t}')")
check "$parent"

TS=$(echo "$parent" | jq -r '.ts')
echo "$TS" > run-ts.txt
echo "parent posted: $TS"

# --- children ----------------------------------------------------------------

shopt -s nullglob
items=(items/*.json)

if [ ${#items[@]} -eq 0 ]; then
  echo "no items - parent only"
  exit 0
fi

failed=0
for f in "${items[@]}"; do
  # reply_broadcast surfaces CRITICAL items in the main channel; nothing else.
  broadcast=false
  if jq -e '.text | test("CRITICAL")' "$f" >/dev/null 2>&1; then
    broadcast=true
  fi

  payload=$(jq -c \
    --arg c "$CHANNEL" \
    --arg ts "$TS" \
    --argjson b "$broadcast" \
    '{channel:$c, thread_ts:$ts, reply_broadcast:$b, text:.text} +
     (if .blocks then {blocks:.blocks} else {} end)' "$f")

  child=$(post "$payload")
  if check "$child"; then
    echo "posted: $f"
  else
    echo "FAILED: $f" >&2
    failed=1
  fi
done

exit $failed
