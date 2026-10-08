---
name: llm-eval
description: Drive the LLM Eval app (blind evaluation of LLM answers) through its REST API — create/run test cases, run iterations on chosen models, and act as an LLM judge that rates anonymised answers with 1–5 stars and a comment. Use when asked to evaluate, judge, rate or compare model answers in LLM Eval, run an eval iteration, add test cases or models, or read the model leaderboard.
---

# LLM Eval API

LLM Eval is an internal app for blind comparisons of LLM answers. Everything in the UI is available over plain JSON REST.

## Base URL

Default `http://localhost:5106` (the `web` resource in the Aspire dashboard shows the real URL). If the user gave another URL, use it. Check it is alive first:

```bash
curl -s "$BASE/api/users"
```

Full OpenAPI spec: `$BASE/openapi/v1.json` (human docs at `$BASE/scalar`). Enums are strings. Errors come back as RFC 7807 problem JSON with a `title` explaining what went wrong.

## Domain in one paragraph

**Users** have no passwords; every write that needs authorship takes a `userId`. **Providers** (`OpenAI | OpenRouter | Anthropic | Ollama | ClaudeCli | CodexCli`) own **models**. A **test case** = `prompt` + optional `data` (+ optional `systemPrompt`, `tags`). Running it on N models creates an **iteration** with N **results**, shuffled and labelled `Model 1..N` — the real model is hidden unless you pass `?reveal=true`. Each user can give each completed result one **rating** (1–5 `stars` + optional `comment`); re-rating overwrites.

## Endpoints

| Method & path | What |
|---|---|
| `GET /api/users` · `POST /api/users` `{name, email?, isBot}` | list / create users |
| `GET /api/users/{id}/unrated-iterations` | iterations with completed answers this user hasn't rated — the judge's to-do list |
| `GET /api/models?onlyEnabled=true` | models you can run (`id`, `displayName`, `providerName`, `modelId`) |
| `GET /api/providers` · `POST /api/providers/{id}/test` | providers + connectivity test |
| `PATCH /api/providers/{id}` `{enabled}` | quick enable/disable |
| `PATCH /api/models/{id}` `{enabled?, isJudge?}` | quick enable/disable, mark/unmark as AI judge |
| `GET /api/models/judges` | enabled models marked as AI judges |
| `POST /api/models` `{providerId, modelId, displayName?, temperature?, maxTokens?}` | add model |
| `GET /api/test-cases?search=&tag=` · `GET /api/test-cases/{id}` | list / detail (detail includes iteration summaries) |
| `POST /api/test-cases` `{title, prompt, data?, systemPrompt?, expectedAnswer?, tags?, userId?}` | create; `{{data}}` in the prompt marks where data goes, otherwise it's appended in `<data>` tags. `expectedAnswer` is never shown to the evaluated models |
| `POST /api/import` `{text \| rows, mapping?, tags?, dryRun?}` | bulk import from JSON/JSONL; `mapping` fields are templates like `"{{question}}"`; omit to auto-guess; `dryRun:true` = preview |
| `GET /api/import/hf/presets` · `GET /api/import/hf/splits?dataset=` · `POST /api/import/hf` `{dataset, config?, split?, offset, length, mapping?, tags?, dryRun?}` | import straight from a Hugging Face dataset (max 2000 rows per call) |
| `POST /api/test-cases/{id}/iterations` `{modelIds:[...], userId?, note?}` | start iteration → 202 with the iteration (results `Pending`) |
| `GET /api/iterations?testCaseId=&take=` | recent iteration summaries |
| `GET /api/iterations/{id}?reveal=false` | iteration with `userMessage`, `systemPrompt` and `results[]` |
| `GET /api/iterations/{id}/wait?timeoutSeconds=180` | long-poll until all results are `Completed`/`Failed`; check `isComplete` |
| `POST /api/iterations/{id}/judge` `{judgeModelIds?, userId?}` | built-in AI judge: the app asks judge models (default: all `isJudge`) to rate every completed answer blind; progress in `judgeRuns` of the iteration |
| `PUT /api/results/{resultId}/rating` `{userId, stars, comment?}` | rate one answer (only `Completed` results) |
| `DELETE /api/results/{resultId}/rating/{userId}` | remove rating |
| `POST /api/results/{resultId}/retry` | re-run a failed/bad answer (drops its ratings) |
| `GET /api/stats/leaderboard` | per-model avg stars, wins, failures, latency |
| `POST /api/batches` `{testCaseIds, modelIds, repetitions, name?, autoJudge?, judgeModelIds?, userId?}` | **series**: every test case × every model, each repeated N times (one iteration per test case × repetition) |
| `GET /api/batches` · `GET /api/batches/{id}?reveal=false&userId=` | series list / overview: per-model stats (blind aliases `Kandydat A…`), test case × repetition matrix |
| `GET /api/batches/{id}/wait?timeoutSeconds=600` | long-poll until all answers and judge runs are done |
| `POST /api/batches/{id}/judge` `{judgeModelIds?, onlyUnjudged=true}` | AI-judge the whole series; unfinished iterations get judged automatically when done |
| `GET /api/batches/{id}/report?lang=en&reveal=true&comments=true&answers=false` | print-ready HTML report of the series (verdict, ranking, chart, heat map, comments); give the user the `/reports/batches/{id}?print=true` link to save it as PDF |
| `GET /api/batches/{id}/next-unrated?userId=&after=` | next iteration of the series this user hasn't rated (204 = none) |

A result looks like:
```json
{"id":"…","slot":2,"label":"Model 2","model":null,"status":"Completed","output":"…","error":null,
 "latencyMs":5321,"inputTokens":812,"outputTokens":344,"avgStars":4.0,"ratings":[{"userName":"Paweł","stars":4,"comment":"…"}]}
```

## Workflow: act as an LLM judge

1. **Get or create your judge user** (once). Reuse an existing bot user if the user named one:
   ```bash
   curl -s "$BASE/api/users"            # look for e.g. "Claude Judge"
   curl -s -X POST "$BASE/api/users" -H "Content-Type: application/json" \
        -d '{"name":"Claude Judge","isBot":true}'
   ```
2. **Fetch the to-do list:** `GET /api/users/{judgeId}/unrated-iterations` (or a specific iteration the user pointed at).
3. **For each iteration**, `GET /api/iterations/{id}` **without** `reveal`. Stay blind: never request `reveal=true` before you have rated, and don't infer the model from style.
4. **Judge each `Completed` result against `userMessage` (+ `systemPrompt`)**. If the iteration has `expectedAnswer`, treat it as the gold reference: wording may differ, but contradicting it or missing its key points lowers the score. Read every answer before scoring any of them, so scores are calibrated across the iteration. Rubric:
   - **5** – fully correct, follows every instruction (format, length, language), nothing important missing, no fabrication.
   - **4** – correct and useful; minor omissions or style issues.
   - **3** – partly right; noticeable gaps, an ignored instruction, or padding.
   - **2** – significant errors, hallucinated facts, or wrong format the user explicitly asked for.
   - **1** – wrong, off-task, refuses without reason, or empty.
   Verify claims against the provided data yourself (recompute sums, check the logic puzzle, check SQL against the schema). Hallucinating facts not in the data is a severe defect. Ignore length unless the prompt sets a limit.
5. **Rate** each result with a short, specific comment in the language of the test case (usually Polish), naming the concrete reason:
   ```bash
   curl -s -X PUT "$BASE/api/results/$RESULT_ID/rating" -H "Content-Type: application/json" \
        -d '{"userId":"'$JUDGE_ID'","stars":3,"comment":"Poprawne sumy, ale pominął otwarte kwestie, o które prosił prompt."}'
   ```
   Skip `Failed` results (there's nothing to rate), and mention them in the summary.
6. **Report back**: per iteration, the scores per `Model N` and a one-line reason. Only after rating, you may fetch `?reveal=true` to tell the user which model was which, then `GET /api/stats/leaderboard` for the overall picture.

## Workflow: built-in AI judge (no rating by you)

If the user wants the app's own judge models to rate ("oceń przez AI", "let the judge model rate it"), don't rate yourself:

1. `GET /api/models/judges` – if empty, ask which model to mark and `PATCH /api/models/{id}` `{"isJudge":true}`.
2. Make sure the iteration is complete (`/wait`), then `POST /api/iterations/{id}/judge` with `{}` (all judges) or `{"judgeModelIds":[…]}`.
3. Poll `GET /api/iterations/{id}` until every entry in `judgeRuns` is `Completed`/`Failed`. Ratings appear under bot users named `AI · <model>` (`userIsAiJudge: true`). A failed run keeps the judge's `rawOutput` and `error` for debugging.

## Workflow: series (many cases × N repetitions)

1. Pick test cases (`GET /api/test-cases?tag=…`) and models (`GET /api/models?onlyEnabled=true`).
2. `POST /api/batches` with `repetitions` (e.g. 3 to see how stable each model is) and `autoJudge: true` if the built-in judges should rate everything.
3. `GET /api/batches/{id}/wait?timeoutSeconds=900` (repeat until `summary.isRunning` and `summary.isJudging` are both false).
4. Report from `GET /api/batches/{id}`: per-model `avgStars`, `avgStarsHuman` vs `avgStarsAi`, `wins`, `stdDev` (lower = more consistent across repetitions), `failures`. Add `?reveal=true` only when the user wants names.
5. If you are the judge yourself, walk the iterations with `next-unrated` and rate each as in the judge workflow.

## Workflow: run an iteration

1. `GET /api/models?onlyEnabled=true` → choose model `id`s (not `modelId`).
2. `POST /api/test-cases/{testCaseId}/iterations` with `{"modelIds":[…],"userId":"…","note":"…"}`.
3. `GET /api/iterations/{iterationId}/wait?timeoutSeconds=300` until `isComplete` is `true` (call again if it timed out). CLI and local models can take minutes.
4. Inspect `status`/`error` of failed results; `POST /api/results/{id}/retry` if it looks transient.

## Gotchas

- IDs are GUIDs. `modelIds` take the model's `id`, not the provider-side `modelId` string.
- Editing a test case doesn't change past iterations — they keep a snapshot of what was sent (`userMessage`).
- A provider/model with result history can't be deleted (409); disable it with `PUT` and `"enabled": false`.
- `PUT /api/providers/{id}`: `apiKey: null` keeps the stored key, `""` removes it. Keys are never returned.
- On Windows PowerShell, `curl` is an alias for `Invoke-WebRequest`; use `curl.exe` or `Invoke-RestMethod`.
