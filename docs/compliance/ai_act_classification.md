# EU AI Act — Risk Classification of CardiTrack's AI Surfaces

> **Status: DRAFT — pending review by regulatory counsel.** Prepared 2026-09-23 against `main`
> as merged that day, in answer to [issue #473](https://github.com/Codesistance/product-carditrack/issues/473).
> Like the [DPIA](dpia.md) and the [Art. 22 analysis](art22_alerting_analysis.md), every
> factual claim about the system cites the repository; every legal reading is a position for
> counsel to confirm or reject, marked `[DECISION REQUIRED]` where the DPIA's open-items table
> tracks it (**OI-15**). Nothing here is legal advice.

## 1. Why this document exists

Regulation (EU) 2024/1689 (the AI Act) classifies AI systems into tiers — prohibited (Art. 5),
high-risk (Art. 6 with Annexes I and III), limited-risk transparency duties (Art. 50), and
everything else — and attaches obligations to the tier. Until this document, nothing in the
repository recorded which tier CardiTrack's AI surfaces sit in: the DPIA uses "high-risk" only in
the GDPR Art. 35 sense, and the Art. 22 analysis covers automated decision-making under GDPR, not
the AI Act. The two regimes overlap in their facts (the same pipeline, the same human in the
loop) but not in their tests, so the same argument has to be made twice, once per regime.

### 1.1 Deadlines as understood on 2026-09-23

| Obligation | Applies from | Source |
|---|---|---|
| Art. 5 prohibited practices; Art. 4 AI literacy | 2 February 2025 | AI Act Art. 113(a) |
| Art. 50 transparency (interaction disclosure, marking of generated content) | 2 August 2026 | AI Act Art. 113 — **unchanged by the Omnibus** |
| Annex III stand-alone high-risk obligations | **2 December 2027** (was 2 August 2026) | Regulation (EU) 2026/1744 ("Digital Omnibus on AI"), in force 27 July 2026 |
| Annex I high-risk (AI in a product under harmonisation law, e.g. MDR) | **2 August 2028** (was 2 August 2027) | Regulation (EU) 2026/1744 |

The Omnibus dates are taken from the issue thread and from
`research/queue/2026-09-04-eu-ai-act-digital-omnibus-postponement.md` (Council press release,
2026-06-29). **Verify against the Official Journal text at sign-off** — the drafting environment
could not reach EUR-Lex. Issue #473's original "deadline that has now passed" framing is
superseded: if either alerting surface is ultimately high-risk, the compliance runway is to
December 2027, and prod does not run that surface today (§3).

## 2. Roles: who is the provider

CardiTrack integrates two models into systems it designs, names and operates:

- **MedGemma**, an open-weight Google model self-hosted on Cloud Run in the estate
  (`infrastructure/environments/*.tfvars` `medgemma_service_url`), used only through prompting —
  no fine-tuning, no weight change (`docs/llm_design.md`).
- **Gemini** on Vertex AI (public and Rewrite slots, DPIA §4.3), a general-purpose AI model
  supplied by Google.

Under Art. 3(3) the *provider* of an AI system is whoever develops it and places it on the market
or puts it into service under their own name. CardiTrack does that for every surface in §3: the
prompts, the routing, the parsers and the products around them are CardiTrack's. So **CardiTrack
is the provider (and the deployer) of each AI system below**. The general-purpose-model
obligations (Chapter V) sit with Google as the model providers; CardiTrack does not become a GPAI
model provider by prompting an unchanged model. The provider *entity* is whichever legal entity
DPIA **OI-3** settles on.

## 3. Inventory: which parts are AI systems

Art. 3(1) defines an AI system by inference from input to output with a degree of autonomy.
Rule-based arithmetic on human-set thresholds is generally outside that definition (Commission
guidelines on the definition of an AI system, February 2025 — counsel to confirm). Mapping the
DPIA's inventory:

| DPIA row | Surface | Model(s) | Environment today | AI system? |
|---|---|---|---|---|
| A13 | Real-time heart-rate assessment — SSA deviation score, MedGemma severity verdict, red/orange → caregiver `Alert` + push | MedGemma | **dev only** (`enable_pipeline_jobs`) | Yes — the surface most likely to be argued high-risk |
| A15 | Statistical findings judgement — nine deterministic rules produce findings; **since 2026-09-19 MedGemma returns the severity, headline and message** that become the alert row | MedGemma | **dev only** (moved from the Worker to the pipeline's `assess` job) | Yes — the model is now in the severity path of the main producer, not a sidecar |
| A14 | Inactivity detection — device silence > 2 h, fixed rule | none | every environment (Worker) | No — deterministic rule |
| R2 | Caregiver-defined alarms — threshold arithmetic on numbers the caregiver chose | none | every environment | No — deterministic; the human set the rule |
| A11 | Family summary (headline + body), two-slot clinical read + rewrite | MedGemma + Gemini | dev | Yes — generated text shown to caregivers |
| A19 | Family questionnaire question, blocklist-validated | MedGemma + Gemini | dev | Yes — generated text |
| A22 | Advise — "Something to try" | MedGemma + Gemini | dev | Yes — generated text |
| A24 | Dashboard status line | MedGemma + Gemini | dev | Yes — generated text |
| A20 | Member chat — multi-turn, incl. the `settings` workflow that can switch alert rules and alarms on the caregiver's explicit yes | MedGemma + Gemini | dev | Yes — **the one surface that interacts directly with a natural person** (Art. 50(1)) |
| A6 | Report narrative (PDF), chat-transcript export | Gemini (narrative); no model for transcript | dev and prod | Yes (narrative) |

**Prod runs none of the MedGemma surfaces**: `infrastructure/environments/prod.tfvars` has
`enable_pipeline_jobs = false` and grants no MedGemma invoker; the Vertex Gemini public slot is
configured and serves A6. The classification below is written for the system as built in dev,
because that is the system prod will run when the flag flips — and **the flip is the event this
classification must precede** (DPIA §13 trigger added 2026-09-23).

## 4. Art. 5 — prohibited practices

Checked and not engaged: no subliminal or manipulative techniques (5(1)(a)); no exploitation of
the vulnerabilities of age or disability to distort behaviour (5(1)(b)) — the wearers are often
elderly, but no surface addresses the wearer, and every output informs a caregiver; no social
scoring (5(1)(c)); no criminal-risk profiling, facial scraping, workplace emotion recognition,
biometric categorisation or remote biometric identification (5(1)(d)–(h)). Heart rate, steps,
sleep and rhythm data are physiological, not biometric identification data as the Act uses the
term (no identification or categorisation of a person from them).

## 5. Annex I — AI as a safety component of a regulated product

Art. 6(1) makes an AI system high-risk when (a) it is, or is a safety component of, a product
covered by the Union harmonisation legislation in Annex I — the Medical Device Regulation
(EU) 2017/745 is listed — **and** (b) that product must undergo third-party conformity
assessment under that legislation.

This route is entirely downstream of DPIA **OI-2** (medical-device classification, EU MDR
Rule 11). If CardiTrack's early-warning claims make it a device of Class IIa or above, its
severity-routing AI is Annex I high-risk with an application date of 2 August 2028 and a
conformity route that runs through the device's own notified body. If OI-2 concludes it is not
a device (general wellness positioning, "not a medical device", `docs/solution_manifest.md`),
Annex I does not apply. The 2026-09-22 rhythm ingestion (DPIA A26 — ECG classifications and
irregular-rhythm notifications from Google-classed SaMD features) does not by itself make
CardiTrack a device, but it sharpens OI-2, and this document inherits whatever OI-2 decides.

**Position recorded:** *Annex I follows OI-2. No separate decision is taken here.*

## 6. Annex III — stand-alone high-risk use cases

Annex III has no general "health" category. Going through it:

| Annex III area | Engaged? | Reasoning |
|---|---|---|
| 1. Biometrics (identification, categorisation, emotion recognition) | No | See §4 — no biometric identification or categorisation is performed |
| 2. Critical infrastructure | No | — |
| 3. Education and vocational training | No | — |
| 4. Employment and worker management | No | — |
| 5(a). Public-authority eligibility for benefits and healthcare services | No | CardiTrack is not used by or on behalf of a public authority to evaluate eligibility |
| 5(b). Creditworthiness | No | — |
| 5(c). Risk assessment and pricing for **life and health insurance** | No **today** | No insurer is a customer or recipient. This flips the moment CardiTrack data or verdicts are sold to or shared with an insurer for pricing or risk assessment |
| 5(d). Evaluating emergency calls, dispatching or prioritising emergency first response, **emergency healthcare patient triage** | **No — the closest category, argued below** | — |
| 6. Law enforcement | No | — |
| 7. Migration, asylum, border | No | — |
| 8. Administration of justice and democratic processes | No | — |

### 6.1 Annex III point 5(d) applied to A13 and A15

The severity-routing chain classifies a member's window or finding as green / yellow / orange /
red and, for red and orange, writes an `Alert` row and pushes it to the member's family
caregivers, with a 120 s / 300 s / 900 s escalation ladder that widens *which caregivers* are
notified and ends in an `UNDELIVERED_CRITICAL` state (DPIA A18; `art22_alerting_analysis.md` §1).
Against point 5(d):

- **It does not evaluate or classify emergency calls.** No call, message or request from a
  person is scored; the input is wearable telemetry.
- **It does not dispatch or prioritise emergency first-response services.** No integration
  with any ambulance, police, fire, telecare or alarm-receiving centre exists; the recipients are
  named family caregivers who registered a device token (`PushDeviceTokens`). Whether anyone
  calls emergency services is that human's decision, made with context the system lacks
  (`art22_alerting_analysis.md` §2).
- **It is not an emergency healthcare patient triage system.** There is no healthcare provider,
  no queue of patients awaiting care, and no allocation of clinical resources. The product is a
  family wellness monitor that disclaims diagnosis at every layer (`MedicalPromptBlocks.Tone`,
  `/privacy`, the Advise footnote, the report banners).

**Position recorded:** *as built, A13 and A15 are not Annex III high-risk. The remaining AI
surfaces (A11, A19, A20, A22, A24, A6) generate text for a caregiver and sit in no Annex III
area at all.* `[DECISION REQUIRED — OI-15]` counsel to confirm.

### 6.2 Why this document does not rely on Art. 6(3)

Art. 6(3) lets a system listed in Annex III escape high-risk status when it performs a narrow
procedural task, improves the result of a completed human activity, detects deviation from prior
decision patterns, or is preparatory to an assessment — **but never where the system performs
profiling of natural persons**. The Art. 22 analysis (§2) concedes that the baselines and
assessments are automated profiling under GDPR Art. 4(4). So if counsel were to find the chain
*inside* point 5(d), 6(3) would not take it out again. The argument in §6.1 is therefore "not in
Annex III", and it has to hold on its own.

### 6.3 What would change the answer

Any of the following re-opens §6 (mirrored in DPIA §13):

- an integration that sends alerts, verdicts or member data to an emergency service, telecare
  provider, alarm-receiving centre or care facility's clinical staff (5(d)) — note DPIA OI-7's
  "POA model for facilities";
- data or verdicts supplied to an insurer for risk assessment or pricing (5(c));
- OI-2 concluding CardiTrack is a medical device (Annex I, §5);
- the model deciding *what is done* rather than *who is told* — e.g. SMS dispatch to a
  non-caregiver, automatic escalation to a third party, or any effect on service, price or
  entitlement (the same line the Art. 22 analysis draws);
- a new Annex III entry by delegated act (Art. 7).

## 7. Obligations that apply now, whatever §5 and §6 conclude

### 7.1 Art. 50 — transparency

Art. 50 attaches to function, not tier, and has applied since 2 August 2026. Two paragraphs
matter here:

- **50(1)** — a system intended to interact directly with natural persons must be designed so
  the person is informed they are interacting with an AI system, unless obvious from context.
  **Member chat (A20) is squarely in scope.**
- **50(2)** — providers of systems generating synthetic text must ensure the output is marked in
  a machine-readable format and detectable as artificially generated, with exemptions for
  assistive editing that does not substantially alter the input. Whether the summary, Advise,
  status-line and alert-message surfaces fall under 50(2), and what "machine-readable marking"
  means for short in-app text, is a question for counsel; the safe product posture is the same
  either way — say it is AI-written where it is shown, and carry an `AI-generated` marker in the
  API response that serves it.

Survey of the user-facing surfaces on 2026-09-23:

| Surface | Disclosure at the point of use | Where |
|---|---|---|
| Member chat sheet | **None.** Empty state says "Ask anything about their readings"; the reply bubble carries a bot mark but no words | `src/Presentation/CardiTrack.Mobile/MemberChatPage.xaml` |
| Advise card ("Something to try") | Partial. Fixed footnote "just a suggestion, never medical advice — worth mentioning to their doctor" — safety framing, but not that it is AI-written | `CardiMemberDetailPage.xaml.cs` |
| Family summary card | **None** found | `CardiMemberDetailPage.xaml` |
| Alert detail (headline, message, narrative card) | **None** — and since 2026-09-19 the headline and message are model-written for every R1 alert | `AlertDetailPage.xaml` |
| Dashboard status line | **None** found | Dashboard hero card |
| PDF report narrative; chat-transcript export | **Present** — "Written by CardiTrack's AI assistant. Not a clinical assessment." / "AI-generated answers · Not a clinical assessment" | `PdfReportRenderer.cs`, `ChatTranscriptDocument.cs` |
| `/privacy` | Describes "a medical language model writes a short assessment" — policy-level, not the product-surface disclosure 50(1) asks for | `Privacy.razor` |

**Gap recorded:** five in-app surfaces need one fixed line of chrome each, matching what the
PDF already does. This is copy, not logic, and it is tracked as
[issue #1244](https://github.com/Codesistance/product-carditrack/issues/1244). It is the only
AI Act obligation on CardiTrack that is **live and unmet** today.

### 7.2 Art. 4 — AI literacy

Providers and deployers must ensure a sufficient level of AI literacy among staff and other
persons operating AI systems on their behalf. Nothing in the repository records this. For a
founder-operated product the measure is small — a dated note of who operates the pipeline and
what they have read (`docs/llm_design.md`, the algorithm card, this document) — but it should
exist. **Gap recorded; owner action, no code.**

### 7.3 What does *not* apply unless §5 or §6 flips

Chapter III Section 2 (risk management system, data governance, technical documentation per
Annex IV, logging, human oversight design, accuracy/robustness), the Art. 43 conformity
assessment, registration in the EU database (Art. 49) and post-market monitoring (Art. 72) are
high-risk obligations. They are **not** owed on the position in §6.1. Much of their substance
nonetheless already exists for GDPR reasons and should be kept current, because it is what
would be reused if the answer changed: the algorithm card (technical documentation), stored
assessments with model input, output and engine stamps (logging), the caregiver acknowledgment
lifecycle (human oversight), and the V1–V4 validation protocol (accuracy).

## 8. Decisions required

| ID | Decision | Position taken in this draft | Owner |
|---|---|---|---|
| OI-15 (a) | Are A13 / A15 Annex III high-risk (point 5(d))? | **No** — §6.1 | Owner + regulatory counsel |
| OI-15 (b) | Annex I status | Follows OI-2; no separate decision | Owner + regulatory counsel |
| OI-15 (c) | Do the text-generating surfaces fall under Art. 50(2), and what marking satisfies it? | Treat as yes; disclose in-product and mark in the API | Owner + counsel |
| OI-15 (d) | Art. 50(1) disclosure copy on the five surfaces in §7.1 | Ship it (Mobile issue) | Owner |
| OI-15 (e) | Art. 4 literacy record | Write it | Owner |

Until OI-15 (a) is confirmed, the product must make **no AI Act compliance representation** in
the privacy policy, terms of service, or the Google for Startups narrative — the same rule #473
states. Neither document makes one today.

## 9. Relationship to the Art. 22 analysis

The two documents share their facts and their central argument — the caregiver is the
decision-maker, the system informs — but answer different questions: Art. 22 asks whether a
*decision* is solely automated with significant effect; Annex III asks whether the *system* is
of a listed kind. A change that moves one usually moves the other, so both carry the same
re-run triggers, and the Art. 22 re-run of 2026-09-23 (`art22_alerting_analysis.md` §2.1) was
written alongside this document.

## 10. Sources

- Regulation (EU) 2024/1689 (AI Act): Arts. 3, 4, 5, 6, 7, 50, 113; Annexes I and III.
- Regulation (EU) 2026/1744 (Digital Omnibus on AI), in force 27 July 2026 — dates per
  `research/queue/2026-09-04-eu-ai-act-digital-omnibus-postponement.md` and issue #473's thread;
  verify against the Official Journal at sign-off.
- Commission guidelines on the definition of an AI system and on prohibited practices
  (February 2025) — for the rule-based-system boundary in §3.
- Repository: [dpia.md](dpia.md) rows A6, A11, A13–A15, A18–A20, A22, A24, A26 and open items
  OI-2, OI-3, OI-7; [art22_alerting_analysis.md](art22_alerting_analysis.md);
  [alerting_algorithm_card.md](alerting_algorithm_card.md); `docs/llm_design.md`;
  `infrastructure/environments/dev.tfvars`, `prod.tfvars`.
