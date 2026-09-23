# Ollama 0.34.3 allows registry cross-host redirects "among allowlisted hosts" — the hf.co pull that broke our build now works upstream; we no longer need it

**Severity:** FYI — closes the open question in the 2026-09-21 rebuild brief
**Category:** dependencies

## Summary

Ollama v0.34.3 (2026-09-19) lists "Fix for model pulls from HuggingFace". The commit behind it is
PR #18533, "server: allow registry cross-host redirects among allowlisted hosts" (pdevine, merged
2026-09-19), fixing issue #18526 "Intermittent redirect failures when pulling HF models on 0.34.2".
That is precisely the failure the 2026-09-21 brief observed in our own CI: the CVE-2026-85180 fix
rejected the `huggingface.co` → `us.aws.cdn.hf.co` blob redirect with "blocked redirect to a
different host", and `ollama pull hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M` stopped working.

The 2026-09-21 brief's option 3 was "re-check whether Ollama offers a supported way to allow a
named redirect host … treat any such flag as re-opening the CVE". Upstream chose a middle path: not
a user flag, but a fixed allowlist of hosts among which a redirect is permitted. The PR page as
rendered in the sandbox does not enumerate the hosts, so whether the allowlist is "hf.co and its
CDN" or something broader is unverified here.

**No action for CardiTrack.** Since 2026-09-21 the MedGemma image pulls nothing: the two GGUFs are
vendored (`vendor-medgemma-weights.yml`, `weights.sha256`), the Dockerfile registers them with
`ollama create` from a Modelfile in git, and the base image is pinned to `ollama/ollama:0.34.2` by
digest. 0.34.3 changes nothing on that path. It only means the "cannot rebuild" condition would
also have cleared itself two days after we worked around it — worth knowing, not worth reverting
the vendoring, which removed a third-party registry from the build path for good.

Also checked this run: 0.34.3 and the 0.34.4-rc0 pre-release (2026-09-23) carry no security
fixes; the ollama/ollama repo still publishes no GitHub advisories; GHSA-57p7-34ff-7w3w still lists
no patched version (unchanged since 2026-09-03).

## Sources

- https://github.com/ollama/ollama/releases/tag/v0.34.3 (release notes, 2026-09-19 — read directly)
- https://github.com/ollama/ollama/pull/18533 (the change; fixes #18526 — read directly)
- https://github.com/ollama/ollama/compare/v0.34.2...v0.34.3 (six commits; #18533 is the only
  server-side one)
- `research/queue/2026-09-21-ollama-ssrf-fix-blocks-medgemma-image-rebuild.md`,
  `research/queue/2026-09-19-ollama-ssrf-blob-pull-redirect.md`

## Why flagged

The 09-21 brief left one question open — does Ollama offer a sanctioned redirect allowance, and
would using it re-open the SSRF? — and the answer arrived upstream this week. Recording it stops
the next reader re-deriving it, and pins down what the next base-image bump changes.

## Question to answer next

When the base image is next bumped past 0.34.2 (nothing forces it today), read the merged diff of
#18533 and note in `docs/technical/medgemma_serving_architecture.md` which hosts the allowlist
contains and whether it is reachable from `POST /api/pull` at runtime. Our runtime never pulls
(weights are baked in, ingress is OIDC-gated), so the answer should be "irrelevant", but it should
be written down once with the file path as evidence, the same way CVE-2026-65315 was handled.

claude "work through @research/queue/2026-09-23-ollama-0-34-3-allowlisted-cross-host-redirects-restores-hf-pulls.md"
