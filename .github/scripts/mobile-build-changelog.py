#!/usr/bin/env python3
"""Build per-store "What's new" text for a mobile upload from git history.

The notes are read by customers and testers, not engineers, so the output is
a short list of plain-language changes with no commit hashes, PR numbers or
engineering prefixes. Each commit since the previous store version becomes a
bullet only if it is likely to be felt in the app:

* A ``Release-note:`` trailer in the commit body wins outright — its text is
  the bullet, verbatim. ``Release-note: none`` hides the commit. Use it when
  the subject line is written for reviewers rather than the person reading
  the store listing.
* Otherwise the commit subject is used, cleaned up, when the commit touches
  the mobile app or the domain it renders. Commits that only change the API,
  workers, infrastructure, docs, tests or CI are folded into a single closing
  line — customers do not need to hear about a Terraform budget or a Copilot
  round.

Play Console "What's new" is capped at 500 characters. TestFlight "What to
Test" is capped at 4000. Both get the same bullets, truncated to fit.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys

PLAY_LIMIT = 500
TESTFLIGHT_LIMIT = 4000

TRAILER = "Release-note"
HIDE_MARKERS = {"none", "no", "skip", "internal", "-"}

# Paths whose changes a customer can see in the app. Anything else is
# "behind the scenes" — still shipped, just not itemised.
CUSTOMER_FACING_PREFIXES = (
    "src/Presentation/CardiTrack.Mobile/",
    "src/Presentation/CardiTrack.Mobile.Core/",
    "src/Core/CardiTrack.Domain/",
    "src/Core/CardiTrack.Application/",
)

# Subjects that are engineering conversation even when the diff lands in
# customer-facing paths.
NOISE_SUBJECT = re.compile(
    r"""^(
        address\s+(the\s+)?copilot |
        (apply|fix|resolve)\s+(the\s+)?(copilot|review)\b |
        revert\b |
        merge\b |
        bump\b |
        pin\b |
        move\b |
        rename\b |
        (add|fix|update|adjust|extend|repair)\s+(the\s+)?(unit|integration|e2e)?\s*tests?\b |
        fix\s+(a\s+)?typo |
        (chore|ci|build|test|docs|refactor|style)(\(.*?\))?:
    )""",
    re.IGNORECASE | re.VERBOSE,
)

# Engineering vocabulary anywhere in a subject: the change may well reach the
# app, but the sentence was written for a reviewer and would read as noise on
# a store listing. Give such commits a Release-note trailer to surface them.
NOISE_VOCABULARY = re.compile(
    r"\b(migrations?|rollout\s+steps?|logging|registry|pipeline|workflows?|timeouts?|"
    r"exceptions?|refactor\w*|telemetry|traces?|tracer|schema|endpoints?|dto|"
    r"namespace|catalogue|prompt\s+version|copilot)\b"
    r"|\b(application|services?)/",  # project-path fragments end in "/", not a word boundary
    re.IGNORECASE,
)

# Engineering decoration on a subject line: conventional-commit prefixes,
# trailing PR numbers, and issue references.
CONVENTIONAL_PREFIX = re.compile(r"^(feat|fix|perf|chore|ci|build|test|docs|refactor|style)(\([^)]*\))?!?:\s*", re.IGNORECASE)
PR_SUFFIX = re.compile(r"\s*\(#\d+\)\s*$")
ISSUE_REF = re.compile(r"\s*\(?(closes|fixes|resolves|refs?)?\s*#\d+\)?", re.IGNORECASE)

BEHIND_THE_SCENES = "Plus stability and performance improvements behind the scenes."
NOTHING_NEW = "Stability and performance improvements."

# Record separator between commits and unit separator between fields. Neither
# can appear in a subject line or a trailer, so parsing stays unambiguous.
RS = "\x1e"
US = "\x1f"


def git_commits(since_tag: str) -> list[tuple[str, str, str]]:
    """Return (subject, release_note, changed_files) per commit, newest first."""
    fmt = f"%x1e%s%x1f%(trailers:key={TRAILER},valueonly,separator=%x20)%x1f"
    cmd = ["git", "log", "--no-merges", "--name-only", f"--pretty=format:{fmt}"]
    if since_tag == "v0.0.0":
        cmd += ["-n", "40"]
    else:
        cmd.append(f"{since_tag}..HEAD")
    # Decode as UTF-8 regardless of the runner locale: subjects and trailers
    # carry curly quotes and dashes, and a C locale would choke on them.
    result = subprocess.run(
        cmd, check=True, capture_output=True, encoding="utf-8", errors="replace"
    )
    commits = []
    for record in result.stdout.split(RS):
        if not record.strip():
            continue
        subject, note, files = record.split(US, 2)
        commits.append((subject.strip(), note.strip(), files))
    return commits


def tidy_subject(subject: str) -> str:
    text = " ".join(subject.split())
    text = CONVENTIONAL_PREFIX.sub("", text)
    text = PR_SUFFIX.sub("", text)
    text = ISSUE_REF.sub("", text).strip()
    if not text:
        return ""
    text = text[0].upper() + text[1:]
    # Keep sentence-ending punctuation the author chose; add a full stop
    # only when there is none.
    return text if text.endswith((".", "!", "?", "…")) else text + "."


def is_customer_facing(files: str) -> bool:
    for line in files.splitlines():
        path = line.strip()
        if path.startswith(CUSTOMER_FACING_PREFIXES) and "/Migrations/" not in path:
            return True
    return False


def bullets_for(commits: list[tuple[str, str, str]]) -> tuple[list[str], bool]:
    """Turn commits into store bullets.

    Returns the bullets and whether anything was folded into the closing line.
    """
    bullets: list[str] = []
    folded = False
    seen: set[str] = set()
    for subject, note, files in commits:
        if note:
            if note.lower() in HIDE_MARKERS:
                folded = True
                continue
            text = tidy_subject(note)
        elif (
            is_customer_facing(files)
            and not NOISE_SUBJECT.match(subject)
            and not NOISE_VOCABULARY.search(subject)
        ):
            text = tidy_subject(subject)
        else:
            folded = True
            continue
        if not text or text.lower() in seen:
            continue
        seen.add(text.lower())
        bullets.append(f"• {text}")
    return bullets, folded


def fit(header: str, bullets: list[str], footer: str, limit: int) -> str:
    """Compose header + bullets [+ "…"] [+ footer] inside `limit`.

    Bullets are the content, so they are kept first, newest to oldest. When
    any bullet is dropped a "…" marker always follows the ones kept — a note
    that simply ends reads as complete. The footer is added last and only if
    it still fits; it is the one line nothing is lost by omitting.
    """
    if not bullets:
        body = f"{header}\n{footer}" if footer else header
        return body if len(body) <= limit else header[:limit]

    def joined(*parts: str) -> str:
        return "\n".join([header, *parts])

    kept: list[str] = []
    for bullet in bullets:
        if len(joined(*kept, bullet)) > limit:
            break
        kept.append(bullet)

    if not kept:
        # Header plus the very first bullet is already too long — hard-cut.
        hard = joined(bullets[0])
        return hard[: limit - 1] + "…"

    dropped = len(kept) < len(bullets)
    if dropped:
        # Make room for the marker, giving up the oldest kept bullet if needed.
        while len(kept) > 1 and len(joined(*kept, "…")) > limit:
            kept.pop()
        if len(joined(*kept, "…")) > limit:
            return joined(*kept)
        kept.append("…")

    with_footer = joined(*kept, footer) if footer else ""
    return with_footer if footer and len(with_footer) <= limit else joined(*kept)

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--since", required=True, help="Previous semver tag, or v0.0.0")
    parser.add_argument("--version", required=True)
    parser.add_argument("--build", required=True, help="Kept for the job summary; not shown to customers")
    parser.add_argument("--testflight-out", required=True)
    parser.add_argument("--play-out", required=True)
    args = parser.parse_args()

    header = f"What's new in CardiTrack {args.version.lstrip('v')}"
    bullets, folded = bullets_for(git_commits(args.since))
    footer = BEHIND_THE_SCENES if (bullets and folded) else ""
    if not bullets:
        footer = NOTHING_NEW

    testflight = fit(header, bullets, footer, TESTFLIGHT_LIMIT - 1)
    play = fit(header, bullets, footer, PLAY_LIMIT - 1)

    # Trailing newline is required so GITHUB_OUTPUT delimiters stay on their own
    # line. Fit to limit-1 so the serialized value stays inside the store caps.
    with open(args.testflight_out, "w", encoding="utf-8") as handle:
        handle.write(testflight)
        handle.write("\n")
    with open(args.play_out, "w", encoding="utf-8") as handle:
        handle.write(play)
        handle.write("\n")

    print(f"Build {args.build}")
    print(f"TestFlight notes ({len(testflight)} chars):\n{testflight}\n")
    print(f"Play notes ({len(play)} chars):\n{play}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
