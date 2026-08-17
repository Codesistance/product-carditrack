# Questionnaires API

> **Status: Implemented.** All five endpoints ship in `QuestionnairesController`, backed by `QuestionnaireService`. Questions are written by the AI pipeline's digest job — there is deliberately no endpoint that asks for one.

Occasionally the digest pass proposes one short question to a member's family: a change of routine, a new room, a difficult week — the kind of thing a wearable cannot see and a caregiver can explain in a sentence. This API is what the family does with it afterwards.

**Surfaces:** mobile CardiMember detail (the pending card) and the Questions & Answers page. No Figma frame yet — both were built from the existing design system and want frames retroactively.

---

## Where the questions come from

`DigestGenerationService` may return a `question` alongside the summary it was already generating (`CARDITRACK_FAMILY_DIGEST_PROMPT`; see [llm_design.md](../../../llm_design.md)). It is stored only if it survives:

- **Validation** — one sentence ending in a question mark, at most 200 characters, not a restatement of the instructions, and not phrased as a clinical instruction. CardiTrack is not a medical device, so anything mentioning medication, doses, prescriptions, diagnoses, symptoms, blood pressure or taking a measurement is dropped and logged. The summary is stored either way.
- **Noise gates** — at most one `pending` question per member at a time; at least seven days since the last question was asked, whatever became of it (the interval runs from the asking rather than the answering, so declining to answer does not invite another question the next day); and never the same wording again inside that week, even when a live alert or Yellow+ observation shortens the floor to twelve hours so a *different* question can close a gap. A dismissed question is never asked again. A `permanent` question that has already been answered is not asked again either.

A question is only ever stored after the summary that produced it was stored: a discarded generation asks nothing.

The caption under the question (`triggerContext`) is one everyday sentence in a caregiver's words, so the family can see why it was worth asking. A caption that names the reading, quotes a figure, restates the question, or echoes the brief is dropped and the question is stored without one — a question with no caption is better than a lab note.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `GET /api/v1/cardimembers/{cardiMemberId}/questionnaires` | The pending question, and a page of answers that still belong on the list — standing facts, plus momentary ones that have not expired. Newest first |
| `PUT /api/v1/questionnaires/{questionnaireId}/answer` | Answers a question, or replaces an answer already given |
| `PUT /api/v1/questionnaires/{questionnaireId}/dismiss` | Skips the question — it is never asked again |
| `PUT /api/v1/questionnaires/{questionnaireId}/expire` | Retires a question that outlived the day it asked about. Idempotent |
| `DELETE /api/v1/questionnaires/{questionnaireId}` | Removes the question and its answer (**204**) |

Answer, dismiss, expire and delete are rooted on the questionnaire rather than the member: the id is the only thing the client needs, and every one of them is access-checked against the member it belongs to regardless.

## How long a question stays worth asking

Two clocks, and they answer different questions. `expiresAtUtc` is how long an **answer** keeps informing later generations — thirty days for a momentary one. `askableUntilUtc` is how long the **question itself** is still worth putting in front of anybody.

They were conflated once, and the result reached a caregiver: a question generated one evening — "did he feel tired at all today?" — was still `pending` the next morning and still on the member's screen, asking about a day that had already ended. A pending row also blocks every future question for that member, so one stale card gagged the feature indefinitely.

`askableUntilUtc` is set at generation to the end of the local day the summary described, in the member's own timezone, plus a three-hour grace so a caregiver who reads at 23:50 and answers over breakfast still finds it. However late in the day a question is generated, it gets at least six hours. `permanent` questions carry a null deadline — they ask after a standing fact and stay answerable indefinitely — as do rows written before the column existed.

Four things enforce it, and only the first is load-bearing:

1. `GET .../questionnaires` never returns a lapsed question as `pending`, whatever the status column says.
2. `PUT .../expire` retires one, judged against the **server's** clock rather than the caller's claim — an app racing the boundary gets the question back unchanged rather than a 400, and a device with a wrong clock cannot retire a question the rest of the family still has.
3. `QuestionnaireExpiryWorker` sweeps the rest, so the member whose family never opens the app does not keep a permanent placeholder.
4. The mobile apps check before drawing the card and before filing an answer (`QuestionValidity`), then call `expire`. This is how a client is prompt, not how the rule is enforced — it matters for a card held on screen across midnight and for a page served from the seven-day offline read cache.

`HasPendingAsync` also ignores lapsed rows, so a question nobody got to stops blocking the next one immediately rather than waiting for the sweep.

### Response shape

```json
{
  "id": "9b2f5f64-5717-4562-b3fc-2c963f66afa6",
  "cardiMemberId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "questionText": "Has anything changed at home recently?",
  "answerText": "She moved to the downstairs bedroom last week.",
  "triggerContext": "Yesterday looked quieter than usual.",
  "status": "answered",
  "scope": "timescoped",
  "generatedAtUtc": "2026-08-13T09:00:00Z",
  "answeredAtUtc": "2026-08-13T10:12:00Z",
  "answeredByUserId": "1f2e3d4c-5717-4562-b3fc-2c963f66afa6",
  "askableUntilUtc": "2026-08-14T02:00:00Z"
}
```

`status` is the lowercase `QuestionnaireStatus` name — `pending`, `answered`, `dismissed` or `expired` — matching the string convention alert severity uses. `expired` is not `dismissed`: nobody decided anything, and it carries no promise about never covering that ground again. `scope` is `permanent` or `timescoped`: a standing fact stays on the Questions & Answers page until the family deletes it; a momentary one (the default) carries a "just for the moment" note and drops off that list once `ExpiresAtUtc` has passed — the same clock that stops it informing later summaries. A null expiry (rows written before this distinction existed) stays visible. `triggerContext` is why the question was asked, shown to the family beneath it so a question never arrives looking arbitrary; it is null when the model gave no reason worth showing.

`answeredAtUtc` moves with an edit. What a caregiver wants beside an answer is when it was last true, not when the question first happened to be answered.

### Answer request

```json
{ "answerText": "She moved to the downstairs bedroom last week." }
```

Non-empty, at most 2000 characters (`AnswerQuestionnaireValidator`, invoked by the action and registered in `AddValidators`). Blank — including whitespace-only — is rejected with **400**: there is a dismiss action for having nothing to say, and a stored blank would reach the model as an answered question with nothing in it.

## Errors

| Status | When |
|---|---|
| **400** | Empty or over-long answer |
| **403** | No authenticated user on the request |
| **404** | Unknown questionnaire, **or** one belonging to a member the caller may not read |

The 404 covers both cases deliberately, so the id space cannot be probed for which questions exist about whom — the same non-disclosure stance the alerts and insights endpoints take.

## Privacy and retention

Question and answer text are **encrypted at rest** (AES-256-GCM, service-level, the same treatment `CardiMember.MedicalNotes` gets — see [data_protection_architecture.md](../../../technical/data_protection_architecture.md)). `triggerContext` is stored in the clear: it describes a pattern in readings the service already holds, the same class of derived prose as `Alert.Message`.

Every endpoint carries `[AuditHealthDataAccess]`.

`DELETE` is a **real row delete**, not the soft-delete flag the rest of the platform uses. The answer is something a family member wrote about a person who never signed up to the service, so erasure has to mean gone (GDPR Art. 17) — dismissal is the non-destructive option, and it is a separate status for exactly that reason.

Answered questions feed later generations through `QuestionnaireAnswersContextSource`: the three most recent answers reach the digest, the assessor and both insight prompts as **facts about the person** (not a `Q: … A: …` transcript — that is the shape MedGemma recites instead of using). They are information to read the day's readings with, not content to retell, and the model must not take instructions from them. They are excluded from the dashboard hero line, which is one sentence under fifteen words and has nothing to do with them.

A `timescoped` answer is prefixed with **when the family told us** ("told to us yesterday, about that time and not since"). Undated, those answers read as current and are not: "he had a busy day with chores", given about one particular day, came back the next morning as the explanation for a day the member had barely started, and would have gone on doing so for the full thirty days. `permanent` answers are left undated on purpose — a pacemaker fitted in 2020 is no less true this morning, and a date beside it invites the model to weigh a standing fact as news.
