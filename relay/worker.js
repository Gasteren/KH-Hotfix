// KH Hotfix report relay - a Cloudflare Worker.
//
// Every copy of the hotfix sends its bug reports to /bug and its crash and session reports to /log, in the format of
// Discord's webhook API. The relay forwards them to the maintainer's Discord webhooks. The webhook URLs stay secret:
// they are stored in the Worker (secrets BUG_WEBHOOK and LOG_WEBHOOK, see Deploy-Relay.ps1), never in the hotfix.
// Only report-shaped messages pass: no pings, no external links in embeds, Discord's size limits, and a rate limit
// per sender.

const MAX_BYTES = 10 * 1024 * 1024;
const ROUTES = { "/bug": "BUG_WEBHOOK", "/log": "LOG_WEBHOOK" };

const json = (status, body) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
const text = (v, max) => (typeof v === "string" ? v.slice(0, max) : undefined);

function cleanEmbed(e) {
  if (!e || typeof e !== "object") return null;
  const out = {};
  if (typeof e.title === "string") out.title = e.title.slice(0, 256);
  if (typeof e.description === "string") out.description = e.description.slice(0, 4096);
  if (Number.isInteger(e.color) && e.color >= 0 && e.color <= 0xffffff) out.color = e.color;
  if (typeof e.timestamp === "string" && !Number.isNaN(Date.parse(e.timestamp))) out.timestamp = e.timestamp;
  if (Array.isArray(e.fields)) {
    out.fields = e.fields
      .filter((f) => f && typeof f.name === "string" && typeof f.value === "string")
      .slice(0, 25)
      .map((f) => ({ name: f.name.slice(0, 256), value: f.value.slice(0, 1024), inline: f.inline === true }));
  }
  if (e.footer && typeof e.footer.text === "string") out.footer = { text: e.footer.text.slice(0, 2048) };
  // Only the report's own screenshot: no images or links from elsewhere.
  if (e.image && typeof e.image.url === "string" && /^attachment:\/\/[\w.-]+$/.test(e.image.url)) out.image = { url: e.image.url };
  return out;
}

// Only the parts of a message that hotfix reports use; mentions can never ping anyone.
export function cleanPayload(p) {
  const username = (text(p.username, 80) || "").replace(/@|#|:|```|discord|clyde/gi, "").trim();
  const out = { content: text(p.content, 2000) || "", username: username || "KH Hotfix", allowed_mentions: { parse: [] } };
  if (typeof p.thread_name === "string" && p.thread_name.trim()) out.thread_name = p.thread_name.slice(0, 100);
  if (Array.isArray(p.applied_tags)) out.applied_tags = p.applied_tags.map(String).filter((t) => /^\d{5,25}$/.test(t)).slice(0, 5);
  if (Array.isArray(p.embeds)) out.embeds = p.embeds.slice(0, 1).map(cleanEmbed).filter(Boolean);
  return out;
}

// Discord's answer without anything else from the webhook.
async function answer(resp) {
  let body = {};
  try { body = await resp.json(); } catch { }
  const keep = {};
  for (const k of ["id", "channel_id", "guild_id", "name", "code", "message", "retry_after", "global"]) if (body[k] !== undefined) keep[k] = body[k];
  return json(resp.status, keep);
}

export default {
  async fetch(request, env) {
    const path = new URL(request.url).pathname.replace(/\/+$/, "");
    const secret = ROUTES[path];
    if (!secret) return json(404, { message: "Unknown endpoint", code: 0 });
    if (!(request.headers.get("user-agent") || "").startsWith("KH-Hotfix")) return json(403, { message: "Only the KH Hotfix can send reports", code: 0 });
    const hook = (env[secret] || "").trim();
    if (!hook) return json(503, { message: "Reporting is not set up on this relay", code: 0 });
    if (env.LIMITER) {
      const { success } = await env.LIMITER.limit({ key: request.headers.get("cf-connecting-ip") || "unknown" });
      if (!success) return json(429, { message: "Too many reports from this address, try again in a minute", retry_after: 60, code: 0 });
    }

    // Webhook check: its name, channel and server - never its token.
    if (request.method === "GET") return answer(await fetch(hook));
    if (request.method !== "POST") return json(405, { message: "Method not allowed", code: 0 });

    if (Number(request.headers.get("content-length") || 0) > MAX_BYTES) return json(413, { message: "Report too large", code: 40005 });
    let form, payload;
    try {
      form = await request.formData();
      payload = JSON.parse(form.get("payload_json"));
      if (!payload || typeof payload !== "object") throw new Error("payload_json is not an object");
    } catch {
      return json(400, { message: "Expected a report (multipart/form-data with payload_json)", code: 50035 });
    }
    const out = new FormData();
    out.append("payload_json", JSON.stringify(cleanPayload(payload)));
    let files = 0, bytes = 0;
    for (const [key, value] of form.entries()) {
      if (!/^files\[\d\]$/.test(key) || typeof value === "string") continue;
      if (++files > 10) break;
      bytes += value.size;
      out.append(key, value, value.name);
    }
    if (bytes > MAX_BYTES) return json(413, { message: "Report too large", code: 40005 });
    return answer(await fetch(hook + "?wait=true", { method: "POST", body: out }));
  },
};
