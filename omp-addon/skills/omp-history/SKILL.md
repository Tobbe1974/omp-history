---
name: omp-history
description: Integrates OMP sessions and conversations with the omp-history C#/Blazor SQLite app. Automatically loaded on startup; supports session creation, session renaming, and logging completed user/model conversation turns per git repository.
alwaysApply: true
---

# omp-history Skill & Addon

`omp-history` records OMP sessions and completed user/assistant conversation turns into the local C# / Blazor / SQLite history server (`http://localhost:5180` by default, configurable via `OMP_HISTORY_URL`).

## Behavior

1. **Autoloaded on Startup**:
   - The extension at `~/.omp/agent/extensions/omp-history.ts` loads automatically when OMP starts.
   - Extracts `repo` from the last segment of the `.git/config` remote URL (stripping `.git`), falling back to the working directory basename.
   - Registers the current session (`POST /api/sessions` with `{ sessionId, repo, name }`).

2. **Session Rename**:
   - When a session is renamed (via `/rename` or title changes), sends `PUT /api/sessions/{sessionId}` with `{ name, repo }`.

3. **Conversation Turn Logging**:
   - After a user question/task is answered (`agent_end`), posts `{ sessionId, repo, sessionName, prompt, responseMd }` to `POST /api/conversations`.
   - Includes **only the final Markdown response** (`type === "text"` blocks) and excludes all model thinking blocks (`thinking`) and intermediate tool outputs.

4. **Error Handling (`Retry / Disable`)**:
   - If posting to `omp-history` fails, prompts:
     `"Could not post to omp-history. Retry/Disable"` with choices `Retry` and `Disable`.
   - Selecting `Retry` immediately retries the request; selecting `Disable` disables posting for the remainder of the session (can be re-enabled anytime with `/omp-history enable`).

## REST API Reference

- `POST http://localhost:5180/api/sessions`
  - Body: `{ "sessionId": "<id>", "repo": "<repo>", "name": "<session-name>" }`
- `PUT http://localhost:5180/api/sessions/{sessionId}`
  - Body: `{ "name": "<new-name>", "repo": "<repo>" }`
- `DELETE http://localhost:5180/api/sessions/{sessionId}`
- `POST http://localhost:5180/api/conversations`
  - Body: `{ "sessionId": "<id>", "repo": "<repo>", "sessionName": "<name>", "prompt": "<user-question>", "responseMd": "<final-markdown-reply>" }`
- `GET http://localhost:5180/api/repos`
- `GET http://localhost:5180/api/repos/{repo}/sessions`
- `GET http://localhost:5180/api/sessions/{sessionId}/conversations`
