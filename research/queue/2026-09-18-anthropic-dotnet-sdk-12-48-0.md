# Anthropic .NET SDK now at 12.48.0 vs CardiTrack's pinned 12.46.0 — non-breaking retry/Retry-After fixes

**Date:** 2026-09-18
**Severity:** FYI
**Category:** dependencies
**Source:** https://github.com/anthropics/anthropic-sdk-csharp/blob/main/src/Anthropic/CHANGELOG.md

## Summary

v12.47.0 (2026-09-10) and v12.48.0 (2026-09-15) changed retry semantics: the async client now retries connection errors and client-side timeouts, and `Retry-After` handling was tightened (out-of-range values ignored, values >60s honoured). No CVEs or breaking changes found in this range.

## Why flagged

Retry-behaviour changes matter for whatever CardiTrack service calls the Claude API from .NET (the AI ingestion pipeline), especially around Cloud Run timeout/backoff assumptions.

## Next question / action

Identify which CardiTrack project references the `Anthropic` NuGet package and whether it has seen transient timeout/retry failures that bumping to 12.48.0 would address.
