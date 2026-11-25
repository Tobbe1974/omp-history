import { spawn, spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const DEFAULT_PROJECT_DIR = "/mnt/div/Projects/omp-history/src/OmpHistory";

export function resolveBaseUrl(): string | undefined {
  const explicitUrl = process.env.OMP_HISTORY_URL?.trim();
  if (explicitUrl) {
    return explicitUrl.replace(/\/+$/, "");
  }
  const port = process.env.OMP_HISTORY_PORT?.trim();
  if (port) {
    return `http://localhost:${port}`;
  }
  return undefined;
}

export function resolveAuthHeader(): string | undefined {
  const raw = process.env.OMP_HISTORY_AUTH?.trim();
  if (!raw) return undefined;
  const credentials = raw.includes(":") ? raw : `omp:${raw}`;
  return `Basic ${Buffer.from(credentials, "utf8").toString("base64")}`;
}

export function buildRequestHeaders(extra?: Record<string, string>): Record<string, string> {
  const headers: Record<string, string> = { ...(extra ?? {}) };
  const auth = resolveAuthHeader();
  if (auth) {
    headers.Authorization = auth;
  }
  return headers;
}

export function encodeSessionDirName(cwd: string): string {
  let canonical = path.resolve(cwd);
  try {
    canonical = fs.realpathSync(canonical);
  } catch {
    // ignore if path does not exist
  }
  const home = path.resolve(os.homedir());
  const tmp = path.resolve(os.tmpdir());

  if (canonical === home || canonical.startsWith(home + path.sep)) {
    const rel = canonical.slice(home.length).replace(/^[\/\\]+/, "");
    return `-${rel.replace(/[\/\\]/g, "-")}`;
  }
  if (canonical === tmp || canonical.startsWith(tmp + path.sep)) {
    const rel = canonical.slice(tmp.length).replace(/^[\/\\]+/, "");
    return `-tmp-${rel.replace(/[\/\\]/g, "-")}`;
  }
  const trimmed = canonical.replace(/^[\/\\]+|[\/\\]+$/g, "");
  return `--${trimmed.replace(/[\/\\]/g, "-")}--`;
}

export function resolveLocalSessionDir(cwd: string, sessionManager?: SessionManagerView): string {
  try {
    const currentFile = sessionManager?.getSessionFile?.();
    if (typeof currentFile === "string" && currentFile.trim().length > 0) {
      return path.dirname(path.resolve(currentFile));
    }
  } catch {
    // fallback below
  }
  const agentDir = process.env.OMP_CODING_AGENT_DIR?.trim() || path.join(os.homedir(), ".omp", "agent");
  return path.join(agentDir, "sessions", encodeSessionDirName(cwd));
}

export function purgeDeletedLocalSessionFiles(
  deletedSessionIds: string[],
  activeSessionId?: string,
): number {
  if (!Array.isArray(deletedSessionIds) || deletedSessionIds.length === 0) return 0;

  const targetIds = new Set(
    deletedSessionIds
      .map(id => (typeof id === "string" ? id.trim() : ""))
      .filter(id => id.length > 0 && id !== activeSessionId),
  );
  if (targetIds.size === 0) return 0;

  const agentDir = process.env.OMP_CODING_AGENT_DIR?.trim() || path.join(os.homedir(), ".omp", "agent");
  const sessionsRoot = path.join(agentDir, "sessions");
  let removedCount = 0;

  try {
    if (!fs.existsSync(sessionsRoot)) return 0;
    const bucketEntries = fs.readdirSync(sessionsRoot, { withFileTypes: true });
    for (const bucket of bucketEntries) {
      if (!bucket.isDirectory()) continue;
      const bucketPath = path.join(sessionsRoot, bucket.name);
      let files: fs.Dirent[];
      try {
        files = fs.readdirSync(bucketPath, { withFileTypes: true });
      } catch {
        continue;
      }

      for (const entry of files) {
        if (!entry.isFile() || !entry.name.endsWith(".jsonl")) continue;
        const stem = entry.name.slice(0, -".jsonl".length);
        const underscoreIdx = stem.lastIndexOf("_");
        const candidateId = underscoreIdx >= 0 ? stem.slice(underscoreIdx + 1) : stem;
        if (!targetIds.has(candidateId)) continue;

        const jsonlPath = path.join(bucketPath, entry.name);
        try {
          fs.unlinkSync(jsonlPath);
          removedCount++;
        } catch {
          // ignore unlink errors
        }

        const artifactDirPath = path.join(bucketPath, stem);
        try {
          if (fs.existsSync(artifactDirPath) && fs.statSync(artifactDirPath).isDirectory()) {
            fs.rmSync(artifactDirPath, { recursive: true, force: true });
          }
        } catch {
          // ignore artifact dir removal errors
        }
      }
    }
  } catch {
    // ignore scan errors
  }

  return removedCount;
}
interface ContentBlock {
  type?: string;
  text?: string;
}

interface UsageCostView {
  input?: number;
  output?: number;
  cacheRead?: number;
  cacheWrite?: number;
  total?: number;
}

interface MessageUsageView {
  input?: number;
  output?: number;
  cacheRead?: number;
  cacheWrite?: number;
  totalTokens?: number;
  cost?: UsageCostView;
  premiumRequests?: number;
}

interface RoleMessage {
  role?: string;
  stopReason?: string;
  provider?: string;
  model?: string;
  usage?: MessageUsageView;
  content?: unknown;
}

interface SessionEntryRecord {
  id?: string;
  type?: string;
  title?: string;
  role?: string;
  stopReason?: string;
  provider?: string;
  model?: string;
  usage?: MessageUsageView;
  content?: unknown;
  message?: RoleMessage;
}

export interface TurnUsageMetrics {
  creditsUsed?: number;
  costUsd?: number;
  totalTokens?: number;
  model?: string;
}

export interface CopilotQuotaSnapshot {
  creditsUsed: number;
  remaining: number;
  entitlement: number;
  percentRemaining: number;
  resetDate?: string;
  plan?: string;
  updatedAt?: string;
}

interface ExtensionUI {
  select?: (title: string, options: string[]) => Promise<string | undefined>;
  notify?: (message: string, level?: "info" | "warning" | "error") => void;
}

interface SessionManagerView {
  getSessionId?: () => string | undefined;
  getSessionFile?: () => string | undefined;
  getBranch?: () => SessionEntryRecord[];
}

interface ModelInfoView {
  provider?: string;
  id?: string;
}

interface ExtensionContextView {
  hasUI?: boolean;
  cwd?: string;
  agent?: { kind?: string };
  ui?: ExtensionUI;
  model?: ModelInfoView;
  models?: {
    current?: () => ModelInfoView | undefined;
  };
  sessionManager?: SessionManagerView;
  setInterval?: (fn: () => void, ms: number) => unknown;
  switchSession?: (sessionPath: string) => Promise<boolean>;
  waitForIdle?: () => Promise<void>;
}

interface RemoteSessionSummary {
  sessionId: string;
  repo: string;
  name: string;
  createdAt: string;
  updatedAt: string;
  conversationCount: number;
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
  sendUserMessage?: (
    content: string,
    options?: { deliverAs?: "steer" | "followUp" | "aside"; attribution?: "user" | "agent" },
  ) => void;
  sendMessage?: (
    message: string | { content: string; display?: boolean },
    options?: { deliverAs?: "steer" | "followUp" | "nextTurn" | "aside"; triggerTurn?: boolean },
  ) => void;
  registerCommand: (
    name: string,
    config: {
      description: string;
      handler: (args: string, ctx: ExtensionContextView) => Promise<void> | void;
    },
  ) => void;
  registerTool?: (tool: {
    name: string;
    label?: string;
    description: string;
    parameters: Record<string, unknown>;
    execute: (
      toolCallId: string,
      params: Record<string, unknown>,
      signal?: AbortSignal,
      onUpdate?: unknown,
      ctx?: ExtensionContextView,
    ) => Promise<{ content: Array<{ type: "text"; text: string }>; details?: unknown }>;
  }) => void;
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

export function resolveRepoRootPath(cwd: string): string {
  try {
    const gitConfigPath = findGitConfigPath(cwd);
    if (gitConfigPath) {
      const normalized = path.resolve(gitConfigPath);
      const dotGitSegment = `${path.sep}.git${path.sep}`;
      const idx = normalized.indexOf(dotGitSegment);
      if (idx > 0) {
        return normalized.slice(0, idx);
      }
      return path.dirname(path.dirname(normalized));
    }
  } catch {
    // fallback to resolved cwd
  }
  return path.resolve(cwd);
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

export function captureGitTreeSnapshot(cwd: string): string | undefined {
  try {
    const gitConfigPath = findGitConfigPath(cwd);
    if (!gitConfigPath) return undefined;
    const repoRoot = resolveRepoRootPath(cwd);
    const tmpIndex = path.join(
      os.tmpdir(),
      `omp-history-idx-${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2, 9)}`,
    );
    try {
      const env = { ...process.env, GIT_INDEX_FILE: tmpIndex };
      const addRes = spawnSync("git", ["add", "-A"], {
        cwd: repoRoot,
        env,
        encoding: "utf8",
        timeout: 4000,
      });
      if (addRes.status !== 0) return undefined;

      const treeRes = spawnSync("git", ["write-tree"], {
        cwd: repoRoot,
        env,
        encoding: "utf8",
        timeout: 4000,
      });
      if (treeRes.status !== 0) return undefined;

      const treeHash = (treeRes.stdout || "").trim();
      return /^[0-9a-f]{40,64}$/i.test(treeHash) ? treeHash : undefined;
    } finally {
      try {
        if (fs.existsSync(tmpIndex)) {
          fs.unlinkSync(tmpIndex);
        }
      } catch {
        // ignore cleanup error
      }
    }
  } catch {
    return undefined;
  }
}

export function computeGitTreeDiff(cwd: string, treeBefore?: string, treeAfter?: string): string | undefined {
  if (!treeBefore || !treeAfter || treeBefore === treeAfter) return undefined;
  try {
    const repoRoot = resolveRepoRootPath(cwd);
    const diffRes = spawnSync("git", ["diff", "--unified=3", treeBefore, treeAfter], {
      cwd: repoRoot,
      encoding: "utf8",
      timeout: 5000,
      maxBuffer: 4 * 1024 * 1024,
    });
    if (diffRes.status !== 0) return undefined;
    const out = diffRes.stdout || "";
    return out.trim().length > 0 ? out : undefined;
  } catch {
    return undefined;
  }
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

export function extractUsageMetricsFromMessages(messages: SessionEntryRecord[]): TurnUsageMetrics {
  let totalCostUsd = 0;
  let hasCost = false;
  let totalTokens = 0;
  let hasTokens = false;
  let modelName: string | undefined;

  for (const msgRaw of messages) {
    const msg = msgRaw?.type === "message" && msgRaw?.message ? msgRaw.message : msgRaw;
    if (msg?.role !== "assistant") continue;

    if (typeof msg.model === "string" && msg.model.trim()) {
      modelName = msg.provider ? `${msg.provider}/${msg.model}` : msg.model.trim();
    }

    const usage = msg.usage;
    if (!usage) continue;

    if (typeof usage.totalTokens === "number" && Number.isFinite(usage.totalTokens) && usage.totalTokens > 0) {
      totalTokens += usage.totalTokens;
      hasTokens = true;
    } else {
      const sumTok =
        (typeof usage.input === "number" ? usage.input : 0) +
        (typeof usage.output === "number" ? usage.output : 0) +
        (typeof usage.cacheRead === "number" ? usage.cacheRead : 0) +
        (typeof usage.cacheWrite === "number" ? usage.cacheWrite : 0);
      if (sumTok > 0) {
        totalTokens += sumTok;
        hasTokens = true;
      }
    }

    const costTotal = usage.cost?.total;
    if (typeof costTotal === "number" && Number.isFinite(costTotal) && costTotal >= 0) {
      totalCostUsd += costTotal;
      hasCost = true;
    }
  }

  // GitHub Copilot token-based billing converts $0.004 of model token cost into 1 AI credit (~250 credits per $1 USD)
  const creditsUsed = hasCost ? Math.round(totalCostUsd * 250 * 10) / 10 : undefined;
  const costUsd = hasCost ? Math.round(totalCostUsd * 1_000_000) / 1_000_000 : undefined;

  return {
    creditsUsed,
    costUsd,
    totalTokens: hasTokens ? totalTokens : undefined,
    model: modelName,
  };
}

export function fetchCopilotQuotaSnapshot(): CopilotQuotaSnapshot | undefined {
  try {
    const res = spawnSync("gh", ["api", "/copilot_internal/user"], {
      encoding: "utf8",
      timeout: 3500,
      stdio: ["ignore", "pipe", "ignore"],
    });
    if (res.status !== 0 || !res.stdout) return undefined;

    const parsed = JSON.parse(res.stdout) as {
      copilot_plan?: string;
      quota_reset_date?: string;
      quota_snapshots?: {
        premium_interactions?: {
          credits_used?: number;
          quota_remaining?: number;
          remaining?: number;
          entitlement?: number;
          percent_remaining?: number;
          timestamp_utc?: string;
        };
      };
    };

    const prem = parsed?.quota_snapshots?.premium_interactions;
    if (!prem || typeof prem.entitlement !== "number" || prem.entitlement <= 0) {
      return undefined;
    }

    const entitlement = prem.entitlement;
    const remaining =
      typeof prem.quota_remaining === "number"
        ? prem.quota_remaining
        : typeof prem.remaining === "number"
          ? prem.remaining
          : 0;
    const creditsUsed =
      typeof prem.credits_used === "number"
        ? prem.credits_used
        : typeof prem.quota_remaining === "number"
          ? Math.max(0, Math.round((entitlement - prem.quota_remaining) * 10) / 10)
          : Math.max(0, Math.round((entitlement - remaining) * 10) / 10);
    const percentRemaining =
      typeof prem.percent_remaining === "number"
        ? prem.percent_remaining
        : Math.round((remaining / entitlement) * 1000) / 10;

    return {
      creditsUsed,
      remaining,
      entitlement,
      percentRemaining,
      resetDate: parsed.quota_reset_date,
      plan: parsed.copilot_plan,
      updatedAt: prem.timestamp_utc || new Date().toISOString(),
    };
  } catch {
    return undefined;
  }
}

export function extractLatestTurnFromMessages(
  messages: SessionEntryRecord[],
): ({ prompt: string; responseMd: string; aborted?: boolean } & TurnUsageMetrics) | undefined {
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

  for (let i = messages.length - 1; i > lastUserIdx; i--) {
    const msgRaw = messages[i];
    const msg = msgRaw?.type === "message" && msgRaw?.message ? msgRaw.message : msgRaw;
    if (msg?.role === "assistant") {
      if (msg.stopReason === "aborted" || msg.stopReason === "error") {
        return { prompt, responseMd: "", aborted: true };
      }
      break;
    }
  }

  const turnAssistantMessages = messages.slice(lastUserIdx + 1);
  const usageMetrics = extractUsageMetricsFromMessages(turnAssistantMessages);

  for (let i = messages.length - 1; i > lastUserIdx; i--) {
    const msgRaw = messages[i];
    const msg = msgRaw?.type === "message" && msgRaw?.message ? msgRaw.message : msgRaw;
    if (msg?.role === "assistant" && msg.stopReason === "stop") {
      const text = extractTextFromContent(msg?.content);
      if (text) {
        return { prompt, responseMd: text, ...usageMetrics };
      }
    }
  }

  for (let i = messages.length - 1; i > lastUserIdx; i--) {
    const msgRaw = messages[i];
    const msg = msgRaw?.type === "message" && msgRaw?.message ? msgRaw.message : msgRaw;
    if (msg?.role === "assistant") {
      const text = extractTextFromContent(msg?.content);
      if (text) {
        return { prompt, responseMd: text, ...usageMetrics };
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
        headers: buildRequestHeaders(),
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
  let missingConfigWarned = false;
  let activeSessionId: string | undefined;
  let lastKnownSessionName: string | undefined;
  let lastKnownRepo: string | undefined;
  let latestUserPrompt: string | undefined;
  let promptInFlight = false;
  let turnTreeBefore: string | undefined;
  let turnQuotaBefore: CopilotQuotaSnapshot | undefined;
  let pendingSuggestionIds: number[] = [];

  function ensureConfiguredBaseUrl(ctx?: ExtensionContextView): string | undefined {
    const baseUrl = resolveBaseUrl();
    if (!baseUrl) {
      if (!missingConfigWarned) {
        missingConfigWarned = true;
        ctx?.ui?.notify?.(
          "omp-history: OMP_HISTORY_URL (or OMP_HISTORY_PORT) is not configured. Set OMP_HISTORY_URL (and optionally OMP_HISTORY_AUTH) to enable omp-history.",
          "warning",
        );
      }
      return undefined;
    }
    return baseUrl;
  }
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

    const baseUrl = ensureConfiguredBaseUrl(ctx);
    if (!baseUrl) return false;
    const url = `${baseUrl}${endpointPath}`;
    while (!disabled) {
      try {
        const res = await fetch(url, {
          method,
          headers: buildRequestHeaders({ "Content-Type": "application/json" }),
          body: JSON.stringify(payload),
          signal: AbortSignal.timeout(4000),
        });
        if (res.ok) {
          return true;
        }
        if (res.status === 401 || res.status === 403) {
          ctx?.ui?.notify?.(
            `omp-history authentication failed (${res.status}). Check OMP_HISTORY_AUTH environment variable.`,
            "error",
          );
          disabled = true;
          return false;
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

    const effectiveCwd = ctx?.cwd || process.cwd();
    const repo = resolveRepoName(effectiveCwd);
    const repoPath = resolveRepoRootPath(effectiveCwd);
    const name = readSessionName(ctx);

    activeSessionId = sessionId;
    lastKnownRepo = repo;
    lastKnownSessionName = name;

    await requestWithRetryPrompt(ctx, "POST", "/api/sessions", {
      sessionId,
      repo,
      repoPath,
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
      const repoPath = resolveRepoRootPath(ctx?.cwd || process.cwd());
      await requestWithRetryPrompt(ctx, "PUT", `/api/sessions/${encodeURIComponent(sessionId)}`, {
        name: currentName,
        repo: currentRepo,
        repoPath,
      });
    }
  }

  async function pollHeartbeatAndFeedback(ctx: ExtensionContextView): Promise<void> {
    if (disabled || (ctx?.agent?.kind && ctx.agent.kind !== "main")) return;

    const sessionId = readSessionId(ctx);
    if (!sessionId) return;

    const baseUrl = resolveBaseUrl();
    if (!baseUrl) return;
    const effectiveCwd = ctx?.cwd || process.cwd();
    const repo = resolveRepoName(effectiveCwd);
    const repoPath = resolveRepoRootPath(effectiveCwd);
    try {
      const res = await fetch(`${baseUrl}/api/sessions/${encodeURIComponent(sessionId)}/heartbeat`, {
        method: "POST",
        headers: buildRequestHeaders({ "Content-Type": "application/json" }),
        body: JSON.stringify({ repo, repoPath }),
        signal: AbortSignal.timeout(2500),
      });
      if (!res.ok) return;

      const data = (await res.json()) as {
        items?: Array<{ id?: string; formattedMessage?: string; comment?: string }>;
        deletedSessionIds?: string[];
      };
      if (Array.isArray(data?.deletedSessionIds) && data.deletedSessionIds.length > 0) {
        purgeDeletedLocalSessionFiles(data.deletedSessionIds, sessionId);
      }
      if (!Array.isArray(data?.items) || data.items.length === 0) return;
      for (const item of data.items) {
        const msg = (item?.formattedMessage || item?.comment || "").trim();
        if (!msg) continue;

        latestUserPrompt = msg;
        promptInFlight = true;
        turnTreeBefore = captureGitTreeSnapshot(effectiveCwd);

        if (typeof pi.sendUserMessage === "function") {
          pi.sendUserMessage(msg);
          ctx.ui?.notify?.("Received feedback from omp-history.", "info");
        } else if (typeof pi.sendMessage === "function") {
          pi.sendMessage({ content: msg, display: true }, { triggerTurn: true });
          ctx.ui?.notify?.("Received feedback from omp-history.", "info");
        }
      }
    } catch {
      // ignore transient heartbeat failure so we don't spam modal prompts
    }
  }

  pi.on("session_start", async (_event, ctx) => {
    if (ctx?.agent?.kind && ctx.agent.kind !== "main") return;
    if (!ensureConfiguredBaseUrl(ctx)) {
      return;
    }
    await syncSessionRegistration(ctx);
    const initialQuota = fetchCopilotQuotaSnapshot();
    if (initialQuota) {
      const sessionId = readSessionId(ctx);
      const baseUrl = resolveBaseUrl();
      if (sessionId && baseUrl) {
        try {
          await fetch(`${baseUrl}/api/sessions/${encodeURIComponent(sessionId)}/heartbeat`, {
            method: "POST",
            headers: buildRequestHeaders({ "Content-Type": "application/json" }),
            body: JSON.stringify({ copilotQuota: initialQuota }),
            signal: AbortSignal.timeout(2500),
          });
        } catch {
          // ignore
        }
      }
    }

    if (typeof ctx?.setInterval === "function") {
      ctx.setInterval(() => {
        void checkAndSyncSessionRename(ctx);
        void pollHeartbeatAndFeedback(ctx);
      }, 2000);
    }
  });

  pi.on("session_switch", async (_event, ctx) => {
    latestUserPrompt = undefined;
    promptInFlight = false;
    turnTreeBefore = undefined;
    turnQuotaBefore = undefined;
    pendingSuggestionIds = [];
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
        const effectiveCwd = ctx?.cwd || process.cwd();
        const repo = resolveRepoName(effectiveCwd);
        const repoPath = resolveRepoRootPath(effectiveCwd);
        void requestWithRetryPrompt(ctx, "PUT", `/api/sessions/${encodeURIComponent(sessionId)}`, {
          name: newName,
          repo,
          repoPath,
        });
      }
      return;
    }

    if (!text.startsWith("/")) {
      latestUserPrompt = text;
      turnTreeBefore = captureGitTreeSnapshot(ctx?.cwd || process.cwd());
      promptInFlight = true;
    }
  });

  pi.on("before_agent_start", async (event, ctx) => {
    if (ctx?.agent?.kind && ctx.agent.kind !== "main") return;
    if (typeof event?.prompt === "string" && event.prompt.trim().length > 0) {
      const cleaned = event.prompt
        .replace(/<system-reminder>[\s\S]*?<\/system-reminder>/g, "")
        .replace(/<system-notice>[\s\S]*?<\/system-notice>/g, "")
        .trim();
      if (cleaned) {
        latestUserPrompt = cleaned;
        promptInFlight = true;
      }
    }
    if (!turnTreeBefore) {
      turnTreeBefore = captureGitTreeSnapshot(ctx?.cwd || process.cwd());
    }
    turnQuotaBefore = fetchCopilotQuotaSnapshot();
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
      turnTreeBefore = undefined;
      return;
    }

    const fromBranch = !fromEvent ? extractLatestTurnFromMessages(branchEntries) : undefined;
    if (fromBranch?.aborted) {
      promptInFlight = false;
      turnTreeBefore = undefined;
      return;
    }

    const prompt = fromEvent?.prompt ?? fromBranch?.prompt ?? latestUserPrompt;
    const responseMd = fromEvent?.responseMd ?? fromBranch?.responseMd;

    if (!prompt || !responseMd) {
      promptInFlight = false;
      turnTreeBefore = undefined;
      return;
    }
    if (!promptInFlight && !fromEvent) return;

    promptInFlight = false;

    const effectiveCwd = ctx?.cwd || process.cwd();
    const repo = lastKnownRepo ?? resolveRepoName(effectiveCwd);
    const repoPath = resolveRepoRootPath(effectiveCwd);
    const sessionName = readSessionName(ctx);
    lastKnownSessionName = sessionName;

    const treeBefore = turnTreeBefore;
    turnTreeBefore = undefined;
    const treeAfter = captureGitTreeSnapshot(effectiveCwd);
    const diffPatch = computeGitTreeDiff(effectiveCwd, treeBefore, treeAfter);
    const turnUsage = fromEvent ?? fromBranch;
    const quotaBefore = turnQuotaBefore;
    turnQuotaBefore = undefined;
    const copilotQuota = fetchCopilotQuotaSnapshot();

    let measuredCredits = turnUsage?.creditsUsed;
    if (quotaBefore && copilotQuota && copilotQuota.entitlement === quotaBefore.entitlement) {
      const exactDelta = Math.round((copilotQuota.creditsUsed - quotaBefore.creditsUsed) * 10) / 10;
      if (exactDelta > 0) {
        measuredCredits = exactDelta;
      }
    }
    const suggestionIds = pendingSuggestionIds.length > 0 ? [...pendingSuggestionIds] : undefined;
    pendingSuggestionIds = [];

    await requestWithRetryPrompt(ctx, "POST", "/api/conversations", {
      sessionId,
      repo,
      repoPath,
      sessionName,
      prompt,
      responseMd,
      diffPatch,
      gitTreeBefore: treeBefore,
      gitTreeAfter: treeAfter,
      creditsUsed: measuredCredits,
      costUsd: turnUsage?.costUsd,
      totalTokens: turnUsage?.totalTokens,
      model: turnUsage?.model,
      copilotQuota,
      suggestionIds,
    });
  });

  async function handleResumeFromHistory(argText: string, ctx: ExtensionContextView): Promise<void> {
    const baseUrl = ensureConfiguredBaseUrl(ctx);
    if (!baseUrl) return;

    const effectiveCwd = ctx?.cwd || process.cwd();
    const currentRepo = resolveRepoName(effectiveCwd);
    const localRepoRoot = resolveRepoRootPath(effectiveCwd);
    let targetSessionId = argText.trim();
    let targetCreatedAt: string | undefined;
    let targetName: string | undefined;

    try {
      let sessions: RemoteSessionSummary[] = [];
      const repoRes = await fetch(`${baseUrl}/api/repos/${encodeURIComponent(currentRepo)}/sessions`, {
        method: "GET",
        headers: buildRequestHeaders(),
        signal: AbortSignal.timeout(5000),
      });
      if (repoRes.status === 401 || repoRes.status === 403) {
        ctx.ui?.notify?.(`omp-history authentication failed (${repoRes.status}). Check OMP_HISTORY_AUTH.`, "error");
        return;
      }
      if (repoRes.ok) {
        sessions = (await repoRes.json()) as RemoteSessionSummary[];
      }

      if (!Array.isArray(sessions) || sessions.length === 0) {
        const allRes = await fetch(`${baseUrl}/api/sessions`, {
          method: "GET",
          headers: buildRequestHeaders(),
          signal: AbortSignal.timeout(5000),
        });
        if (allRes.ok) {
          sessions = (await allRes.json()) as RemoteSessionSummary[];
        }
      }

      if (targetSessionId) {
        const matched = sessions.find(
          s =>
            s.sessionId.toLowerCase() === targetSessionId.toLowerCase() ||
            s.sessionId.toLowerCase().startsWith(targetSessionId.toLowerCase()),
        );
        if (matched) {
          targetSessionId = matched.sessionId;
          targetCreatedAt = matched.createdAt;
          targetName = matched.name;
        }
      } else {
        if (!sessions || sessions.length === 0) {
          ctx.ui?.notify?.(`No sessions found on omp-history (${baseUrl}).`, "warning");
          return;
        }
        if (!ctx?.hasUI || typeof ctx?.ui?.select !== "function") {
          ctx.ui?.notify?.("Usage: /omp-history resume <sessionId>", "warning");
          return;
        }

        const options = sessions.map(
          s => `${s.name} [${s.repo}] (${s.conversationCount} turns) — ${s.sessionId}`,
        );
        const picked = await ctx.ui.select(
          `Resume session from omp-history (${currentRepo})`,
          options,
        );
        if (!picked) return;

        const pickedIdx = options.indexOf(picked);
        const selected = pickedIdx >= 0 ? sessions[pickedIdx] : undefined;
        if (!selected) return;
        targetSessionId = selected.sessionId;
        targetCreatedAt = selected.createdAt;
        targetName = selected.name;
      }

      let currentModelSpec = "";
      try {
        const m = ctx?.models?.current?.() ?? ctx?.model;
        if (m?.provider && m?.id) {
          currentModelSpec = `${m.provider}/${m.id}`;
        } else if (m?.id) {
          currentModelSpec = m.id;
        }
      } catch {
        // ignore
      }

      const modelQuery = currentModelSpec ? `&model=${encodeURIComponent(currentModelSpec)}` : "";
      const exportUrl = `${baseUrl}/api/sessions/${encodeURIComponent(targetSessionId)}/export-jsonl?cwd=${encodeURIComponent(localRepoRoot)}${modelQuery}`;
      const exportRes = await fetch(exportUrl, {
        method: "GET",
        headers: buildRequestHeaders(),
        signal: AbortSignal.timeout(8000),
      });
      if (!exportRes.ok) {
        ctx.ui?.notify?.(
          `Could not fetch session "${targetSessionId}" from omp-history (HTTP ${exportRes.status}).`,
          "error",
        );
        return;
      }

      const jsonl = await exportRes.text();
      if (!jsonl.trim()) {
        ctx.ui?.notify?.(`Session "${targetSessionId}" returned empty transcript.`, "error");
        return;
      }

      const sessionDir = resolveLocalSessionDir(localRepoRoot, ctx?.sessionManager);
      fs.mkdirSync(sessionDir, { recursive: true });

      let targetFilePath: string | undefined;
      try {
        const existingFiles = fs.readdirSync(sessionDir);
        const existingMatch = existingFiles.find(
          f => f.endsWith(`_${targetSessionId}.jsonl`) || f === `${targetSessionId}.jsonl`,
        );
        if (existingMatch) {
          targetFilePath = path.join(sessionDir, existingMatch);
        }
      } catch {
        // ignore
      }

      if (!targetFilePath) {
        const iso = (targetCreatedAt || new Date().toISOString()).replace(/[:.]/g, "-");
        targetFilePath = path.join(sessionDir, `${iso}_${targetSessionId}.jsonl`);
      }

      fs.writeFileSync(targetFilePath, jsonl, "utf8");

      if (typeof ctx.waitForIdle === "function") {
        await ctx.waitForIdle();
      }

      if (typeof ctx.switchSession === "function") {
        const switched = await ctx.switchSession(targetFilePath);
        if (switched) {
          disabled = false;
          await syncSessionRegistration(ctx);
          ctx.ui?.notify?.(
            `Resumed session "${targetName || targetSessionId}" (${targetSessionId.slice(0, 8)}) from omp-history.`,
            "info",
          );
        } else {
          ctx.ui?.notify?.(
            `Downloaded session to ${targetFilePath}, but switching session was cancelled or rejected.`,
            "warning",
          );
        }
      } else {
        ctx.ui?.notify?.(
          `Downloaded session to ${targetFilePath}. Run: omp --resume ${targetSessionId}`,
          "info",
        );
      }
    } catch (err) {
      ctx.ui?.notify?.(
        `Failed to resume session from omp-history: ${err instanceof Error ? err.message : String(err)}`,
        "error",
      );
    }
  }

  if (typeof pi.registerTool === "function") {
    pi.registerTool({
      name: "omp_history_suggest",
      label: "Suggest Code Improvement",
      description:
        "Record a non-blocking code improvement suggestion in omp-history when you spot a genuine bug, dead code, race condition, or nonsensical logic outside the current task. Provide a human-readable summary for the user and dense technical context for a future agent.",
      parameters: {
        type: "object",
        properties: {
          title: {
            type: "string",
            description: "Short, specific title for the improvement suggestion",
          },
          humanSummary: {
            type: "string",
            description: "Clear Markdown explanation for the user describing what looks wrong and why it matters",
          },
          agentContext: {
            type: "string",
            description: "Model-optimized technical context (exact file paths, symbols, root cause, invariants, and verification steps) for a future agent to propose a fix",
          },
          filePaths: {
            type: "string",
            description: "Comma-separated relative file paths affected (optional)",
          },
          severity: {
            type: "string",
            enum: ["bug", "refactor", "perf", "dead-code", "security", "cleanup"],
            description: "Category/severity tag (optional)",
          },
        },
        required: ["title", "humanSummary", "agentContext"],
      },
      execute: async (_toolCallId, params, _signal, _onUpdate, ctx) => {
        const baseUrl = resolveBaseUrl();
        if (!baseUrl) {
          return {
            content: [{ type: "text", text: "omp-history is not configured (OMP_HISTORY_URL / OMP_HISTORY_PORT unset)." }],
          };
        }
        const effectiveCwd = ctx?.cwd || process.cwd();
        const repo = lastKnownRepo ?? resolveRepoName(effectiveCwd);
        const repoPath = resolveRepoRootPath(effectiveCwd);
        const sessionId = (ctx ? readSessionId(ctx) : undefined) ?? activeSessionId;

        const payload = {
          repo,
          repoPath,
          sessionId,
          title: typeof params?.title === "string" ? params.title : "Code improvement suggestion",
          humanSummary: typeof params?.humanSummary === "string" ? params.humanSummary : "",
          agentContext: typeof params?.agentContext === "string" ? params.agentContext : "",
          filePaths: typeof params?.filePaths === "string" ? params.filePaths : undefined,
          severity: typeof params?.severity === "string" ? params.severity : undefined,
        };

        try {
          const res = await fetch(`${baseUrl}/api/suggestions`, {
            method: "POST",
            headers: buildRequestHeaders({ "Content-Type": "application/json" }),
            body: JSON.stringify(payload),
            signal: AbortSignal.timeout(4000),
          });
          if (!res.ok) {
            return {
              content: [{ type: "text", text: `Failed to record suggestion in omp-history (HTTP ${res.status}).` }],
            };
          }
          const created = (await res.json()) as { id?: number; title?: string };
          if (typeof created?.id === "number" && created.id > 0) {
            pendingSuggestionIds.push(created.id);
          }
          return {
            content: [
              {
                type: "text",
                text: `Recorded suggestion #${created?.id ?? "?"} ("${created?.title ?? payload.title}") in omp-history for repo "${repo}".`,
              },
            ],
          };
        } catch (err) {
          return {
            content: [
              {
                type: "text",
                text: `Error recording suggestion in omp-history: ${err instanceof Error ? err.message : String(err)}`,
              },
            ],
          };
        }
      },
    });
  }

  pi.registerCommand("omp-history", {
    description:
      "Manage omp-history logging or resume remote sessions (/omp-history [resume [id]|start|enable|disable|status])",
    handler: async (args, ctx) => {
      const rawArgs = (args || "").trim();
      const [subRaw, ...rest] = rawArgs.split(/\s+/);
      const sub = (subRaw || "").toLowerCase();
      const restArgs = rest.join(" ").trim();

      if (sub === "resume") {
        await handleResumeFromHistory(restArgs, ctx);
        return;
      }

      const baseUrl = ensureConfiguredBaseUrl(ctx);
      if (!baseUrl) return;

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
        `omp-history: ${disabled ? "disabled" : "enabled"} (url=${baseUrl}, auth=${resolveAuthHeader() ? "configured" : "none"}, repo=${resolveRepoName(ctx?.cwd || process.cwd())})`,
        "info",
      );
    },
  });
}
