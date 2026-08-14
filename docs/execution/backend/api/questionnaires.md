# Questionnaires API

> **Status: Implemented.** All four endpoints ship in `QuestionnairesController`, backed by `QuestionnaireService`. Questions are written by the AI pipeline's digest job — there is deliberately no endpoint that asks for one.

Occasionally the digest pass proposes one short question to a member's family: a change of routine, a new room, a difficult week — the kind of thing a wearable cannot see and a caregiver can explain in a sentence. This API is what the family does with it afterwards.

**Surfaces:** mobile CardiMember detail (the pending card) and the Questions & Answers page. No Figma frame yet — both were built from the existing design system and want frames retroactively.

---

## Where the questions come from

`DigestGenerationService` may return a `question` alongside the summary it was already generating (`CARDITRACK_FAMILY_DIGEST_PROMPT`; see [llm_design.md](../../../llm_design.md)). It is stored only if it survives:

- **Validation** — one sentence ending in a question mark, at most 200 characters, not a restatement of the instructions, and not phrased as a clinical instruction. CardiTrack is not a medical device, so anything mentioning medication, doses, prescriptions, diagnoses, symptoms, blood pressure or taking a measurement is dropped and logged. The summary is stored either way.
- **Two noise gates** — at most one `pending` question per member at a time, and at least seven days since the last question was asked, whatever became of it. The interval runs from the asking rather than the answering, so declining to answer does not invite another question the next day.

A question is only ever stored after the summary that produced it was stored: a discarded generation asks nothing.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `GET /api/v1/cardimembers/{cardiMemberId}/questionnaires` | Every question asked about this member, newest first, whatever its status |
| `PUT /api/v1/questionnaires/{questionnaireId}/answer` | Answers a question, or replaces an answer already given |
| `PUT /api/v1/questionnaires/{questionnaireId}/dismiss` | Skips the question — it is never asked again |
| `DELETE /api/v1/questionnaires/{questionnaireId}` | Removes the question and its answer (**204**) |

Answer, dismiss and delete are rooted on the questionnaire rather than the member: the id is the only thing the client needs, and every one of them is access-checked against the member it belongs to regardless.

### Response shape

```json
{
  "id": "9b2f5f64-5717-4562-b3fc-2c963f66afa6",
  "cardiMemberId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "questionText": "Has anything changed at home recently?",
  "answerText": "She moved to the downstairs bedroom last week.",
  "triggerContext": "Sleep has been shorter than usual all week.",
  "status": "answered",
  "generatedAtUtc": "2026-08-13T09:00:00Z",
  "answeredAtUtc": "2026-08-13T10:12:00Z",
  "answeredByUserId": "1f2e3d4c-5717-4562-b3fc-2c963f66afa6"
}
```

`status` is the lowercase `QuestionnaireStatus` name — `pending`, `answered` or `dismissed` — matching the string convention alert severity uses. `triggerContext` is why the question was asked, shown to the family beneath it so a question never arrives looking arbitrary; it is null when the model gave no reason.

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

Answered questions feed later generations through `QuestionnaireAnswersContextSource`: the three most recent answers reach the digest, the assessor and both insight prompts, framed as information about the person the model must not take instructions from. They are excluded from the dashboard hero line, which is one sentence under fifteen words and has nothing to do with them.
