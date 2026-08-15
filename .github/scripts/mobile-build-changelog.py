#!/usr/bin/env python3
"""Build per-store changelogs for a mobile upload from git history.

Play Console "What's new" is capped at 500 characters. TestFlight "What to
Test" is capped at 4000. Both get the same commit list, truncated to fit.
"""

from __future__ import annotations

import argparse
import subprocess
import sys


PLAY_LIMIT = 500
TESTFLIGHT_LIMIT = 4000


def git_log(since_tag: str) -> list[str]:
    pretty = ["git", "log", "--no-merges", "--pretty=format:%s (%h)"]
    if since_tag == "v0.0.0":
        cmd = pretty + ["-n", "40"]
    else:
        cmd = pretty + [f"{since_tag}..HEAD"]
    result = subprocess.run(cmd, check=True, capture_output=True, text=True)
    lines = []
    for raw in result.stdout.splitlines():
        line = " ".join(raw.split())
        if line:
            lines.append(f"- {line}")
    return lines


def fit(header: str, bullets: list[str], limit: int) -> str:
    if not bullets:
        body = f"{header}\nNo new commits since the previous store version."
        return body if len(body) <= limit else header[:limit]

    kept: list[str] = []
    for bullet in bullets:
        candidate = "\n".join([header, *kept, bullet])
        if len(candidate) <= limit:
            kept.append(bullet)
            continue
        if kept:
            # Everything already in `kept` fits by construction, so the "…" is the
            # only part that might not. Drop the marker rather than the bullets it
            # stands in for — losing a truncation hint beats losing the changelog.
            overflow = "\n".join([header, *kept, "…"])
            return overflow if len(overflow) <= limit else "\n".join([header, *kept])
        # Header plus the very first bullet is already too long — hard-cut.
        hard = f"{header}\n{bullet}"
        return hard[: limit - 1] + "…" if len(hard) > limit else hard
    return "\n".join([header, *kept])


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--since", required=True, help="Previous semver tag, or v0.0.0")
    parser.add_argument("--version", required=True)
    parser.add_argument("--build", required=True)
    parser.add_argument("--testflight-out", required=True)
    parser.add_argument("--play-out", required=True)
    args = parser.parse_args()

    header = f"CardiTrack {args.version} (build {args.build})"
    bullets = git_log(args.since)

    testflight = fit(header, bullets, TESTFLIGHT_LIMIT - 1)
    play = fit(header, bullets, PLAY_LIMIT - 1)

    # Trailing newline is required so GITHUB_OUTPUT delimiters stay on their own
    # line. Fit to limit-1 so the serialized value stays inside the store caps.
    with open(args.testflight_out, "w", encoding="utf-8") as handle:
        handle.write(testflight)
        handle.write("\n")
    with open(args.play_out, "w", encoding="utf-8") as handle:
        handle.write(play)
        handle.write("\n")

    print(f"TestFlight notes ({len(testflight)} chars):\n{testflight}\n")
    print(f"Play notes ({len(play)} chars):\n{play}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
