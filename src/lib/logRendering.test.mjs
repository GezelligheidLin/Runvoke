import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import * as Vue from "vue";
import { compileTemplate, parse } from "vue/compiler-sfc";
import { applyLogEntry } from "./logEntries.ts";

// Compile the actual log row so its memo dependencies are covered by this test.
const { descriptor } = parse(readFileSync(new URL("../App.vue", import.meta.url), "utf8"));
const row = descriptor.template.content.match(/<div\s+v-for="entry in visibleLogs"[\s\S]*?<\/div>/)?.[0];
assert.ok(row, "App.vue must contain the log row template");
const compiled = compileTemplate({
  source: `<div>${row}</div>`, filename: "log-row.vue", id: "log-row-test",
  compilerOptions: { mode: "function" },
});
assert.deepEqual(compiled.errors, []);
const render = new Function("Vue", compiled.code)(Vue);

function element(type, text = "") {
  return { type, text, children: [], parent: null, props: {} };
}
const renderer = Vue.createRenderer({
  createElement: element,
  createText: (text) => element("text", text),
  createComment: (text) => element("comment", text),
  setText: (node, text) => { node.text = text; },
  setElementText: (node, text) => { node.text = text; node.children = []; },
  patchProp: (node, key, _previous, value) => { node.props[key] = value; },
  parentNode: (node) => node.parent,
  nextSibling: (node) => node.parent?.children[node.parent.children.indexOf(node) + 1] ?? null,
  insert(node, parent, anchor = null) {
    if (node.parent) this.remove(node);
    const index = anchor ? parent.children.indexOf(anchor) : parent.children.length;
    parent.children.splice(index, 0, node);
    node.parent = parent;
  },
  remove(node) {
    node.parent?.children.splice(node.parent.children.indexOf(node), 1);
    node.parent = null;
  },
});

function textContent(node) {
  return (node.type === "comment" ? "" : node.text) + node.children.map(textContent).join("");
}

test("actual Vue log row refreshes text, time and links while preserving its element", async () => {
  const visibleLogs = Vue.ref([]);
  const logLinkAction = Vue.ref("open");
  const app = renderer.createApp({
    setup: () => ({ visibleLogs, logLinkAction, formatTime: String,
      splitLogMessage: (message) => [{ text: message, url: message.startsWith("http") ? message : undefined }],
      isLocalhostUrl: () => true, handleLogLink: () => {},
    }),
    render,
  });
  const root = element("root");
  const send = async (message, timestamp, mode = "progress") => {
    applyLogEntry(visibleLogs.value, { message, timestamp, mode, stream: "stderr", runId: "run-1", projectId: "project-1" }, () => "progress-row");
    await Vue.nextTick();
  };
  app.mount(root);
  try {
    await send("29% building", 1);
    const rowNode = root.children[0].children.find((node) => node.props["data-log-entry-id"] === "progress-row");
    assert.ok(rowNode);
    assert.equal(textContent(rowNode), "1stderr29% building");
    await send("90% sealing", 2);
    assert.equal(textContent(rowNode), "2stderr90% sealing");
    await send("http://localhost:8020", 3);
    const button = rowNode.children.find((node) => node.type === "pre").children.find((node) => node.type === "button");
    assert.ok(button);
    assert.equal(button.props.title, "使用默认浏览器打开");
    logLinkAction.value = "copy";
    await Vue.nextTick();
    assert.equal(button.props.title, "复制链接");
    await send("100% done", 4, "finish");
    assert.equal(textContent(rowNode), "4stderr100% done");
    assert.equal(root.children[0].children.filter((node) => node.type === "div").length, 1);
    assert.equal(root.children[0].children.find((node) => node.type === "div"), rowNode);
  } finally {
    app.unmount();
  }
});
