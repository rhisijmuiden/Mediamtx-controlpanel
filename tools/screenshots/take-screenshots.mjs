// Takes the README screenshots (docs/screenshots/*.png) from a demo panel started with start-demo.sh.
// Creates demo users and streams through the UI, publishes a test picture with ffmpeg when it is
// installed, and photographs the main pages.
//   PANEL_URL (default http://127.0.0.1:5080), ADMIN_EMAIL, ADMIN_PASSWORD, OUT_DIR, CHROMIUM_PATH
import { chromium } from "playwright";
import { spawn, spawnSync } from "node:child_process";
import { mkdirSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const base = (process.env.PANEL_URL ?? "http://127.0.0.1:5080").replace(/\/$/, "");
const email = process.env.ADMIN_EMAIL ?? "admin@example.com";
const password = process.env.ADMIN_PASSWORD ?? "Admin12345";
const outDir = process.env.OUT_DIR ?? path.resolve(here, "../../docs/screenshots");
mkdirSync(outDir, { recursive: true });

const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1, locale: "en-GB" });
const page = await context.newPage();
const children = [];

const go = async (url) => {
    await page.goto(base + url, { waitUntil: "networkidle" });
    await settle();
};
// Wait for the interactive (Blazor Server) page to be connected and rendered.
const settle = async (ms = 1200) => {
    await page.waitForLoadState("networkidle").catch(() => {});
    await page.waitForTimeout(ms);
};
const shot = async (name, options = {}) => {
    await page.screenshot({ path: path.join(outDir, `${name}.png`), ...options });
    console.log(`  ${name}.png`);
};

// ---------------------------------------------------------------- log in (in English)
await page.goto(`${base}/api/language?code=en&returnUrl=Account/Login`);
await settle(500);
await shot("login");
await page.fill("#Input\\.Email", email);
await page.fill("#Input\\.Password", password);
await Promise.all([page.waitForURL((u) => !u.pathname.toLowerCase().includes("login")), page.click(".content form button.btn-primary[type=submit]")]);
console.log("Logged in:", page.url());
await settle();

// ---------------------------------------------------------------- demo data
async function createUser(mail, name, role) {
    await go(`/users/new?role=${role}`);
    await page.fill("#email", mail);
    await page.fill("#dn", name);
    await page.click("text=Generate");
    await page.click(".content button[type=submit]");
    await page.waitForURL(/\/users$/, { timeout: 15000 }).catch(() => {});
}

async function createStream(name, displayName, { owner, pull, isPublic } = {}) {
    await go("/streams/new");
    await page.fill("#name", name);
    await page.fill("#displayName", displayName);
    if (pull) {
        await page.check("#src-pull");
        await settle(300);
        await page.fill("#sourceUrl", pull);
    }
    if (isPublic) await page.check("#public");
    if (owner) await page.selectOption("#owner", { label: owner }).catch(() => {});
    await page.click(".content button[type=submit]");
    await page.waitForURL(/\/streams\/\d+/, { timeout: 15000 });
    await settle();
    return page.url();
}

const existing = await (await page.request.get(`${base}/streams`)).text();
let demoUrl;
if (!existing.includes("Main stage")) {
    console.log("Creating demo data…");
    await createUser("studio@example.com", "Studio crew", "User");
    await createUser("events@example.com", "Acme Events", "Customer");
    demoUrl = await createStream("demo", "Main stage", { owner: "events@example.com (Customer)", isPublic: true });
    await createStream("lobby-cam", "Lobby camera", { pull: "rtsp://camera.local:554/stream1" });
    await createStream("events/keynote", "Keynote");
}

// ---------------------------------------------------------------- live test picture
await go("/streams");
await page.click("text=Main stage");
await page.waitForURL(/\/streams\/\d+/);
await settle();
demoUrl ??= page.url();
const streamKey = await page.locator("label:has-text('Stream key') ~ .input-group input, label:has-text('Stream key') + .input-group input").first().inputValue().catch(() => null);
if (streamKey && spawnSync("ffmpeg", ["-version"]).status === 0) {
    console.log("Publishing a test picture…");
    const ff = spawn("ffmpeg", ["-loglevel", "error", "-re",
        "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=25", "-f", "lavfi", "-i", "sine=frequency=440",
        "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-g", "50", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-f", "flv", `rtmp://127.0.0.1:1935/${streamKey}`], { stdio: "ignore" });
    children.push(ff);
    await page.waitForTimeout(6000);
}

// ---------------------------------------------------------------- screenshots
console.log("Screenshots:");
await go("/");
await page.waitForTimeout(5500); // the dashboard refreshes live data every 5 seconds
await shot("dashboard");

await go("/streams");
await shot("streams");

await go(demoUrl.replace(base, ""));
await page.waitForTimeout(1500);
await shot("stream-details");
const watchHref = await page.locator("a[href*='watch/']").first().getAttribute("href").catch(() => null);

await go(new URL(demoUrl).pathname + "/embed");
await page.waitForTimeout(4000); // the preview connects to the stream
await shot("embed-designer");

await go("/streams/new");
await shot("stream-new");

await go("/servers");
await shot("servers");

await go("/users");
await shot("users");

await go("/settings");
await shot("settings-general");
await go("/settings/web");
await shot("settings-web");
await go("/settings/mail");
await shot("settings-mail");
await go("/settings/sso");
await shot("settings-sso");
await go("/settings/alerts");
await shot("settings-alerts");
await go("/settings/backup");
await shot("settings-backup");

await go("/");
await page.click(".sidebar-footer button:has-text('Info')");
await page.waitForSelector(".app-modal");
await page.waitForTimeout(400);
await shot("info");
await page.keyboard.press("Escape");

await go("/portal");
await page.waitForTimeout(2000);
await shot("customer-portal");
const portalHref = await page.locator(".card", { hasText: "Main stage" }).locator("a[href*='portal/streams/']").first().getAttribute("href");
await go("/" + portalHref.replace(/^\//, ""));
await page.waitForTimeout(5000); // live preview and statistics
await shot("customer-stream");

if (watchHref) {
    const watchPage = await context.newPage();
    await watchPage.goto(new URL(watchHref, base + "/").toString(), { waitUntil: "networkidle" });
    await watchPage.waitForTimeout(4000);
    await watchPage.screenshot({ path: path.join(outDir, "watch-page.png") });
    console.log("  watch-page.png");
    await watchPage.close();
}

// The same dashboard in Dutch, to show the language menu.
await page.goto(`${base}/api/language?code=nl&returnUrl=`);
await settle();
await page.click(".lang-menu summary");
await page.waitForTimeout(300);
await shot("dashboard-nl");
await page.goto(`${base}/api/language?code=en&returnUrl=`);

for (const child of children) child.kill("SIGTERM");
await browser.close();
