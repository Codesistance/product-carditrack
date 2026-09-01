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
check() {
  if ! echo "$1" | jq -e '.ok' >/dev/null 2>&1; then
    echo "slack error: $(echo "$1" | jq -r '.error // "unparseable response"')" >&2
    echo "$1" >&2
    return 1
  fi
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
