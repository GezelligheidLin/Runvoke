import type { LogEntry } from "../types";

export type LogMode = "append" | "progress" | "finish";
export type IncomingLog = Omit<LogEntry, "id" | "transient"> & {
  mode?: LogMode;
};

const LOG_MESSAGE_LIMIT = 16_000;

function cleanMessage(message: string) {
  // Backend output is already parsed; keep this guard for legacy events.
  const stripped = message
    .replace(/\u001B\][^\u0007]*(?:\u0007|\u001B\\)/g, "")
    .replace(/\u001B\[[0-?]*[ -/]*[@-~]/g, "");
  return stripped.length > LOG_MESSAGE_LIMIT
    ? `${stripped.slice(0, LOG_MESSAGE_LIMIT)}… [单条日志过长，已截断]`
    : stripped;
}

/** Apply one event to the caller's run-specific log array. */
export function applyLogEntry(
  entries: LogEntry[],
  incoming: IncomingLog,
  createId: () => string,
) {
  const { mode = "append", ...payload } = incoming;
  const message = cleanMessage(payload.message);
  const effectiveMode: LogMode =
    mode === "append" && /\[webpack\.Progress\]\s+\d{1,3}%(?:\s|$)/i.test(message)
      ? "progress"
      : mode;
  let activeIndex = -1;
  for (let index = entries.length - 1; index >= 0; index--) {
    if (entries[index]?.transient && entries[index]?.stream === payload.stream) {
      activeIndex = index;
      break;
    }
  }
  const active = entries[activeIndex];
  const replacing = effectiveMode !== "append" && active !== undefined;
  const entry: LogEntry = {
    ...payload,
    message,
    id: replacing ? active.id : createId(),
    transient: effectiveMode === "progress",
  };
  if (replacing) entries[activeIndex] = entry;
  else {
    if (active) entries[activeIndex] = { ...active, transient: false };
    entries.push(entry);
  }
}
