import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const DEFAULT_PORT = "5180";
const DEFAULT_BASE_URL = `http://localhost:${DEFAULT_PORT}`;
const DEFAULT_PROJECT_DIR = "/mnt/div/Projects/omp-history/src/OmpHistory";

export function resolveBaseUrl(): string {
  const explicitUrl = process.env.OMP_HISTORY_URL?.trim();
  if (explicitUrl) {
    return explicitUrl.replace(/\/+$/, "");
  }
  const port = process.env.OMP_HISTORY_PORT?.trim();
  if (port) {
    return `http://localhost:${port}`;
  }
  return DEFAULT_BASE_URL;
}
interface ContentBlock {
  type?: string;
  text?: string;
}

interface RoleMessage {
  role?: string;
  stopReason?: string;
  content?: unknown;
}

interface SessionEntryRecord {
  id?: string;
  type?: string;
  title?: string;
  role?: string;
  stopReason?: string;
  content?: unknown;
  message?: RoleMessage;
}

interface ExtensionUI {
  select?: (title: string, options: string[]) => Promise<string | undefined>;
  notify?: (message: string, level?: "info" | "warning" | "error") => void;
}

interface SessionManagerView {
  getSessionId?: () => string | undefined;
  getBranch?: () => SessionEntryRecord[];
}

interface ExtensionContextView {
  hasUI?: boolean;
  cwd?: string;
  agent?: { kind?: string };
  ui?: ExtensionUI;
  sessionManager?: SessionManagerView;
  setInterval?: (fn: () => void, ms: number) => unknown;
}

interface InputEventView {
  text?: string;
}

interface BeforeAgentStartEventView {
  prompt?: string;
}

interface AgentEndEventView {
  messages?: SessionEntryRecord[];
}

interface ExtensionHostAPI {
  on: {
    (event: "session_start" | "session_switch", handler: (event: unknown, ctx: ExtensionContextView) => Promise<void> | void): void;
    (event: "input", handler: (event: InputEventView, ctx: ExtensionContextView) => Promise<void> | void): void;
    (event: "before_agent_start", handler: (event: BeforeAgentStartEventView, ctx: ExtensionContextView) => Promise<void> | void): void;
    (event: "agent_end", handler: (event: AgentEndEventView, ctx: ExtensionContextView) => Promise<void> | void): void;
  };
  getSessionName?: () => string | undefined;
  registerCommand: (
    name: string,
    config: {
      description: string;
      handler: (args: string, ctx: ExtensionContextView) => Promise<void> | void;
    },
  ) => void;
}

export function extractRepoNameFromRemoteUrl(remoteUrl: string): string | undefined {
  let cleaned = remoteUrl.trim().replace(/[\/\\]+$/, "");
  if (!cleaned) return undefined;

  if (cleaned.endsWith(".git")) {
    cleaned = cleaned.slice(0, -4).replace(/[\/\\]+$/, "");
  }

  const colonIdx = cleaned.lastIndexOf(":");
  const slashIdx = Math.max(cleaned.lastIndexOf("/"), cleaned.lastIndexOf("\\"));
  const splitIdx = Math.max(colonIdx, slashIdx);

  const segment = splitIdx >= 0 ? cleaned.slice(splitIdx + 1) : cleaned;
  const trimmed = segment.trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

export function parseGitConfigRemoteUrl(gitConfigContent: string): string | undefined {
  const lines = gitConfigContent.split(/\r?\n/);
  let currentSection = "";
  let originUrl: string | undefined;
  let firstRemoteUrl: string | undefined;

  for (const rawLine of lines) {
    const line = rawLine.trim();
    if (!line || line.startsWith("#") || line.startsWith(";")) continue;

    const sectionMatch = line.match(/^\[([^\]]+)\]$/);
    if (sectionMatch) {
      currentSection = sectionMatch[1].trim();
      continue;
    }

    if (/^remote\s+"/i.test(currentSection)) {
      const urlMatch = line.match(/^url\s*=\s*(.+)$/i);
      if (urlMatch) {
        const value = urlMatch[1].trim().replace(/^["']|["']$/g, "");
        if (!firstRemoteUrl) firstRemoteUrl = value;
        if (/^remote\s+"origin"$/i.test(currentSection)) {
          originUrl = value;
        }
      }
    }
  }

  return originUrl ?? firstRemoteUrl;
}

function findGitConfigPath(startDir: string): string | undefined {
  let current = path.resolve(startDir);
  while (true) {
    const dotGit = path.join(current, ".git");
    try {
      const stat = fs.statSync(dotGit);
      if (stat.isDirectory()) {
        const cfg = path.join(dotGit, "config");
        if (fs.existsSync(cfg)) return cfg;
      } else if (stat.isFile()) {
        const gitFile = fs.readFileSync(dotGit, "utf8");
        const match = gitFile.match(/^gitdir:\s*(.+)$/m);
        if (match) {
          const gitDir = path.resolve(current, match[1].trim());
          const directCfg = path.join(gitDir, "config");
          if (fs.existsSync(directCfg)) return directCfg;
          const commonCfg = path.resolve(gitDir, "..", "..", "config");
          if (fs.existsSync(commonCfg)) return commonCfg;
        }
      }
    } catch {
      // ignore and walk up
    }

    const parent = path.dirname(current);
    if (parent === current) break;
    current = parent;
  }
  return undefined;
}

export function resolveRepoName(cwd: string): string {
  try {
    const gitConfigPath = findGitConfigPath(cwd);
    if (gitConfigPath) {
      const content = fs.readFileSync(gitConfigPath, "utf8");
      const remoteUrl = parseGitConfigRemoteUrl(content);
      if (remoteUrl) {
        const parsed = extractRepoNameFromRemoteUrl(remoteUrl);
        if (parsed) return parsed;
      }
    }
  } catch {
    // fallback to directory basename
  }

  const fallback = path.basename(path.resolve(cwd));
  return fallback || "unknown";
}

export function extractTextFromContent(content: unknown): string {
  if (typeof content === "string") {
    return content
      .replace(/<system-reminder>[\s\S]*?<\/system-reminder>/g, "")
      .replace(/<system-notice>[\s\S]*?<\/system-notice>/g, "")
      .trim();
  }
  if (!Array.isArray(content)) {
    return "";
  }

  const parts: string[] = [];
  for (const item of content) {
    if (!item || typeof item !== "object") continue;
    const block = item as ContentBlock;
    // Only extract plain "text" blocks; ignore "thinking", "toolCall", etc.
    if (block.type === "text" && typeof block.text === "string") {
      const cleaned = block.text
        .replace(/<system-reminder>[\s\S]*?<\/system-reminder>/g, "")
        .replace(/<system-notice>[\s\S]*?<\/system-notice>/g, "")
        .trim();
      if (cleaned) {
        parts.push(cleaned);
      }
    }
  }
  return parts.join("\n\n").trim();
}

export function extractLatestTurnFromMessages(
  messages: SessionEntryRecord[],
): { prompt: string; responseMd: string; aborted?: boolean } | undefined {
  if (!Array.isArray(messages) || messages.length === 0) return undefined;

  let lastUserIdx = -1;
  for (let i = messages.length - 1; i >= 0; i--) {
    const msg = messages[i];
    const unwrapped = msg?.type === "message" && msg?.message ? msg.message : msg;
    if (unwrapped?.role === "user") {
      lastUserIdx = i;
      break;
    }
  }

  if (lastUserIdx < 0) return undefined;

  const userMsgRaw = messages[lastUserIdx];
  const userMsg = userMsgRaw?.type === "message" && userMsgRaw?.message ? userMsgRaw.message : userMsgRaw;
  const prompt = extractTextFromContent(userMsg?.content);
  if (!prompt) return undefined;

  // Check if the final assistant message in this turn was aborted or errored
  for (let i = messages.length - 1; i > lastUserIdx; i--) {
    const msgRaw = messages[i];
    const msg = msgRaw?.type === "message" && msgRaw?.message ? msgRaw.message : msgRaw;
    if (msg?.role === "assistant") {
      if (msg.stopReason === "aborted" || msg.stopReason === "error") {
        return { prompt, responseMd: "", aborted: true };
      }
      const text = extractTextFromContent(msg?.content);
      if (text) {
        return { prompt, responseMd: text };
      }
    }
  }

  return undefined;
}

export function resolveServerProjectDir(cwd?: string): string | undefined {
  const candidates: string[] = [];
  const envDir =
    process.env.OMP_HISTORY_PROJECT_DIR?.trim() ||
    process.env.OMP_HISTORY_DIR?.trim() ||
    process.env.OMP_HISTORY_PATH?.trim();
  if (envDir) {
    candidates.push(path.resolve(envDir, "src", "OmpHistory"));
    candidates.push(path.resolve(envDir));
  }
  if (cwd) {
    candidates.push(path.resolve(cwd, "src", "OmpHistory"));
    candidates.push(path.resolve(cwd));
  }
  try {
    const extRepoDir = path.resolve(import.meta.dir, "..", "..", "src", "OmpHistory");
    candidates.push(extRepoDir);
  } catch {
    // ignore
  }
  candidates.push(DEFAULT_PROJECT_DIR);
  candidates.push("/mnt/div/Projects/omp-history/src/OmpHistory");
  candidates.push(path.join(os.homedir(), "Projects", "Personal", "omp-history", "src", "OmpHistory"));
  candidates.push(path.join(os.homedir(), "Projects", "Personal", "omp-hisitory", "src", "OmpHistory"));

  for (const dir of candidates) {
    try {
      const csproj = path.join(dir, "OmpHistory.csproj");
      if (fs.existsSync(csproj)) {
        return dir;
      }
    } catch {
      // ignore
    }
  }
  return undefined;
}

export async function startOmpHistoryServer(baseUrl: string, cwd?: string): Promise<{ ok: boolean; error?: string }> {
  const projectDir = resolveServerProjectDir(cwd);
  if (!projectDir) {
    return { ok: false, error: "Could not locate src/OmpHistory/OmpHistory.csproj" };
  }

  const normalizedBaseUrl = baseUrl.replace(/\/+$/, "");
  try {
    const child = spawn("dotnet", ["run", "--no-launch-profile", "--urls", normalizedBaseUrl], {
      cwd: projectDir,
      detached: true,
      stdio: "ignore",
      env: {
        ...process.env,
        ASPNETCORE_URLS: normalizedBaseUrl,
      },
    });
    child.unref();
  } catch (err) {
    return { ok: false, error: err instanceof Error ? err.message : String(err) };
  }

  const probeUrl = `${normalizedBaseUrl}/api/repos`;
  const deadline = Date.now() + 25_000;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(probeUrl, {
        method: "GET",
        signal: AbortSignal.timeout(1500),
      });
      if (res.ok) {
        return { ok: true };
      }
    } catch {
      // wait until server is listening
    }
    const { promise, resolve } = Promise.withResolvers<void>();
    setTimeout(resolve, 400);
    await promise;
  }

  return { ok: false, error: `Timed out waiting for omp-history server at ${baseUrl}` };
}

export default function ompHistoryExtension(pi: ExtensionHostAPI): void {
  let disabled = false;
  let activeSessionId: string | undefined;
  let lastKnownSessionName: string | undefined;
  let lastKnownRepo: string | undefined;
  let latestUserPrompt: string | undefined;
  let promptInFlight = false;

  function readSessionId(ctx: ExtensionContextView): string | undefined {
    try {
      const id = ctx?.sessionManager?.getSessionId?.();
      if (typeof id === "string" && id.trim().length > 0) {
        return id.trim();
      }
    } catch {
      // ignore
    }
    return undefined;
  }

  function readSessionName(ctx: ExtensionContextView): string {
    try {
      const fromPi = pi?.getSessionName?.();
      if (typeof fromPi === "string" && fromPi.trim().length > 0) {
        return fromPi.trim();
      }
    } catch {
      // ignore
    }

    try {
      const branch = ctx?.sessionManager?.getBranch?.();
      if (Array.isArray(branch)) {
        for (let i = branch.length - 1; i >= 0; i--) {
          const entry = branch[i];
          if (entry?.type === "title_change" && typeof entry?.title === "string" && entry.title.trim()) {
            return entry.title.trim();
          }
          if (entry?.type === "session" && typeof entry?.title === "string" && entry.title.trim()) {
            return entry.title.trim();
          }
        }
      }
    } catch {
      // ignore
    }

    if (latestUserPrompt) {
      const firstLine = latestUserPrompt.split(/\r?\n/)[0]?.trim();
      if (firstLine) {
        return firstLine.length <= 60 ? firstLine : `${firstLine.slice(0, 60)}…`;
      }
    }

    const id = readSessionId(ctx) ?? "session";
    return `Session ${id.slice(0, 8)}`;
  }

  async function requestWithRetryPrompt(
    ctx: ExtensionContextView,
    method: "POST" | "PUT",
    endpointPath: string,
    payload: Record<string, unknown>,
  ): Promise<boolean> {
    if (disabled) return false;

    const baseUrl = resolveBaseUrl();
    const url = `${baseUrl}${endpointPath}`;
    while (!disabled) {
      try {
        const res = await fetch(url, {
          method,
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(payload),
          signal: AbortSignal.timeout(4000),
        });
        if (res.ok) {
          return true;
        }
      } catch {
        // handled below
      }

      if (ctx?.hasUI && typeof ctx?.ui?.select === "function") {
        try {
          const choice = await ctx.ui.select(
            "Could not post to omp-history. Start server/Retry/Disable",
            ["Start server", "Retry", "Disable"],
          );
          if (choice === "Start server") {
            ctx.ui.notify?.("Starting omp-history server...", "info");
            const started = await startOmpHistoryServer(baseUrl, ctx?.cwd);
            if (started.ok) {
              ctx.ui.notify?.(`omp-history server started at ${baseUrl}.`, "info");
            } else {
              ctx.ui.notify?.(`Failed to start omp-history server: ${started.error}`, "error");
            }
            continue;
          }
          if (choice === "Retry") {
            continue;
          }
          disabled = true;
          ctx.ui.notify?.("omp-history posting disabled for this session.", "warning");
          return false;
        } catch {
          disabled = true;
          return false;
        }
      } else {
        ctx?.ui?.notify?.("Could not post to omp-history. Start server/Retry/Disable", "warning");
        return false;
      }
    }

    return false;
  }

  async function syncSessionRegistration(ctx: ExtensionContextView): Promise<void> {
    if (disabled || (ctx?.agent?.kind && ctx.agent.kind !== "main")) return;

    const sessionId = readSessionId(ctx);
    if (!sessionId) return;

    const repo = resolveRepoName(ctx?.cwd || process.cwd());
    const name = readSessionName(ctx);

    activeSessionId = sessionId;
    lastKnownRepo = repo;
    lastKnownSessionName = name;

    await requestWithRetryPrompt(ctx, "POST", "/api/sessions", {
      sessionId,
      repo,
      name,
    });
  }

  async function checkAndSyncSessionRename(ctx: ExtensionContextView): Promise<void> {
    if (disabled || (ctx?.agent?.kind && ctx.agent.kind !== "main")) return;

    const sessionId = readSessionId(ctx);
    if (!sessionId) return;

    const currentRepo = resolveRepoName(ctx?.cwd || process.cwd());
    if (activeSessionId !== sessionId) {
      await syncSessionRegistration(ctx);
      return;
    }

    const currentName = readSessionName(ctx);
    if (currentName && currentName !== lastKnownSessionName) {
      lastKnownSessionName = currentName;
      lastKnownRepo = currentRepo;
      await requestWithRetryPrompt(ctx, "PUT", `/api/sessions/${encodeURIComponent(sessionId)}`, {
        name: currentName,
        repo: currentRepo,
      });
    }
  }

  pi.on("session_start", async (_event, ctx) => {
    if (ctx?.agent?.kind && ctx.agent.kind !== "main") return;
    await syncSessionRegistration(ctx);

    if (typeof ctx?.setInterval === "function") {
      ctx.setInterval(() => {
        void checkAndSyncSessionRename(ctx);
      }, 3000);
    }
  });

  pi.on("session_switch", async (_event, ctx) => {
    if (ctx?.agent?.kind && ctx.agent.kind !== "main") return;
    latestUserPrompt = undefined;
    promptInFlight = false;
    await syncSessionRegistration(ctx);
  });

  pi.on("input", async (event, ctx) => {
    if (ctx?.agent?.kind && ctx.agent.kind !== "main") return;
    const text = typeof event?.text === "string" ? event.text.trim() : "";
    if (!text) return;

    const renameMatch = text.match(/^\/rename\s+(.+)$/i);
    if (renameMatch && renameMatch[1]?.trim()) {
      const newName = renameMatch[1].trim();
      const sessionId = readSessionId(ctx);
      if (sessionId) {
        lastKnownSessionName = newName;
        const repo = resolveRepoName(ctx?.cwd || process.cwd());
        void requestWithRetryPrompt(ctx, "PUT", `/api/sessions/${encodeURIComponent(sessionId)}`, {
          name: newName,
          repo,
        });
      }
      return;
    }

    if (!text.startsWith("/")) {
      latestUserPrompt = text;
      promptInFlight = true;
    }
  });

  pi.on("before_agent_start", async (event, ctx) => {
    if (ctx?.agent?.kind && ctx.agent.kind !== "main") return;
    if (typeof event?.prompt === "string" && event.prompt.trim().length > 0) {
      latestUserPrompt = event.prompt
        .replace(/<system-reminder>[\s\S]*?<\/system-reminder>/g, "")
        .replace(/<system-notice>[\s\S]*?<\/system-notice>/g, "")
        .trim();
      promptInFlight = true;
    }
    await checkAndSyncSessionRename(ctx);
  });

  pi.on("agent_end", async (event, ctx) => {
    if (disabled || (ctx?.agent?.kind && ctx.agent.kind !== "main")) return;

    const sessionId = readSessionId(ctx);
    if (!sessionId) return;

    await checkAndSyncSessionRename(ctx);

    const eventMessages = Array.isArray(event?.messages) ? event.messages : [];
    const branchEntries = ctx?.sessionManager?.getBranch?.() ?? [];

    const fromEvent = extractLatestTurnFromMessages(eventMessages);
    if (fromEvent?.aborted) {
      promptInFlight = false;
      return;
    }

    const fromBranch = !fromEvent ? extractLatestTurnFromMessages(branchEntries) : undefined;
    if (fromBranch?.aborted) {
      promptInFlight = false;
      return;
    }

    const prompt = fromEvent?.prompt ?? fromBranch?.prompt;
    const responseMd = fromEvent?.responseMd ?? fromBranch?.responseMd;

    if (!prompt || !responseMd) {
      promptInFlight = false;
      return;
    }
    if (!promptInFlight && !fromEvent) return;

    promptInFlight = false;

    const repo = lastKnownRepo ?? resolveRepoName(ctx?.cwd || process.cwd());
    const sessionName = readSessionName(ctx);
    lastKnownSessionName = sessionName;

    await requestWithRetryPrompt(ctx, "POST", "/api/conversations", {
      sessionId,
      repo,
      sessionName,
      prompt,
      responseMd,
    });
  });

  pi.registerCommand("omp-history", {
    description: "Start, enable, disable, or check status of omp-history logging (/omp-history [start|enable|disable|status])",
    handler: async (args, ctx) => {
      const sub = (args || "").trim().toLowerCase();
      const baseUrl = resolveBaseUrl();
      if (sub === "start") {
        disabled = false;
        ctx.ui?.notify?.("Starting omp-history server...", "info");
        const started = await startOmpHistoryServer(baseUrl, ctx?.cwd);
        if (started.ok) {
          ctx.ui?.notify?.(`omp-history server started at ${baseUrl}.`, "info");
          await syncSessionRegistration(ctx);
        } else {
          ctx.ui?.notify?.(`Failed to start omp-history server: ${started.error}`, "error");
        }
        return;
      }
      if (sub === "enable" || sub === "on") {
        disabled = false;
        ctx.ui?.notify?.("omp-history enabled.", "info");
        await syncSessionRegistration(ctx);
        return;
      }
      if (sub === "disable" || sub === "off") {
        disabled = true;
        ctx.ui?.notify?.("omp-history disabled for this session.", "warning");
        return;
      }
      ctx.ui?.notify?.(
        `omp-history: ${disabled ? "disabled" : "enabled"} (url=${baseUrl}, repo=${resolveRepoName(ctx?.cwd || process.cwd())})`,
        "info",
      );
    },
  });
}
