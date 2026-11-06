# Express Repair — Call → Lead demo

A voicemail webhook comes in, a background worker transcribes it and extracts a structured lead with an LLM, and the leads page updates live.
Stack: .NET 8, ASP.NET Core (Razor Pages + API controllers), EF Core + SQLite, SignalR, Bootstrap 5.

## Run it (mocks, no keys)

```bash
dotnet run --project src/ExpressRepair.Leads
```

Open **http://localhost:5080** (or press F5 in Visual Studio).

1. Pick a sample recording and click **Simulate incoming call**. The browser sends a form POST to the real webhook `POST /api/calls/recording`, and the panel shows its status code and response time.
2. A row appears as **Processing**. About 3 seconds later it switches to **Done** (or **Failed**) by itself, through SignalR. Click a row to see the transcript and details.
3. *Garbled recording*: the mock LLM answers in prose instead of JSON. The extractor asks once more, then marks the call **Failed** with the reason.
4. *Transcription outage*: the mock throws a 503 on the first attempt, so the call is **Failed**. Click **Retry** and it ends **Done**. The mock keeps this "first attempt" state in memory, so after an app restart the first retry fails once more.

The SQLite database `calls.db` is created on first run. Delete it to start with an empty table.
The project targets `net8.0`, with `RollForward=Major` so it also runs on a machine that only has a newer runtime.

You can also send the task's sample payload as JSON:
```bash
curl -X POST http://localhost:5080/api/calls/recording -H "Content-Type: application/json" -d "{\"CallSid\":\"CA1a2b3c4d5e6f7890\",\"From\":\"+14165550123\",\"To\":\"+16475550199\",\"RecordingSid\":\"RE9f8e7d6c5b4a3210\",\"RecordingUrl\":\"https://example.com/recordings/RE9f8e7d6c5b4a3210.mp3\",\"RecordingDuration\":\"47\"}"
```

## Using real AI (OpenAI)

In `appsettings.json` set `Ai:LlmProvider` and/or `Ai:TranscriptionProvider` to `OpenAI`. Put the key outside the repo:

```bash
dotnet user-secrets set "OpenAI:ApiKey" "sk-..." --project src/ExpressRepair.Leads
```

You can also use the environment variable `OpenAI__ApiKey`. Keys are never committed: `appsettings.json` has no key field, and `.gitignore` excludes local config files.
If a provider is set to OpenAI and no key is configured, the app refuses to start and tells you why.
The two switches are separate so you can try the real LLM with mock transcription; the demo recording URLs don't point to real audio.
The real clients have not been run against OpenAI, since this task provides no keys.

## Design decisions

- **Webhook accepts form-encoded data** (Twilio's real format; its request signature is calculated over the form fields). It also accepts JSON, because the task's sample is JSON and it's handy for curl. Both formats go through the same code.
- **The webhook only saves and enqueues.** It stores the call as Processing, puts the id on an in-memory `Channel<Guid>`, and returns 200 in milliseconds. It makes no AI calls.
- **Idempotent webhook:** a unique index on `RecordingSid`. A repeated delivery returns 200 with the existing id instead of creating a second lead.
- **`BackgroundService` worker:**
  - creates a new DI scope per call, because `DbContext` is scoped and the worker is a singleton;
  - processes 2 calls at a time (`Processing:MaxConcurrency`);
  - on startup, re-queues calls still in Processing, since the in-memory queue is lost on restart.
- **Errors never crash the app.** Each call has one deadline (`Processing:TimeoutSeconds`). Any exception, timeout or bad LLM output ends as **Failed** with a readable error. A last catch in the worker keeps the background loop alive.
- **`ILlmClient` returns raw text** and `LeadJsonParser` validates it: invalid JSON, a missing `problem` or an unknown `urgency` are rejected, and an unknown appliance becomes `other`. Mock and real clients therefore share the same error handling. Unusable answers are retried `Processing:MaxLlmAttempts` times.
- **Real HTTP clients** use `AddStandardResilienceHandler`, which retries timeouts, 429 and 5xx responses with backoff.
- **Live UI:** SignalR pushes `callChanged`, and the page reloads the current page of data. Paging, filtering and search run on the server (`GET /api/calls?page=&pageSize=&status=&search=`). If SignalR is unavailable, the page falls back to polling.

## Before production

- Validate the `X-Twilio-Signature` header on the webhook.
- Add login to the CRM page.
- Use a durable queue (Hangfire, Service Bus) with dead-lettering and automatic retries for transient failures.
- Use PostgreSQL with EF migrations instead of `EnsureCreated`.
- Download Twilio recordings with basic auth (`RecordingDownload` settings already exist).
- Set retention and encryption rules for phone numbers and transcripts.
- Add structured logging, metrics and alerts on the Failed rate.
- Add automated tests.

## Code map

| Path | What |
|---|---|
| `Api/RecordingWebhookController.cs` | Webhook endpoint (Task 1) |
| `Processing/` | Queue, `CallProcessingWorker` (background loop), `CallProcessor` (transcribe → extract → Done/Failed) |
| `Ai/` | `ITranscriptionService`, `ILlmClient`, `LeadExtractor` (prompt + retry), `LeadJsonParser` (validation), DI switch |
| `Ai/Mock/`, `Ai/OpenAi/` | Mock and real implementations |
| `Api/CallsController.cs` | Paged list and retry API for the page |
| `Pages/Index.cshtml`, `wwwroot/js/leads.js` | Leads page, simulate button, SignalR client |

**Adding an extracted field** touches these places:
- the prompt in `LeadExtractor`
- `LeadExtraction`
- `LeadJsonParser`
- the `Call` entity and `Call.MarkDone`
- `CallDto`
- a table column in `leads.js` / `Index.cshtml`

Then delete `calls.db` so the database is recreated with the new column.

## AI assistance

This project was built with help from Claude (Anthropic): the structure, most of the code and this README. I reviewed every part and can explain or change it.
