# GitHub moves `ubuntu-latest` to Ubuntu 26.04 in a phased rollout from 2026-10-19 to 2026-11-19 — all 60 CardiTrack jobs ride that label, none pins a version

**Severity:** HIGH
**Category:** dependencies
**Date recorded:** 2026-09-26

## Summary

GitHub announced on 2026-09-17 that the `ubuntu-latest` runner label will move from Ubuntu 24.04 to
Ubuntu 26.04 in a phased rollout starting **2026-10-19** and expected to finish by **2026-11-19**.
During the window a job on the label may land on either image. The announcement's tool table shows
matching tool versions on both images (OS 24.04.5 → 26.04.1, kernel 6.17 → 7.0, systemd 255 → 259)
and lists no removals, but warns that anything relying on 24.04-specific system libraries or prebuilt
binaries may break.

**Where CardiTrack stands.** `grep -rho 'runs-on: ubuntu-latest' .github/workflows | wc -l` is 60
and no workflow pins `ubuntu-24.04` or `ubuntu-26.04`. The affected jobs include: every job in
`_env.yml`; the 30 jobs in `deploy-apps-dev.yml`, including "Build Mobile (Android)", which runs
`dotnet workload install maui-android` against the image's JDK and Android SDK; the Testcontainers
-backed test jobs, which use the image's Docker; every `deploy-infra-*.yml` and
`deploy-medgemma-common.yml` job using the image's gcloud; `deploy-mobile-dev.yml`,
`vendor-medgemma-weights.yml` and `post-digest.yml`. The iOS job runs on `macos-26` and is not
affected. Android builds use `-p:RunAOTCompilation=false`, so the NDK 28 removal / NDK 30 addition on
2026-10-01 (runner-images #14745) does not bite either.

Companion notices in the same announcement stream: Ubuntu 26.04 and 26.04-arm64 images are GA
(#14747, 2026-09-17); Ubuntu 22.04 images began deprecation 2026-09-17 with retirement 2027-04-17
(#14254) — CardiTrack does not use `ubuntu-22.04`.

## Sources

- https://github.com/actions/runner-images/issues/14748 — the rollout announcement (read directly via WebFetch; also linked from the Ubuntu 24.04 image README on raw.githubusercontent.com)
- https://github.com/actions/runner-images/issues/14747 — Ubuntu 26.04 image GA (indexed)
- https://github.com/actions/runner-images/issues/14254 — Ubuntu 22.04 deprecation (indexed)
- https://github.com/actions/runner-images/issues/14745 — NDK 28 removal 2026-10-01 (read directly)

## Why flagged

A dated migration window on the runner every non-iOS CardiTrack job uses, starting in 23 days, with
no explicit pin anywhere. The failure mode is intermittent — the same commit can land on either image
for a month — which is the most expensive kind to debug in a deploy pipeline. HIGH rather than
CRITICAL because GitHub lists no tool removals and the fix is a one-line pin.

## Question to answer next

1. Pin now or adopt now? Option A: change every `runs-on: ubuntu-latest` to `ubuntu-24.04` before
   2026-10-19 (zero risk until 24.04's own retirement, likely 2028). Option B: run one full dev
   pipeline (`deploy-apps-dev.yml`, including the Android build and the Testcontainers tests) on
   `ubuntu-26.04` this week and, if green, pin to `ubuntu-26.04` deliberately.
2. Either way, add the chosen label to `_env.yml`'s outputs so there is one place to change it next
   time.

claude "work through @research/queue/2026-09-26-github-ubuntu-latest-moves-to-26-04-from-2026-10-19.md"
