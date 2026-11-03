# OMP History

**OMP History** is a local Blazor Server + SQLite web application and OMP extension that automatically records, organizes, and renders your [OMP (Oh My Pi)](https://github.com/can1357/oh-my-pi) coding assistant sessions and conversation turns per Git repository — with live updates and two-way feedback back into active OMP terminals.

---

## Features

- **Automatic Session & Turn Logging**:
  - Autoloads on OMP startup via the `omp-history.ts` extension.
  - Groups sessions by Git repository name (resolved from `.git/config` remote URL, falling back to directory name).
  - Logs only the user prompt and final Markdown response (`type === "text"`), stripping model thinking blocks and intermediate tool noise.
  - Tracks session renames (`/rename` or title updates) in real time.

- **Rich Markdown, Math & Diagram Rendering**:
  - Full GitHub-flavored/advanced Markdown via **Markdig**.
  - Client-side **Mermaid** diagram rendering (` ```mermaid `).
  - Client-side **KaTeX** math rendering (`$...$`, `$$...$$`, `\(...\)`, `\[...\]`).

- **Live Workspace UI**:
  - Real-time push updates when new sessions or conversation turns arrive.
  - **Auto-select new conversations** toggle in Settings.
  - **Pin Lists** toggle to collapse the Sessions & Conversations sidebars and focus on the response pane.
  - Instant conversation search/filtering across prompts and model responses.
  - Session deletion with confirmation modal.

- **Active Session Detection & Two-Way Feedback to OMP**:
  - Detects which sessions are currently active in running OMP terminals (via extension heartbeats and `/proc` inspection) and marks them with a green **`● ACTIVE`** badge.
  - **Right-click Feedback Card**: Select any text in a model response (or right-click anywhere in the detail pane) to open a Word-style comment card, quote the selected passage, and post feedback directly back into the running OMP session as a new turn.

- **Resilient Startup & Recovery**:
  - If the history server isn't running when OMP tries to log a turn, OMP prompts `Start server / Retry / Disable` and can launch the server in the background automatically.

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

Set `OMP_HISTORY_PROJECT_DIR` (or `OMP_HISTORY_DIR`) in your shell profile (`~/.bashrc` / `~/.zshrc`) so the OMP extension knows where the server project is located, and optionally customize the port or URL:

```bash
export OMP_HISTORY_PROJECT_DIR="/path/to/omp-history"
export OMP_HISTORY_PORT="5180" # optional, defaults to 5180
```

| Environment Variable | Description | Default |
| :--- | :--- | :--- |
| `OMP_HISTORY_PROJECT_DIR` | Path to the `omp-history` repo or `src/OmpHistory` (used by OMP to auto-start the server) | Auto-detected |
| `OMP_HISTORY_PORT` | Port for the local server (`http://localhost:<port>`) | `5180` |
| `OMP_HISTORY_URL` | Explicit base URL (overrides `OMP_HISTORY_PORT`) | `http://localhost:5180` |
| `ASPNETCORE_URLS` | Standard ASP.NET Core binding URL override | — |

You can also override the SQLite database path in `src/OmpHistory/appsettings.json` via `"Database": { "Path": "/path/to/omp-history.db" }`.

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

- `/omp-history status` — Show whether logging is enabled, the active server URL, and resolved repo name.
- `/omp-history start` — Start the `omp-history` server in the background and enable logging.
- `/omp-history enable` (`on`) — Re-enable logging for the current session.
- `/omp-history disable` (`off`) — Disable logging for the current session.

---

## REST API

| Method | Endpoint | Description |
| :--- | :--- | :--- |
| `GET` | `/api/repos` | List all recorded repositories and session counts |
| `GET` | `/api/repos/{repo}/sessions` | List sessions for a repository |
| `POST` | `/api/sessions` | Create or update a session (`{ sessionId, repo, name }`) |
| `PUT` | `/api/sessions/{sessionId}` | Rename a session (`{ name, repo }`) |
| `DELETE` | `/api/sessions/{sessionId}` | Delete a session and its conversations |
| `GET` | `/api/sessions/{sessionId}/conversations` | List all conversation turns in a session |
| `POST` | `/api/conversations` | Record a completed conversation turn (`{ sessionId, repo, sessionName, prompt, responseMd }`) |
| `GET` | `/api/active-sessions` | List active OMP session IDs |
| `POST` | `/api/sessions/{sessionId}/heartbeat` | Record heartbeat for a session and dequeue pending feedback items |
| `POST` | `/api/sessions/{sessionId}/feedback` | Queue a feedback comment/quote for an OMP session (`{ comment, quotedText, conversationId }`) |
| `GET` | `/api/sessions/{sessionId}/feedback/pending` | Dequeue pending feedback items for a session |
