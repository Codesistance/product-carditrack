# GitHub Actions: Node 20 runner removal (~3 weeks out)

**Severity:** CRITICAL
**Category:** dependencies

## Summary

GitHub Actions runners are removing Node 20 support; an August 25, 2026 editor's note on GitHub's
changelog post moved the removal date, with secondary sources reporting **23 September 2026** as
the current target (the exact date could not be independently reconfirmed this run — GitHub's own
changelog domain was egress-blocked from this sandbox; verify directly before treating the date as
final). Actions still requiring Node 20 will fail to run on runners after removal unless
`ACTIONS_ALLOW_USE_UNSECURE_NODE_VERSION=true` is set, which is a workaround, not a fix.

## Source links

- https://github.blog/changelog/2025-09-19-deprecation-of-node-20-on-github-actions-runners/
  (GitHub's own changelog — primary, but could not be directly fetched this run; verify the current
  removal date directly)

## Why flagged

CardiTrack's CI pins `actions/checkout@v6` and `actions/setup-dotnet@v5`, both older majors;
`actions/checkout@v7` and `actions/setup-dotnet@v6` are already released and confirmed Node
24-based. This is a dated, low-effort, high-consequence fix: every deploy workflow
(`deploy-apps-dev.yml`, `deploy-apps-prod.yml`, and the rest of `.github/workflows/`) depends on
checkout succeeding, so a missed bump breaks CI outright once the removal lands — inside three
weeks of this run.

## Question to answer next

Bump `actions/checkout` to v7 and `actions/setup-dotnet` to v6 across `.github/workflows/*.yml`,
confirm the remaining pinned actions (`dorny/paths-filter@v4`, `google-github-actions/*@v3`,
`hashicorp/setup-terraform@v4`, `apple-actions/upload-testflight-build@v5`,
`r0adkll/upload-google-play@v1`) are already Node 24-capable at their current pins, and re-verify the
exact removal date directly against GitHub's changelog before treating this as done.
