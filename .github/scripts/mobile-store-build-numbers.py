#!/usr/bin/env python3
"""Report the highest build number a store already holds.

Neither store will accept a build number it has seen before, and neither forgets
one. The push workflow used to discover that the only way there was: by
uploading, being refused, and failing the run after the fact — twice over, once
per platform. On 2026-09-18 it re-selected a tag that had already shipped and
both stores rejected it, which is what this exists to catch first.

Printing the store's current high-water mark lets the run decide before it
uploads anything, and turns "Version code 1524 has already been used" into a
message that names the tag and says what to do about it.

Exit codes are the contract:

* ``0`` — the number is on stdout. ``0`` means the store holds no build at all.
* ``2`` — the store could not be asked (credentials absent, API refused or
  unreachable). The caller is expected to warn and carry on: an upload blocked
  by a momentary API failure is a worse outcome than one that fails on a
  duplicate, which is the very thing the upload step itself still catches.
* ``1`` — the arguments are wrong.

With ``--build N`` — the number the caller is about to push — the answer names
that build as well as the high-water mark::

    highest=<n>
    holds=<true|false>

``holds`` is what a re-run meets: an earlier run delivered the build, so there
is nothing to upload and nothing to fail. It is asked for by number, not read
off the high-water page, and on App Store Connect only a build that is VALID or
still PROCESSING counts — one that FAILED or is INVALID never reaches TestFlight,
yet its number is spent all the same, so it shows in ``highest`` and not in
``holds``: a clash, to be rebuilt under a new tag. The other number that can
never land is one the store has not seen sitting below one it has.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

APPSTORE_API = "https://api.appstoreconnect.apple.com/v1"
PLAY_API = "https://androidpublisher.googleapis.com/androidpublisher/v3"
GOOGLE_TOKEN_URI = "https://oauth2.googleapis.com/token"
PLAY_SCOPE = "https://www.googleapis.com/auth/androidpublisher"

# App Store Connect refuses a token with a lifetime over 20 minutes; Google
# refuses an assertion over an hour. Nothing here is slow, so both are short.
APPSTORE_TOKEN_TTL_SECONDS = 15 * 60
PLAY_ASSERTION_TTL_SECONDS = 10 * 60

# One page of the most recently uploaded builds, for the high-water mark. The
# highest build number is in practice among the newest, and this is a pre-flight
# guard rather than an accounting record — the upload itself remains the
# authority on duplicates. Whether a *given* build is held is asked separately,
# by number, so it does not depend on being inside this page.
APPSTORE_BUILD_PAGE = 200

# Processing states under which a build is, or will be, on TestFlight.
APPSTORE_LIVE_STATES = frozenset({"VALID", "PROCESSING"})

TIMEOUT_SECONDS = 30


class StoreUnavailable(RuntimeError):
    """The store could not be asked. Distinct from it answering 'nothing here'."""


def _call(
    method: str,
    url: str,
    headers: dict[str, str],
    data: bytes | None = None,
) -> dict:
    request = urllib.request.Request(url, method=method, data=data, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=TIMEOUT_SECONDS) as response:
            body = response.read()
            return json.loads(body) if body else {}
    except urllib.error.HTTPError as error:
        detail = error.read().decode(errors="replace")[:500]
        raise StoreUnavailable(f"{method} {url} failed ({error.code}): {detail}") from error
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as error:
        raise StoreUnavailable(f"{method} {url} failed: {error}") from error


def _numbers(values: list) -> list[int]:
    """The values that are whole numbers, ignoring anything that is not.

    Build numbers are strings on both APIs and neither promises they are
    numeric — TestFlight in particular will hold things like "1.0.2" from a
    hand-made upload. Those cannot be compared with ours, so they are skipped
    rather than guessed at.
    """
    numbers = []
    for value in values:
        try:
            numbers.append(int(str(value).strip()))
        except (TypeError, ValueError):
            continue
    return numbers


# ── App Store Connect ───────────────────────────────────────────────────────


def appstore_builds(
    bundle_id: str, issuer_id: str, key_id: str, private_key: str, build: int | None
) -> tuple[list[int], list[int]]:
    """(numbers App Store Connect has seen, numbers that are or will be on TestFlight).

    The first is a page of the newest builds, plus ``build`` itself when asked
    for; the second is the subset of those in a live processing state.
    """
    import jwt  # Imported late so a missing dependency is a store we cannot ask.

    now = int(time.time())
    bearer = jwt.encode(
        {
            "iss": issuer_id,
            "aud": "appstoreconnect-v1",
            "iat": now - 60,
            "exp": now + APPSTORE_TOKEN_TTL_SECONDS,
        },
        private_key,
        algorithm="ES256",
        headers={"kid": key_id, "typ": "JWT"},
    )
    headers = {"Authorization": f"Bearer {bearer}", "Content-Type": "application/json"}

    query = urllib.parse.urlencode({"filter[bundleId]": bundle_id, "limit": 1})
    apps = _call("GET", f"{APPSTORE_API}/apps?{query}", headers).get("data") or []
    if not apps:
        # A bundle id App Store Connect has never seen holds no builds, which is
        # a real answer: the first upload can use any number.
        return [], []
    app_id = apps[0]["id"]

    def builds(filters: dict) -> list:
        query = urllib.parse.urlencode(
            {
                "filter[app]": app_id,
                "fields[builds]": "version,processingState",
                **filters,
            }
        )
        return _call("GET", f"{APPSTORE_API}/builds?{query}", headers).get("data") or []

    listed = builds({"sort": "-uploadedDate", "limit": APPSTORE_BUILD_PAGE})
    if build is not None:
        # By number, so an old build past the page above is still found.
        listed = listed + builds({"filter[version]": str(build), "limit": 10})

    seen, live = [], []
    for entry in listed:
        attributes = entry.get("attributes") or {}
        seen.append(attributes.get("version"))
        if attributes.get("processingState") in APPSTORE_LIVE_STATES:
            live.append(attributes.get("version"))
    return _numbers(seen), _numbers(live)


# ── Play Console ────────────────────────────────────────────────────────────


def _play_token(service_account: dict) -> str:
    import jwt  # As above.

    now = int(time.time())
    assertion = jwt.encode(
        {
            "iss": service_account["client_email"],
            "scope": PLAY_SCOPE,
            "aud": service_account.get("token_uri", GOOGLE_TOKEN_URI),
            "iat": now,
            "exp": now + PLAY_ASSERTION_TTL_SECONDS,
        },
        service_account["private_key"],
        algorithm="RS256",
    )
    body = urllib.parse.urlencode(
        {
            "grant_type": "urn:ietf:params:oauth:grant-type:jwt-bearer",
            "assertion": assertion,
        }
    ).encode()
    granted = _call(
        "POST",
        service_account.get("token_uri", GOOGLE_TOKEN_URI),
        {"Content-Type": "application/x-www-form-urlencoded"},
        body,
    )
    token = granted.get("access_token")
    if not token:
        raise StoreUnavailable("Google returned no access token for the Play service account.")
    return token


def play_version_codes(package_name: str, service_account: dict) -> tuple[list[int], list[int]]:
    """(versionCodes Play Console lists, the same list): Play keeps only accepted uploads."""
    token = _play_token(service_account)
    headers = {"Authorization": f"Bearer {token}", "Content-Type": "application/json"}
    base = f"{PLAY_API}/applications/{urllib.parse.quote(package_name)}/edits"

    # Listing what has been uploaded needs an edit to read through. It is
    # discarded straight afterwards: nothing is committed, so the app is
    # untouched either way.
    edit_id = _call("POST", base, headers, b"").get("id")
    if not edit_id:
        raise StoreUnavailable(f"Play Console opened no edit for {package_name}.")

    try:
        version_codes: list = []
        # Bundles are what ships today; APKs are read too so a versionCode from
        # the app's APK era still counts against us — Play remembers those.
        for kind, key in (("bundles", "bundles"), ("apks", "apks")):
            listing = _call("GET", f"{base}/{edit_id}/{kind}", headers)
            version_codes += [item.get("versionCode") for item in listing.get(key) or []]
        accepted = _numbers(version_codes)
        return accepted, accepted
    finally:
        try:
            _call("DELETE", f"{base}/{edit_id}", headers)
        except StoreUnavailable as error:
            # An abandoned edit expires on its own and blocks nothing.
            print(f"::warning::Could not discard the Play edit: {error}", file=sys.stderr)


def report(seen: list[int], live: list[int], build: int | None) -> None:
    highest = max(seen, default=0)
    if build is None:
        print(highest)
        return
    print(f"highest={highest}")
    print(f"holds={'true' if build in live else 'false'}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", required=True, choices=("android", "ios"))
    parser.add_argument("--package-name", help="android: the Play Console package name")
    parser.add_argument("--play-key-file", help="android: service account JSON")
    parser.add_argument("--bundle-id", help="ios: the app's bundle id")
    parser.add_argument("--issuer-id", help="ios: App Store Connect issuer id")
    parser.add_argument("--key-id", help="ios: App Store Connect key id")
    parser.add_argument("--private-key-file", help="ios: App Store Connect .p8")
    parser.add_argument(
        "--build",
        type=int,
        help="the build number about to be pushed: also say whether the store already holds it",
    )
    args = parser.parse_args()

    if args.platform == "android":
        required = ("package_name", "play_key_file")
    else:
        required = ("bundle_id", "issuer_id", "key_id", "private_key_file")
    missing = [f"--{name.replace('_', '-')}" for name in required if not getattr(args, name)]
    if missing:
        parser.error(f"{args.platform} needs {', '.join(missing)}")

    try:
        if args.platform == "android":
            with open(args.play_key_file, encoding="utf-8") as handle:
                service_account = json.load(handle)
            seen, live = play_version_codes(args.package_name, service_account)
        else:
            with open(args.private_key_file, encoding="utf-8") as handle:
                private_key = handle.read()
            seen, live = appstore_builds(
                args.bundle_id, args.issuer_id, args.key_id, private_key, args.build
            )
        report(seen, live, args.build)
    except StoreUnavailable as error:
        print(f"::warning::{error}", file=sys.stderr)
        return 2
    except (OSError, KeyError, ValueError, ImportError) as error:
        print(f"::warning::Could not read the {args.platform} credentials: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
