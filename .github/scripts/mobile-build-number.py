#!/usr/bin/env python3
"""Derive a store build number from a release tag.

Play Console's versionCode and TestFlight's CFBundleVersion must both rise with
every upload, and neither store ever forgets a number: once one has been used it
is spent for the life of the app.

Until now both were ``git rev-list --count`` — the commit count at the tag. That
is not a function of the tag, which is what breaks it: two tags minted at the
same commit get the same number, and the second can never be pushed to either
store. It had happened seven times by the time this was written, most recently
v0.2.312 and v0.2.313, both sitting on 63e3de08 and both claiming 1538.

The number now comes from the tag alone::

    MAJOR * 10_000_000 + MINOR * 10_000 + PATCH

Release tags only ever increase — deploy-apps-dev.yml bumps the patch,
bump-version.yml the minor or major — so the encoding only ever increases too,
and a tag names exactly one build number however many commits precede it and
whichever commit it sits on.

The radix is what keeps that true, so it is checked rather than assumed: a minor
past 999 or a patch past 9999 would carry into the field above and let a later
tag produce a smaller number than an earlier one. Play caps versionCode at
2_100_000_000, which this encoding reaches at major 210.
"""

from __future__ import annotations

import argparse
import re
import sys

TAG_PATTERN = re.compile(r"^v(\d+)\.(\d+)\.(\d+)$")

MAJOR_SCALE = 10_000_000
MINOR_SCALE = 10_000

# Past these the fields overlap and the result stops being monotonic.
MAX_MINOR = 999
MAX_PATCH = 9_999

# Play Console rejects a versionCode above this.
MAX_VERSION_CODE = 2_100_000_000

# The highest commit-count build number that ever reached a store: v0.2.308,
# which both stores hold. Every tag-derived number has to clear it, or an upload
# would be refused as a duplicate of something shipped under the old scheme.
# v0.2.309 and v0.2.310 were tagged under that scheme too but never reached a
# store — 310's Android build failed on a XAML parse error — so 1524 stands as
# the high-water mark. Both stores confirmed it when they rejected a re-push of
# 1524 on 2026-09-18.
LEGACY_MAX_BUILD_NUMBER = 1524


class TagError(ValueError):
    pass


def build_number(tag: str) -> int:
    match = TAG_PATTERN.match(tag.strip())
    if not match:
        raise TagError(f"'{tag}' is not a release tag (expected vMAJOR.MINOR.PATCH).")

    major, minor, patch = (int(part) for part in match.groups())

    if minor > MAX_MINOR:
        raise TagError(
            f"Minor version {minor} in {tag} is above {MAX_MINOR}: it would carry into "
            f"the major field and let a later tag produce a lower build number. Widen "
            f"MINOR_SCALE (and every store's history) before tagging this."
        )
    if patch > MAX_PATCH:
        raise TagError(
            f"Patch version {patch} in {tag} is above {MAX_PATCH}: it would carry into "
            f"the minor field and let a later tag produce a lower build number. Widen "
            f"MAJOR_SCALE/MINOR_SCALE (and every store's history) before tagging this."
        )

    number = major * MAJOR_SCALE + minor * MINOR_SCALE + patch

    if number <= LEGACY_MAX_BUILD_NUMBER:
        raise TagError(
            f"{tag} derives build number {number}, which does not clear the last "
            f"commit-count build shipped ({LEGACY_MAX_BUILD_NUMBER}). Both stores "
            f"would reject it as a duplicate."
        )
    if number > MAX_VERSION_CODE:
        raise TagError(
            f"{tag} derives build number {number}, above Play Console's versionCode "
            f"ceiling of {MAX_VERSION_CODE}."
        )

    return number


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True, help="Release tag, e.g. v0.2.318")
    args = parser.parse_args()

    try:
        print(build_number(args.tag))
    except TagError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
