# GitHub Actions drops Node.js 20 runner support Sept 23, 2026 — CardiTrack verified unaffected

**Severity:** FYI
**Category:** dependencies

## Summary

GitHub is removing Node.js 20 from Actions runners on **2026-09-23** (pushed back from
an original Sept 16 date, updated 2026-08-25). Any action still declaring
`runs.using: node20` in its `action.yml` stops working outright after that date; the
`ACTIONS_ALLOW_USE_UNSECURE_NODE_VERSION` opt-out is removed on the same date.

## Why this matters to CardiTrack

Checked directly against the repo today: every action CardiTrack's workflows pin
(`actions/checkout@v6`, `actions/download-artifact@v8`, `actions/github-script@v8`,
`actions/setup-dotnet@v5`, `actions/setup-python@v7`, `actions/upload-artifact@v7`,
`r0adkll/upload-google-play@v1`, `apple-actions/upload-testflight-build@v5`) is already
on `node24` — confirmed for `apple-actions/upload-testflight-build@v5` by fetching its
`action.yml` directly. There is no `.github/actions/` directory, so there are no
first-party composite actions to check either. CardiTrack needs no action before the
deadline; reported for the audit trail and because this is a hard platform date that
would matter the moment a new action pin is added.

## Sources

- https://github.blog/changelog/2025-09-19-deprecation-of-node-20-on-github-actions-runners/ (official GitHub changelog)

## Next question

None outstanding for CardiTrack today. Re-check this only if a new third-party or
composite action is added to `.github/workflows/` before 2026-09-23.
