// Tests for the report relay:  node test.mjs [path\to\KHHotfixCli.exe]
// Runs worker.js locally against a fake Discord. With the hotfix CLI it also sends real hotfix reports through it.
import http from "node:http";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import worker, { cleanPayload } from "./worker.js";

let failed = 0;
async function check(name, fn) {
  try { await fn(); console.log("  PASS  " + name); }
  catch (e) { failed++; console.log("  FAIL  " + name + "\n        " + String(e && e.message).split("\n").join("\n        ")); }
}

// ---- fake Discord: webhook 1 is a forum channel (posts need a title), webhook 2 a text channel (no titles) ----------
const received = [];
const discord = http.createServer(async (req, res) => {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  const hook = (req.url.match(/\/webhooks\/(\d+)\//) || [])[1];
  const send = (status, obj) => { res.writeHead(status, { "content-type": "application/json" }); res.end(JSON.stringify(obj)); };
  if (req.method === "GET") return send(200, { id: hook, type: 1, name: "Hook " + hook, channel_id: "50" + hook, guild_id: "777", token: "SECRET-TOKEN-" + hook });
  const form = await new Request("http://x/", { method: "POST", headers: { "content-type": req.headers["content-type"] }, body: Buffer.concat(chunks) }).formData();
  const payload = JSON.parse(form.get("payload_json"));
  const files = [];
  for (const [key, v] of form.entries()) if (key !== "payload_json") files.push({ key, name: v.name, size: v.size, bytes: Buffer.from(await v.arrayBuffer()) });
  received.push({ hook, url: req.url, payload, files });
  if (hook === "1" && !payload.thread_name) return send(400, { code: 220001, message: "Webhooks posted to forum channels must have a thread_name or thread_id" });
  if (hook === "2" && payload.thread_name) return send(400, { code: 220003, message: "Webhooks can only create threads in forum channels" });
  send(200, { id: "90" + received.length, channel_id: payload.thread_name ? "60" + received.length : "50" + hook, webhook_id: hook, content: payload.content });
});
await new Promise((r) => discord.listen(0, "127.0.0.1", r));
const dport = discord.address().port;

let allowed = Infinity;
const env = {
  BUG_WEBHOOK: `http://127.0.0.1:${dport}/api/webhooks/1/SECRET-TOKEN-1\n`,   // a pasted secret may end with a line break
  LOG_WEBHOOK: `http://127.0.0.1:${dport}/api/webhooks/2/SECRET-TOKEN-2`,
  LIMITER: { limit: async () => ({ success: allowed-- > 0 }) },
};
const UA = "KH-Hotfix (https://github.com/Gasteren/KH-Hotfix) test";

function report(route, payload, files = [], headers = {}) {
  const fd = new FormData();
  fd.append("payload_json", typeof payload === "string" ? payload : JSON.stringify(payload));
  files.forEach((f, i) => fd.append(`files[${i}]`, new Blob([f.data]), f.name));
  return new Request("https://relay.test" + route + "?wait=true", { method: "POST", body: fd, headers: { "user-agent": UA, "cf-connecting-ip": "203.0.113.5", ...headers } });
}

console.log("Report relay (worker.js) with a fake Discord");
await check("a bug report is forwarded to the bug webhook as a forum post, files intact", async () => {
  received.length = 0;
  const shot = Buffer.from([0xff, 0xd8, 0, 1, 2, 255, 13, 10]);
  const r = await worker.fetch(report("/bug", {
    username: "Sora (KH Hotfix)", thread_name: "[KH1] Test", content: "hello <@123> @everyone",
    embeds: [{ title: "T", color: 15158332, fields: [{ name: "Game", value: "KH1", inline: true }], image: { url: "attachment://screenshot.jpg" } }],
    allowed_mentions: { parse: ["everyone"] }, tts: true, avatar_url: "https://evil.example/a.png",
  }, [{ name: "screenshot.jpg", data: shot }, { name: "session.log", data: Buffer.from("log line\r\n") }]), env);
  assert.equal(r.status, 200);
  const body = await r.json();
  assert.deepEqual(Object.keys(body).sort(), ["channel_id", "id"]);
  assert.equal(received.length, 1);
  const m = received[0];
  assert.equal(m.hook, "1");
  assert.match(m.url, /\?wait=true$/);
  assert.deepEqual(m.payload.allowed_mentions, { parse: [] });
  assert.equal(m.payload.tts, undefined);
  assert.equal(m.payload.avatar_url, undefined);
  assert.equal(m.payload.thread_name, "[KH1] Test");
  assert.equal(m.payload.content, "hello <@123> @everyone");
  assert.equal(m.payload.embeds[0].image.url, "attachment://screenshot.jpg");
  assert.deepEqual(m.files.map((f) => [f.key, f.name, f.size]), [["files[0]", "screenshot.jpg", shot.length], ["files[1]", "session.log", 10]]);
  assert.deepEqual(m.files[0].bytes, shot);
});
await check("Discord's answer is passed on (a text channel refuses post titles: code 220003)", async () => {
  const r = await worker.fetch(report("/log", { content: "x", thread_name: "title" }), env);
  assert.equal(r.status, 400);
  assert.equal((await r.json()).code, 220003);
});
await check("the webhook check shows name, channel and server - never the token", async () => {
  const r = await worker.fetch(new Request("https://relay.test/bug", { headers: { "user-agent": UA } }), env);
  const t = await r.text();
  assert.equal(r.status, 200);
  assert.deepEqual(JSON.parse(t), { id: "1", channel_id: "501", guild_id: "777", name: "Hook 1" });
  assert.doesNotMatch(t, /SECRET/);
});
await check("pings, external images and fields reports never use are removed", async () => {
  const p = cleanPayload({
    username: "@everyone discord", content: "a".repeat(3000), components: [1], flags: 4,
    embeds: [{ title: "x", image: { url: "https://evil.example/x.png" }, thumbnail: { url: "https://evil" }, footer: { text: "f", icon_url: "https://evil" }, author: { name: "a" } }, { title: "second" }],
    applied_tags: ["123456789012", "evil", 98765432101],
  });
  assert.equal(p.username, "everyone");
  assert.equal(p.content.length, 2000);
  assert.equal(p.components, undefined);
  assert.equal(p.flags, undefined);
  assert.deepEqual(p.embeds, [{ title: "x", footer: { text: "f" } }]);
  assert.deepEqual(p.applied_tags, ["123456789012", "98765432101"]);
  assert.deepEqual(p.allowed_mentions, { parse: [] });
});
await check("only the hotfix may send: other programs are refused", async () => {
  received.length = 0;
  const r = await worker.fetch(report("/bug", { content: "spam" }, [], { "user-agent": "curl/8.0" }), env);
  assert.equal(r.status, 403);
  assert.equal(received.length, 0);
});
await check("unknown endpoints and methods are refused", async () => {
  assert.equal((await worker.fetch(new Request("https://relay.test/", { headers: { "user-agent": UA } }), env)).status, 404);
  assert.equal((await worker.fetch(new Request("https://relay.test/api/webhooks/1/x", { headers: { "user-agent": UA } }), env)).status, 404);
  assert.equal((await worker.fetch(new Request("https://relay.test/bug", { method: "PUT", body: "x", headers: { "user-agent": UA } }), env)).status, 405);
});
await check("rate limit per sender: answered with 429 and retry_after", async () => {
  received.length = 0;
  allowed = 1;
  const first = await worker.fetch(report("/bug", { content: "1", thread_name: "t" }), env);
  const second = await worker.fetch(report("/bug", { content: "2", thread_name: "t" }), env);
  allowed = Infinity;
  assert.equal(first.status, 200);
  assert.equal(second.status, 429);
  assert.equal((await second.json()).retry_after, 60);
  assert.equal(received.length, 1);
});
await check("reports over Discord's 10 MB limit are refused before they reach Discord", async () => {
  received.length = 0;
  const r = await worker.fetch(report("/log", { content: "big" }, [{ name: "dump.dmp.gz", data: Buffer.alloc(10 * 1024 * 1024 + 1) }]), env);
  assert.equal(r.status, 413);
  assert.equal((await r.json()).code, 40005);
  assert.equal(received.length, 0);
});
await check("broken requests are refused", async () => {
  assert.equal((await worker.fetch(report("/bug", "not json"), env)).status, 400);
  assert.equal((await worker.fetch(new Request("https://relay.test/bug", { method: "POST", body: "x", headers: { "user-agent": UA, "content-type": "text/plain" } }), env)).status, 400);
});
await check("a relay without its webhook secrets says so", async () => {
  const r = await worker.fetch(report("/bug", { content: "x" }), { LIMITER: env.LIMITER });
  assert.equal(r.status, 503);
});

// ---- the real hotfix (KHHotfixCli.exe) sending through the relay -------------------------------------------------
const cli = process.argv[2];
if (cli) {
  console.log("Hotfix -> relay -> Discord (" + path.basename(cli) + ")");
  const relay = http.createServer(async (req, res) => {
    const chunks = [];
    for await (const c of req) chunks.push(c);
    const headers = {};
    for (const h of ["user-agent", "content-type", "content-length"]) if (req.headers[h]) headers[h] = req.headers[h];
    const request = new Request("http://127.0.0.1" + req.url, { method: req.method, headers, body: req.method === "GET" ? undefined : Buffer.concat(chunks) });
    const r = await worker.fetch(request, env);
    res.writeHead(r.status, { "content-type": "application/json" });
    res.end(Buffer.from(await r.arrayBuffer()));
  });
  await new Promise((r) => relay.listen(0, "127.0.0.1", r));
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "khh-relaytest-"));
  fs.copyFileSync(cli, path.join(dir, "KHHotfixCli.exe"));
  fs.writeFileSync(path.join(dir, "KHHotfix.ini"), `ReportRelay=http://127.0.0.1:${relay.address().port}\r\nShowDialogs=0\r\n`);
  const run = (...args) => new Promise((resolve) => {
    const p = spawn(path.join(dir, "KHHotfixCli.exe"), args, { cwd: dir, env: { ...process.env, KHHOTFIX_INSTANCE: ".relaytest" } });
    let out = "";
    p.stdout.on("data", (d) => (out += d));
    p.stderr.on("data", (d) => (out += d));
    p.on("close", (code) => resolve({ code, out }));
  });
  try {
    await check("--discord-test checks both relay endpoints without posting", async () => {
      received.length = 0;
      const r = await run("--discord-test");
      assert.equal(r.code, 0, r.out);
      assert.match(r.out, /Bug reports: relay 127\.0\.0\.1:\d+\/bug OK - "Hook 1"/);
      assert.match(r.out, /Crash and session reports: relay 127\.0\.0\.1:\d+\/log OK - "Hook 2"/);
      assert.doesNotMatch(r.out, /SECRET/);
      assert.equal(received.length, 0);
    });
    await check("a bug report from the hotfix arrives as a forum post", async () => {
      received.length = 0;
      const r = await run("--report-bug", "--send", "--title", "Relay test", "--text", "Sent through the relay <@1>", "--game", "KH1", "--category", "Sound", "--name", "Tester");
      assert.equal(r.code, 0, r.out);
      assert.equal(received.length, 1, JSON.stringify(received.map((x) => x.payload)));
      const m = received[0];
      assert.equal(m.hook, "1");
      assert.equal(m.payload.thread_name, "[KH1] Relay test");
      assert.equal(m.payload.username, "Tester (KH Hotfix)");
      assert.equal(m.payload.content, "Sent through the relay <@1>");
      assert.deepEqual(m.payload.allowed_mentions, { parse: [] });
      assert.ok(m.files.some((f) => f.name === "system.txt"), "system.txt attached");
    });
    await check("crash and session reports reach the text channel (the post title is dropped after Discord's answer)", async () => {
      received.length = 0;
      const r = await run("--discord-test", "--send");
      assert.equal(r.code, 0, r.out);
      const logs = received.filter((x) => x.hook === "2");
      assert.equal(logs.length, 2);
      assert.ok(logs[0].payload.thread_name && logs[1].payload.thread_name === undefined);
      assert.match(logs[1].payload.content, /Test - crash and session reports arrive here/);
    });
  } finally {
    relay.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

discord.close();
console.log(failed === 0 ? "ALL RELAY TESTS PASSED" : failed + " RELAY TEST(S) FAILED");
process.exit(failed === 0 ? 0 : 1);
