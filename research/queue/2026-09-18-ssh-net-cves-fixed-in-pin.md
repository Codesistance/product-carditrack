# SSH.NET: two HIGH CVEs (path-traversal, command-injection via ScpClient) fixed exactly at CardiTrack's pinned 2026.0.0 — no action needed

**Date:** 2026-09-18
**Severity:** FYI
**Category:** dependencies
**Source:** https://github.com/advisories/GHSA-q939-rpr3-3284

## Summary

CVE-2026-48798 (CVSS 7.1, ScpClient path-traversal arbitrary file write) and CVE-2026-85756 (CVSS 7.5, ScpClient OS command injection via unsafe remote-path quoting) both affect SSH.NET ≤2025.1.0 and are fixed in 2026.0.0 — the exact version CardiTrack has pinned.

## Why flagged

First-time check of this dependency; confirms the pin is already at the fixed floor, not an older vulnerable version.

## Next question / action

Confirm whether any CardiTrack code path uses `ScpClient` (vs `SftpClient`) against a shell-based server, and if so, that `RemotePathTransformation.ShellQuote` is explicitly set.
