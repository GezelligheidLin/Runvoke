import assert from "node:assert/strict";
import { test } from "node:test";
import { applyLogEntry, type IncomingLog } from "./logEntries.ts";
import type { LogEntry } from "../types";

function recorder() {
  const runs = new Map<string, LogEntry[]>();
  let sequence = 0;
  return {
    runs,
    send(message: string, mode: IncomingLog["mode"] = "append", stream: LogEntry["stream"] = "stdout", runId = "run-1") {
      const entries = runs.get(runId) ?? [];
      applyLogEntry(entries, { message, mode, stream, runId, projectId: "project-1", timestamp: ++sequence }, () => `log-${sequence}`);
      runs.set(runId, entries);
      return entries;
    },
  };
}

test("progress preserves its row ID, position and newest timestamp across interleaved streams", () => {
  const { send } = recorder();
  const entries = send("10%", "progress", "stderr");
  const id = entries[0]!.id;
  send("ordinary");
  send("other progress", "progress");
  send("20%", "progress", "stderr");
  assert.equal(entries.length, 3);
  assert.equal(entries[0]!.id, id);
  assert.equal(entries[0]!.timestamp, 4);
  send("100%", "finish", "stderr");
  assert.equal(entries[0]!.id, id);
  assert.equal(entries[0]!.message, "100%");
  assert.equal(entries[0]!.transient, false);
  send("error", "append", "stderr");
  send("next build", "progress", "stderr");
  assert.equal(entries.length, 5);
});

test("ordinary percentages stay separate, while existing webpack progress remains compatible", () => {
  const { send } = recorder();
  send("10% business report");
  const entries = send("20% business report");
  assert.equal(entries.length, 2);
  send("[webpack.Progress] 10% building");
  send("[webpack.Progress] 90% sealing");
  assert.equal(entries.length, 3);
  send("compiled");
  send("[webpack.Progress] 5% next build");
  assert.equal(entries.length, 5);
  assert.equal(entries[2]!.transient, false);
});

test("run isolation, clearing mid-progress and empty clear-line frames", () => {
  const { runs, send } = recorder();
  send("10%", "progress");
  send("other run", "progress", "stdout", "run-2");
  send("", "progress");
  assert.equal(runs.get("run-1")![0]!.message, "");
  assert.equal(runs.get("run-2")![0]!.message, "other run");
  runs.set("run-1", []);
  const entries = send("20%", "progress");
  const id = entries[0]!.id;
  send("done", "finish");
  assert.equal(entries.length, 1);
  assert.equal(entries[0]!.id, id);
  assert.equal(entries[0]!.message, "done");
});

test("high-frequency progress uses one row and link text reflects only the latest frame", () => {
  const { send } = recorder();
  const entries = send("Local: http://localhost:8020", "progress");
  const id = entries[0]!.id;
  for (let index = 0; index < 10_000; index++) send(`${index}%`, "progress");
  assert.equal(entries.length, 1);
  assert.equal(entries[0]!.id, id);
  assert.equal(entries[0]!.message, "9999%");
  assert.ok(!entries[0]!.message.includes("localhost"));
});
