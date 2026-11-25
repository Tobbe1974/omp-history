---
name: omp-history
description: Integrates OMP sessions and conversations with the omp-history C#/Blazor SQLite app. Automatically loaded on startup; supports session creation, session renaming, and logging completed user/model conversation turns per git repository.
alwaysApply: true
---

# omp-history Skill & Addon

`omp-history` records OMP sessions and completed user/assistant conversation turns into a local or remote C# / Blazor / SQLite history server (configured via `OMP_HISTORY_URL` or `OMP_HISTORY_PORT`, and optional Basic Auth via `OMP_HISTORY_AUTH`).

## Behavior

1. **Autoloaded on Startup**:
   - The extension at `~/.omp/agent/extensions/omp-history.ts` loads automatically when OMP starts.
   - Warns if neither `OMP_HISTORY_URL` nor `OMP_HISTORY_PORT` is set in the environment.
   - Sends `Authorization: Basic ...` header automatically when `OMP_HISTORY_AUTH` (`user:pass` or `pass`) is set.
   - Extracts `repo` from the last segment of the `.git/config` remote URL (stripping `.git`), falling back to the working directory basename.
   - Registers the current session (`POST /api/sessions` with `{ sessionId, repo, repoPath, name }`).

2. **Session Rename**:
   - When a session is renamed (via `/rename` or title changes), sends `PUT /api/sessions/{sessionId}` with `{ name, repo }`.

3. **Conversation Turn & File Diff Logging**:
   - Snapshots the Git working tree before (`before_agent_start`) and after (`agent_end`) each turn using a temporary `GIT_INDEX_FILE` (`git add -A && git write-tree`) so the real `.git/index` and commit history remain untouched.
   - Posts `{ sessionId, repo, repoPath, sessionName, prompt, responseMd, diffPatch, gitTreeBefore, gitTreeAfter }` to `POST /api/conversations`.
   - Includes **only the final Markdown response** (`type === "text"` blocks) and excludes all model thinking blocks (`thinking`) and intermediate tool outputs.

4. **Error Handling (`Start server / Retry / Disable`)**:
   - If posting to `omp-history` fails, prompts:
     `"Could not post to omp-history. Start server/Retry/Disable"` with choices `Start server`, `Retry`, and `Disable`.
   - Selecting `Start server` runs `dotnet run` in `src/OmpHistory` in the background, waits for `http://localhost:5180` to come online, and retries the request; selecting `Retry` immediately retries the request; selecting `Disable` disables posting for the remainder of the session (can be re-enabled anytime with `/omp-history enable` or `/omp-history start`).


5. **Cross-Machine Session Resume (`/omp-history resume [sessionId]`)**:
   - Lists sessions from `omp-history` for the current repo (falling back to all repos) or matches a provided `sessionId` prefix.
   - Downloads a synthesized OMP v3 `.jsonl` file from `GET /api/sessions/{sessionId}/export-jsonl?cwd=<localRepoRoot>`, writes it to `~/.omp/agent/sessions/<encoded-cwd>/<timestamp>_<sessionId>.jsonl`, and switches the live session into it via `ctx.switchSession(...)` so work continues under the exact same `sessionId`.

6. **Change Suggestions (`omp_history_suggest`)**:
   - While exploring or working on a task, if you notice a genuine bug, race condition, dead code, security issue, or nonsensical logic **outside** the user's current request, record it with `omp_history_suggest` instead of derailing the active task.
   - Populate:
     - `title`: short, specific summary.
     - `humanSummary`: clear Markdown explanation for the user (what is wrong and why it matters).
     - `agentContext`: dense, model-optimized technical payload (exact file paths, symbol names, root cause, invariants, edge cases, and verification steps) so a future agent can review and propose a fix when the user clicks **Change suggestion** in the UI.
     - `filePaths` / `severity` (`bug`, `refactor`, `perf`, `dead-code`, `security`, `cleanup`): optional metadata.

## Output Formatting Rules (No Nested Markdown Fences)

- All assistant replies logged to `omp-history` are **already rendered as Markdown**.
- **NEVER** wrap drafted stories, tickets, documentation, or Markdown content in outer ```` ```markdown ```` or ```` ```md ```` fenced code blocks (especially when creating stories/tickets that themselves contain code blocks or formatting), because nested Markdown fences break outer fence parsing and rendering.
- Write story/ticket/document content directly as top-level Markdown headings, lists, and blockquotes in your response.

## REST API Reference

- `POST /api/sessions`
  - Body: `{ "sessionId": "<id>", "repo": "<repo>", "repoPath": "<path>", "name": "<session-name>" }`
- `PUT /api/sessions/{sessionId}`
  - Body: `{ "name": "<new-name>", "repo": "<repo>", "repoPath": "<path>" }`
- `DELETE /api/sessions/{sessionId}`
- `GET /api/deleted-sessions`
- `POST /api/conversations`
  - Body: `{ "sessionId": "<id>", "repo": "<repo>", "repoPath": "<path>", "sessionName": "<name>", "prompt": "<user-question>", "responseMd": "<final-markdown-reply>", "diffPatch": "<unified-diff>", "gitTreeBefore": "<sha>", "gitTreeAfter": "<sha>" }`
- `GET /api/repos`
- `GET /api/repos/{repo}/sessions`
- `GET /api/sessions/{sessionId}/conversations`
- `GET /api/sessions/{sessionId}/export-jsonl?cwd=<path>`
- `GET /api/repos/{repo}/suggestions`
- `POST /api/suggestions`
  - Body: `{ "repo": "<repo>", "repoPath": "<path>", "sessionId": "<id>", "title": "<title>", "humanSummary": "<markdown>", "agentContext": "<model-context>", "filePaths": "<paths>", "severity": "<tag>" }`
- `POST /api/suggestions/{id}/change`
- `DELETE /api/suggestions/{id}`
