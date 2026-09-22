#!/usr/bin/env python3
"""Attach a changelog to a TestFlight build as its "What to Test" note.

`apple-actions/upload-testflight-build` can do this itself, but only by editing
a beta build localization App Store Connect has already created — it polls for
one for ten minutes and fails the whole step if none appears. Apple creates
those from the app's *beta app* localizations, so a build uploaded before Test
Information was ever filled in gets none, and the poll can never succeed. That
failure landed on a job whose binary had already shipped.

This script creates the localization when it is missing instead of waiting for
one, so the note lands on the first build as readily as the hundredth.

It also owns the wait for App Store Connect to process the build. The action
can do that too, but on a single token with a ten-minute life and a poll that
backs off past it: build 20329 (2026-09-21) took over thirteen minutes to be
listed at all, and the action's fifth poll was refused with a 401 — a delivered
binary reported as a failed step. Here the token is re-minted as it ages, and
the deadline is one the app's processing time actually fits inside.

Without ``--notes-file`` the script only waits: the workflow runs it that way
first, as a step that fails the job when the build never becomes VALID, and
then again with the notes as a best-effort step. A build that fails processing
is a failed deploy; a build without its notes is not.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

import jwt

API = "https://api.appstoreconnect.apple.com/v1"

# App Store Connect rejects tokens with a lifetime over 20 minutes, and refuses
# one the moment it expires — mid-poll, if the poll outlives it. So a token is
# minted for 15 minutes and replaced after 10, well before either edge.
TOKEN_TTL_SECONDS = 15 * 60
TOKEN_REFRESH_AFTER_SECONDS = 10 * 60

# TestFlight truncates "What to Test" past 4000 characters. mobile-build-changelog.py
# already fits the text to that; this is the backstop for anything else.
WHATS_NEW_LIMIT = 4000

# A fresh upload is not listed for a while, then sits in PROCESSING; "What to
# Test" only sticks to a build that has reached VALID. Both waits share one
# deadline. Thirty minutes is well past the longest this app has taken and is
# Linux-runner time, which is cheap; the step is continue-on-error besides.
PROCESSING_WAIT_MINUTES = 30
POLL_DELAY_SECONDS = 30

# Per request. The deadline above is only checked between calls, so a call that
# could hang would let the wait outlive it; this keeps every call bounded.
REQUEST_TIMEOUT_SECONDS = 30

# Used only when the app has no beta app localization to borrow a locale from —
# i.e. Test Information is still empty. Apple accepts the note either way.
FALLBACK_LOCALE = "en-US"


class AppStoreError(RuntimeError):
    pass


class Bearer:
    """A token that re-mints itself before App Store Connect would refuse it."""

    def __init__(self, issuer_id: str, key_id: str, private_key: str) -> None:
        self._issuer_id = issuer_id
        self._key_id = key_id
        self._private_key = private_key
        self._minted_at = 0.0
        self._value = ""

    def __str__(self) -> str:
        now = time.time()
        if now - self._minted_at > TOKEN_REFRESH_AFTER_SECONDS:
            self._value = jwt.encode(
                {
                    "iss": self._issuer_id,
                    "aud": "appstoreconnect-v1",
                    "iat": int(now) - 60,
                    "exp": int(now) + TOKEN_TTL_SECONDS,
                },
                self._private_key,
                algorithm="ES256",
                headers={"kid": self._key_id, "typ": "JWT"},
            )
            self._minted_at = now
        return self._value


def call(
    method: str,
    path: str,
    bearer: Bearer,
    payload: dict | None = None,
) -> dict:
    request = urllib.request.Request(
        f"{API}{path}",
        method=method,
        data=json.dumps(payload).encode() if payload is not None else None,
        headers={
            "Authorization": f"Bearer {bearer}",
            "Content-Type": "application/json",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            body = response.read()
            return json.loads(body) if body else {}
    except urllib.error.HTTPError as error:
        detail = error.read().decode(errors="replace")
        raise AppStoreError(f"{method} {path} failed ({error.code}): {detail}") from error
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as error:
        raise AppStoreError(f"{method} {path} failed: {error}") from error


def find_app(bundle_id: str, bearer: Bearer) -> str:
    query = urllib.parse.urlencode({"filter[bundleId]": bundle_id, "limit": 1})
    data = call("GET", f"/apps?{query}", bearer).get("data") or []
    if not data:
        raise AppStoreError(f"No App Store Connect app found for bundle id {bundle_id}.")
    return data[0]["id"]


def wait_for_build(
    app_id: str, build_number: str, bearer: Bearer, wait_minutes: int
) -> str:
    """The build's id once App Store Connect has finished processing it.

    Processing ends in VALID, or in FAILED / INVALID — a duplicate build number
    lands there, and so does a binary Apple's checks reject. Neither will ever
    reach TestFlight, so they are reported as such rather than waited on.
    """
    query = urllib.parse.urlencode(
        {"filter[app]": app_id, "filter[version]": build_number, "limit": 1}
    )
    # One poll always happens, so a zero wait is a single check. After that the
    # deadline is enforced before sleeping, and the sleep is capped to what is
    # left, so the wait overruns by at most one request timeout.
    deadline = time.monotonic() + wait_minutes * 60
    while True:
        data = call("GET", f"/builds?{query}", bearer).get("data") or []
        state = (data[0].get("attributes") or {}).get("processingState") if data else None
        if state == "VALID":
            return data[0]["id"]
        if state in ("FAILED", "INVALID"):
            raise AppStoreError(
                f"Build {build_number} is {state} in App Store Connect — it will never "
                "reach TestFlight. Apple emails the account holder the reason; a duplicate "
                "build number is the usual one."
            )
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise AppStoreError(
                f"Build {build_number} was not processed within {wait_minutes} minutes "
                f"(last seen: {state or 'not listed yet'}). The binary is uploaded; "
                "attach the notes by hand once it appears, or dispatch the push again — "
                "a build the store already holds is skipped, and only the notes are redone."
            )
        delay = min(POLL_DELAY_SECONDS, remaining)
        print(
            f"Build {build_number}: {state or 'not listed yet'}; "
            f"checking again in {delay:.0f}s",
            flush=True,
        )
        time.sleep(delay)


def preferred_locale(app_id: str, bearer: Bearer) -> str:
    """The locale Apple would have used, so a later auto-created row matches ours."""
    data = call("GET", f"/apps/{app_id}/betaAppLocalizations", bearer).get("data") or []
    for localization in data:
        locale = (localization.get("attributes") or {}).get("locale")
        if locale:
            return locale
    return FALLBACK_LOCALE


def attach(build_id: str, app_id: str, notes: str, bearer: Bearer) -> None:
    existing = call("GET", f"/builds/{build_id}/betaBuildLocalizations", bearer)
    localizations = existing.get("data") or []

    if not localizations:
        locale = preferred_locale(app_id, bearer)
        call(
            "POST",
            "/betaBuildLocalizations",
            bearer,
            {
                "data": {
                    "type": "betaBuildLocalizations",
                    "attributes": {"whatsNew": notes, "locale": locale},
                    "relationships": {
                        "build": {"data": {"type": "builds", "id": build_id}}
                    },
                }
            },
        )
        print(f"Created {locale} release note for build {build_id}.")
        return

    # Every locale gets the same changelog — the alternative is testers on one
    # locale seeing notes and testers on another seeing none.
    for localization in localizations:
        localization_id = localization["id"]
        locale = (localization.get("attributes") or {}).get("locale", "?")
        call(
            "PATCH",
            f"/betaBuildLocalizations/{localization_id}",
            bearer,
            {
                "data": {
                    "id": localization_id,
                    "type": "betaBuildLocalizations",
                    "attributes": {"whatsNew": notes},
                }
            },
        )
        print(f"Updated {locale} release note for build {build_id}.")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle-id", required=True)
    parser.add_argument("--build", required=True, help="CFBundleVersion of the upload")
    parser.add_argument(
        "--notes-file",
        help="the changelog to attach; without it the script only waits for processing",
    )
    parser.add_argument("--issuer-id", required=True)
    parser.add_argument("--key-id", required=True)
    parser.add_argument(
        "--private-key-file", required=True, help="App Store Connect .p8, PEM encoded"
    )
    parser.add_argument(
        "--wait-minutes",
        type=int,
        default=PROCESSING_WAIT_MINUTES,
        help="how long to wait for App Store Connect to list and process the build",
    )
    args = parser.parse_args()

    notes = None
    if args.notes_file:
        with open(args.notes_file, encoding="utf-8") as handle:
            notes = handle.read().strip()[:WHATS_NEW_LIMIT]
        if not notes:
            print("No release notes to attach.")
            return 0

    with open(args.private_key_file, encoding="utf-8") as handle:
        private_key = handle.read()

    try:
        bearer = Bearer(args.issuer_id, args.key_id, private_key)
        app_id = find_app(args.bundle_id, bearer)
        build_id = wait_for_build(app_id, args.build, bearer, args.wait_minutes)
        print(f"Build {args.build} is VALID on App Store Connect ({build_id}).")
        if notes is not None:
            attach(build_id, app_id, notes, bearer)
    except AppStoreError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
