# OMP History

**OMP History** is a local Blazor Server + SQLite web application and OMP extension that automatically records, organizes, and renders your [OMP (Oh My Pi)](https://github.com/can1357/oh-my-pi) coding assistant sessions and conversation turns per Git repository — with live updates and two-way feedback back into active OMP terminals.

---

## Features

- **Automatic Session & Turn Logging**:
  - Autoloads on OMP startup via the `omp-history.ts` extension.
  - Groups sessions by Git repository name (resolved from `.git/config` remote URL, falling back to directory name).
  - Logs only the user prompt and final Markdown response (`type === "text"`), stripping model thinking blocks and intermediate tool noise.
  - Tracks session renames (`/rename` or title updates) in real time.
  - Captures non-invasive Git tree snapshots (`GIT_INDEX_FILE` + `git write-tree`) before and after each turn to record per-message file diffs (`diffPatch`, `gitTreeBefore`, `gitTreeAfter`) without touching `.git/index` or creating commits.

- **Rich Markdown, Math & Diagram Rendering**:
  - Full GitHub-flavored/advanced Markdown via **Markdig**.
  - Client-side **Mermaid** diagram rendering (` ```mermaid `).
  - Client-side **KaTeX** math rendering (`$...$`, `$$...$$`, `\(...\)`, `\[...\]`).
  - Embedded ` ```markdown ` / ` ```md ` fenced blocks rendered as formatted Markdown cards instead of raw source text.

- **Live Workspace UI**:
  - Real-time push updates when new sessions or conversation turns arrive.
  - **Auto-select new conversations** toggle in Settings.
  - **Pin Lists** toggle to collapse the Sessions & Conversations sidebars and focus on the response pane.
  - Instant conversation search/filtering across prompts and model responses.
  - Session deletion with confirmation modal.
  - **Per-Message Diffs & Compare Messages**: Displays modified files (`+X -Y`) directly beneath any conversation turn and supports comparing file state between any two messages in **Git Compare → Compare Messages**.

- **Active Session Detection & Two-Way Feedback to OMP**:
  - Detects which sessions are currently active in running OMP terminals (via extension heartbeats and `/proc` inspection) and marks them with a green **`● ACTIVE`** badge.
  - **Right-click Feedback Card**: Select any text in a model response (or right-click anywhere in the detail pane) to open a Word-style comment card, quote the selected passage, and post feedback directly back into the running OMP session as a new turn.

- **Cross-Machine Session Resume (`/omp-history resume`)**:
  - Synthesizes a clean OMP v3 `.jsonl` session transcript directly from the SQLite database (`GET /api/sessions/{sessionId}/export-jsonl`) and switches into it on any machine — keeping the exact same `sessionId` across desktop and laptop without transferring multi-megabyte raw session logs.

- **Basic Authentication & Reverse Proxy Support**:
  - Optional HTTP Basic Auth via `OMP_HISTORY_AUTH` (`user:password` or password-only `omp:<password>`).
  - Supports hosting under a subpath (e.g. `/omp-history` behind Nginx) via `X-Forwarded-Prefix` or `OMP_HISTORY_PATH_BASE`.

- **Resilient Startup & Recovery**:
  - Warns on startup if `OMP_HISTORY_URL` (or `OMP_HISTORY_PORT`) is not configured.
  - If the history server isn't reachable when OMP tries to log a turn, OMP prompts `Start server / Retry / Disable`.
---

## Repository Structure

```text
omp-history/
├── OmpHistory.slnx
├── omp-addon/
│   ├── extensions/
│   │   └── omp-history.ts          # OMP autoloaded extension (logging, heartbeats, feedback polling)
│   └── skills/
│       └── omp-history/
│           └── SKILL.md            # OMP skill documentation
└── src/
    └── OmpHistory/                 # .NET 10 Blazor Server + Minimal API + SQLite app
        ├── Components/             # Blazor UI (Home.razor, App.razor, layouts)
        ├── Data/                   # SQLite repository (HistoryRepository.cs)
        ├── Models/                 # DTOs and domain records
        ├── Services/               # Live notifier & ActiveSessionTracker
        └── wwwroot/                # Styles (app.css), Mermaid, and KaTeX assets
```

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [OMP (Oh My Pi)](https://github.com/can1357/oh-my-pi)

### 1. Setup Configuration

Set `OMP_HISTORY_URL` (or `OMP_HISTORY_PORT`) and optionally `OMP_HISTORY_AUTH` in your shell profile (`~/.bashrc` / `~/.zshrc`):

```bash
export OMP_HISTORY_URL="https://skegriebyavag13.duckdns.org/omp-history"
export OMP_HISTORY_AUTH="omp:your-secret-password"
```

| Environment Variable | Description | Default |
| :--- | :--- | :--- |
| `OMP_HISTORY_URL` | Base URL for the `omp-history` server (required unless `OMP_HISTORY_PORT` is set) | — (warns if unset) |
| `OMP_HISTORY_PORT` | Port for a local server (`http://localhost:<port>`) when `OMP_HISTORY_URL` is not set | — |
| `OMP_HISTORY_AUTH` | Basic Auth credentials (`username:password` or `password` which defaults to user `omp`) | Disabled if unset |
| `OMP_HISTORY_PATH_BASE` | Optional subpath prefix when hosted behind a reverse proxy (e.g. `/omp-history`) | — |
| `OMP_HISTORY_PROJECT_DIR` | Path to the `omp-history` repo or `src/OmpHistory` (used by `/omp-history start`) | Auto-detected |
| `ASPNETCORE_URLS` | Standard ASP.NET Core binding URL override for the server | `http://localhost:5180` |

You can also override the SQLite database path in `src/OmpHistory/appsettings.json` via `"Database": { "Path": "/path/to/omp-history.db" }` (or environment variable `Database__Path`).

### 2. Install the OMP Extension & Skill

Copy or symlink the extension and skill into your `~/.omp/agent/` directory:

```bash
mkdir -p ~/.omp/agent/extensions ~/.omp/agent/skills/omp-history
cp omp-addon/extensions/omp-history.ts ~/.omp/agent/extensions/omp-history.ts
cp omp-addon/skills/omp-history/SKILL.md ~/.omp/agent/skills/omp-history/SKILL.md
```

### 3. Start OMP (Auto-Starts the Server)

You do **not** need to start the server manually if your configuration is set:

- When `omp` starts (or logs a turn) and the server is not running, it prompts **`Start server / Retry / Disable`** — choosing **`Start server`** launches `dotnet run` in the background automatically.
- Or run `/omp-history start` inside `omp` at any time.
- If you ever want to run the server manually:
  ```bash
  dotnet run --project src/OmpHistory
  ```
  Then open **`http://localhost:5180`** in your browser.

---

## OMP Slash Commands

Inside any OMP session, use `/omp-history`:

- `/omp-history resume [sessionId]` — Interactive picker (or direct by ID) to download a session's conversation history from `omp-history` as a local `.jsonl` file and switch the active OMP session into it (preserving the same `sessionId`).
- `/omp-history status` — Show whether logging is enabled, the active server URL, auth status, and resolved repo name.
- `/omp-history start` — Start a local `omp-history` server in the background and enable logging.
- `/omp-history enable` (`on`) — Re-enable logging for the current session.
- `/omp-history disable` (`off`) — Disable logging for the current session.
---

## REST API

| Method | Endpoint | Description |
| :--- | :--- | :--- |
| `GET` | `/api/repos` | List all recorded repositories and session counts |
| `GET` | `/api/repos/{repo}/sessions` | List sessions for a repository |
| `GET` | `/api/sessions` | List recent sessions across all repositories (or filter by `?repo=`) |
| `GET` | `/api/sessions/{sessionId}` | Get metadata for a specific session |
| `GET` | `/api/sessions/{sessionId}/export-jsonl` | Synthesize an OMP v3 `.jsonl` transcript from recorded conversations (`?cwd=/local/repo/path`) |
| `POST` | `/api/sessions` | Create or update a session (`{ sessionId, repo, name, repoPath }`) |
| `PUT` | `/api/sessions/{sessionId}` | Rename a session (`{ name, repo, repoPath }`) |
| `DELETE` | `/api/sessions/{sessionId}` | Delete a session and its conversations |
| `GET` | `/api/sessions/{sessionId}/conversations` | List all conversation turns in a session |
| `POST` | `/api/conversations` | Record a completed conversation turn (`{ sessionId, repo, sessionName, prompt, responseMd }`) |
| `GET` | `/api/active-sessions` | List active OMP session IDs |
| `POST` | `/api/sessions/{sessionId}/heartbeat` | Record heartbeat for a session and dequeue pending feedback items |
| `POST` | `/api/sessions/{sessionId}/feedback` | Queue a feedback comment/quote for an OMP session (`{ comment, quotedText, conversationId }`) |
| `GET` | `/api/sessions/{sessionId}/feedback/pending` | Dequeue pending feedback items for a session |
