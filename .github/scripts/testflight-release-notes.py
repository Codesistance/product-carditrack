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

# App Store Connect rejects tokens with a lifetime over 20 minutes. Nothing here
# polls for long, so a short one is plenty.
TOKEN_TTL_SECONDS = 15 * 60

# TestFlight truncates "What to Test" past 4000 characters. mobile-build-changelog.py
# already fits the text to that; this is the backstop for anything else.
WHATS_NEW_LIMIT = 4000

# The caller waits for processing before invoking us, so the build is normally
# visible on the first try. This covers the seconds of index lag after that.
BUILD_LOOKUP_ATTEMPTS = 6
BUILD_LOOKUP_DELAY_SECONDS = 15

# Used only when the app has no beta app localization to borrow a locale from —
# i.e. Test Information is still empty. Apple accepts the note either way.
FALLBACK_LOCALE = "en-US"


class AppStoreError(RuntimeError):
    pass


def token(issuer_id: str, key_id: str, private_key: str) -> str:
    now = int(time.time())
    return jwt.encode(
        {
            "iss": issuer_id,
            "aud": "appstoreconnect-v1",
            "iat": now - 60,
            "exp": now + TOKEN_TTL_SECONDS,
        },
        private_key,
        algorithm="ES256",
        headers={"kid": key_id, "typ": "JWT"},
    )


def call(
    method: str,
    path: str,
    bearer: str,
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
        with urllib.request.urlopen(request) as response:
            body = response.read()
            return json.loads(body) if body else {}
    except urllib.error.HTTPError as error:
        detail = error.read().decode(errors="replace")
        raise AppStoreError(f"{method} {path} failed ({error.code}): {detail}") from error


def find_app(bundle_id: str, bearer: str) -> str:
    query = urllib.parse.urlencode({"filter[bundleId]": bundle_id, "limit": 1})
    data = call("GET", f"/apps?{query}", bearer).get("data") or []
    if not data:
        raise AppStoreError(f"No App Store Connect app found for bundle id {bundle_id}.")
    return data[0]["id"]


def find_build(app_id: str, build_number: str, bearer: str) -> str:
    query = urllib.parse.urlencode(
        {"filter[app]": app_id, "filter[version]": build_number, "limit": 1}
    )
    for attempt in range(BUILD_LOOKUP_ATTEMPTS):
        data = call("GET", f"/builds?{query}", bearer).get("data") or []
        if data:
            return data[0]["id"]
        if attempt < BUILD_LOOKUP_ATTEMPTS - 1:
            print(
                f"Build {build_number} not visible yet "
                f"(attempt {attempt + 1}/{BUILD_LOOKUP_ATTEMPTS}); "
                f"retrying in {BUILD_LOOKUP_DELAY_SECONDS}s",
                flush=True,
            )
            time.sleep(BUILD_LOOKUP_DELAY_SECONDS)
    raise AppStoreError(f"Build {build_number} never became visible in App Store Connect.")


def preferred_locale(app_id: str, bearer: str) -> str:
    """The locale Apple would have used, so a later auto-created row matches ours."""
    data = call("GET", f"/apps/{app_id}/betaAppLocalizations", bearer).get("data") or []
    for localization in data:
        locale = (localization.get("attributes") or {}).get("locale")
        if locale:
            return locale
    return FALLBACK_LOCALE


def attach(build_id: str, app_id: str, notes: str, bearer: str) -> None:
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
    parser.add_argument("--notes-file", required=True)
    parser.add_argument("--issuer-id", required=True)
    parser.add_argument("--key-id", required=True)
    parser.add_argument(
        "--private-key-file", required=True, help="App Store Connect .p8, PEM encoded"
    )
    args = parser.parse_args()

    with open(args.notes_file, encoding="utf-8") as handle:
        notes = handle.read().strip()[:WHATS_NEW_LIMIT]
    if not notes:
        print("No release notes to attach.")
        return 0

    with open(args.private_key_file, encoding="utf-8") as handle:
        private_key = handle.read()

    try:
        bearer = token(args.issuer_id, args.key_id, private_key)
        app_id = find_app(args.bundle_id, bearer)
        build_id = find_build(app_id, args.build, bearer)
        attach(build_id, app_id, notes, bearer)
    except AppStoreError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
