const STATUS_URL = "http://127.0.0.1:47831/status";
const JUMP_URL = "http://127.0.0.1:47831/jump";
const SETTLE_MS = 3000; // time for memory to drop after an unload
const BADGE_MS = 500; // time for the browser to pick up the sleeping badge
const TYPED_TIMEOUT_MS = 2000;

let running = false;
let timer; // only used for intervals shorter than the alarm minimum
let unloaded = []; // not yet sent to the menu bar app's log
let tracking = Promise.resolve();

// "mail.google.com" for web pages, "" for anything else.
function site(tab) {
  try {
    const url = new URL(tab.url);
    return /^https?:$/.test(url.protocol) ? url.hostname.replace(/^www\./, "") : "";
  } catch {
    return "";
  }
}

// Reports to the menu bar app (on this Mac only) and gets the settings back.
async function status() {
  try {
    const tabs = await chrome.tabs.query({});
    const sent = unloaded.length;
    const { reloaded = false } = await chrome.storage.local.get("reloaded");
    const body = JSON.stringify({
      reloaded, // brings back a browser removed in the app
      tabs: { loaded: tabs.filter((t) => !t.discarded).length, total: tabs.length },
      sites: [...new Set(tabs.map(site).filter(Boolean))],
      unloaded: unloaded.slice(0, sent),
    });
    const response = await fetch(STATUS_URL, { method: "POST", body, cache: "no-store" });
    const s = await response.json();
    unloaded.splice(0, sent);
    if (reloaded) await chrome.storage.local.remove("reloaded");
    await schedule(s.interval);
    return s;
  } catch {
    // The app isn't running. Forget the short timer, or the alarm would think it is
    // still counting and checks would never start again.
    clearTimeout(timer);
    timer = undefined;
    return null;
  }
}

// Alarms can't fire more often than every 30s. Below that, a timer does the
// checking; calling an extension API each time keeps the worker awake.
async function schedule(seconds = 60) {
  const period = Math.max(seconds, 30) / 60;
  const alarm = await chrome.alarms.get("check");
  if (alarm?.periodInMinutes !== period) {
    await chrome.alarms.create("check", { periodInMinutes: period });
  }
  clearTimeout(timer);
  timer = seconds < 30 ? setTimeout(enforce, seconds * 1000) : undefined;
}

// Notes when you leave each tab, for the grace period. Changes run one at a time.
function track(change) {
  tracking = tracking.then(async () => {
    const { active = {}, left = {} } = await chrome.storage.session.get(["active", "left"]);
    await change(active, left);
    await chrome.storage.session.set({ active, left });
  }).catch(console.error);
  return tracking;
}

// Longest-unviewed tab that is safe to unload.
async function oldestTab(settings, tried) {
  await tracking;
  const { left = {} } = await chrome.storage.session.get("left");
  const viewed = (t) => Math.max(left[t.id] ?? 0, t.lastAccessed ?? 0);
  const recent = Date.now() - settings.grace * 1000;
  const tabs = await chrome.tabs.query({});
  return tabs
    .filter((t) => !t.active && !t.pinned && !t.audible && !t.discarded &&
      t.autoDiscardable !== false && !tried.has(t.id) &&
      !settings.never.includes(site(t)) &&
      (!settings.grace || viewed(t) <= recent))
    .sort((a, b) => viewed(a) - viewed(b))[0];
}

// Asks every frame of a tab a yes-or-no question.
async function ask(tabId, world, func) {
  const check = chrome.scripting
    .executeScript({ target: { tabId, allFrames: true }, world, func })
    .then((frames) => frames.some((f) => f.result), () => false); // pages scripts can't enter
  // A tab too busy to answer is left alone.
  const timeout = new Promise((resolve) => setTimeout(() => resolve(true), TYPED_TIMEOUT_MS));
  return Promise.race([check, timeout]);
}

const hasTyped = (tabId) => ask(tabId, "ISOLATED", () => globalThis.ramLimiterHasTyped?.() === true);
// In a call or uploading. busy.js lives in the page's own world.
const isBusy = (tabId) => ask(tabId, "MAIN", () => globalThis.ramLimiterBusy?.() === true);

// The site's icon, from the tab or else from the browser's own icon cache.
async function siteIcon(tab) {
  const cached = () => chrome.runtime.getURL(`/_favicon/?pageUrl=${encodeURIComponent(tab.url)}&size=32`);
  for (const source of [() => tab.favIconUrl, cached]) {
    try {
      const response = await fetch(source());
      if (response.ok) return await createImageBitmap(await response.blob());
    } catch {} // missing, or a format that can't be drawn here
  }
  return null;
}

// The site's icon with a purple moon badge, as a data URL. The icon is never
// replaced outright: if it can't be drawn, a letter tile stands in for it.
async function badgeIcon(tab) {
  const canvas = new OffscreenCanvas(32, 32);
  const g = canvas.getContext("2d");
  const icon = await siteIcon(tab);
  if (icon) {
    g.drawImage(icon, 0, 0, 28, 28);
  } else {
    g.fillStyle = "#5F5E5A";
    g.fillRect(0, 0, 28, 28);
    g.fillStyle = "#FFFFFF";
    g.font = "bold 20px sans-serif";
    g.textAlign = "center";
    g.textBaseline = "middle";
    g.fillText((site(tab)[0] ?? "?").toUpperCase(), 14, 15);
  }
  const [x, y, r] = [22, 22, 10];
  const circle = (cx, cy, radius, color) => {
    g.fillStyle = color;
    g.beginPath();
    g.arc(cx, cy, radius, 0, 2 * Math.PI);
    g.fill();
  };
  circle(x, y, r, "#534AB7");
  circle(x - r * 0.05, y, r * 0.6, "#FFFFFF");
  circle(x + r * 0.28, y - r * 0.2, r * 0.52, "#534AB7"); // the bite makes a crescent
  const bytes = new Uint8Array(await (await canvas.convertToBlob()).arrayBuffer());
  return `data:image/png;base64,${btoa(String.fromCharCode(...bytes))}`;
}

// Runs in the page. Reloading the tab brings the normal icon back by itself.
function setIcon(href) {
  if (href) {
    const old = [...document.querySelectorAll("link[rel~='icon']")];
    const added = Object.assign(document.createElement("link"), { rel: "icon", href });
    globalThis.ramLimiterIcon = { old, added };
    for (const link of old) link.remove();
    document.head.append(added);
  } else if (globalThis.ramLimiterIcon) {
    globalThis.ramLimiterIcon.added.remove();
    document.head.append(...globalThis.ramLimiterIcon.old);
  }
}

// Best effort: some pages don't allow it, and the tab is unloaded regardless.
async function mark(tab, on) {
  try {
    const href = on ? await badgeIcon(tab) : null;
    const done = chrome.scripting.executeScript({ target: { tabId: tab.id }, func: setIcon, args: [href] });
    await Promise.race([done, new Promise((resolve) => setTimeout(resolve, TYPED_TIMEOUT_MS))]);
    if (on) await new Promise((resolve) => setTimeout(resolve, BADGE_MS));
  } catch {}
}

// A clicked log entry: go to that tab, or reopen the page if it was closed.
async function openTab({ url, tab }) {
  const tabs = await chrome.tabs.query({});
  const match = tabs.find((t) => t.id === tab && t.url === url) ?? tabs.find((t) => t.url === url);
  if (!match) return chrome.tabs.create({ url });
  await chrome.tabs.update(match.id, { active: true });
  await chrome.windows.update(match.windowId, { focused: true });
}

// The app holds each request until something is clicked (20s at most).
async function listenForClicks() {
  for (;;) {
    try {
      const jump = await (await fetch(JUMP_URL, { method: "POST", body: "{}", cache: "no-store" })).json();
      if (jump.url) await openTab(jump);
      await chrome.runtime.getPlatformInfo(); // keeps the worker awake
    } catch {
      await new Promise((resolve) => setTimeout(resolve, 5000));
    }
  }
}

async function enforce() {
  if (running) return;
  running = true;
  try {
    const tried = new Set();
    // Re-read each round so switching it off stops it immediately.
    for (let s = await status(); s?.enabled && s.used > s.limit; s = await status()) {
      const tab = await oldestTab(s, tried);
      if (!tab) break;
      tried.add(tab.id);
      if (s.protectTyped && (await hasTyped(tab.id))) continue;
      if (s.protectBusy && (await isBusy(tab.id))) continue;
      await mark(tab, true);
      let gone;
      try {
        gone = await chrome.tabs.discard(tab.id);
      } catch {
        await mark(tab, false);
        continue; // tab closed or can't be unloaded
      }
      unloaded.push({
        time: Date.now(), title: tab.title || "Untitled", site: site(tab), url: tab.url,
        tab: gone?.id ?? tab.id,
      });
      await new Promise((resolve) => setTimeout(resolve, SETTLE_MS));
    }
  } finally {
    running = false;
  }
}

async function start() {
  track(async (active) => {
    for (const tab of await chrome.tabs.query({ active: true })) active[tab.windowId] ??= tab.id;
  });
  if (!(await chrome.alarms.get("check"))) await schedule();
  await enforce();
}

// Tabs already open when the extension loads need the page watchers added.
async function watchOpenTabs() {
  for (const tab of await chrome.tabs.query({ discarded: false })) {
    for (const [file, world] of [["typed.js", "ISOLATED"], ["busy.js", "MAIN"]]) {
      chrome.scripting
        ?.executeScript({ target: { tabId: tab.id, allFrames: true }, world, files: [file] })
        .catch(() => {});
    }
  }
}

chrome.tabs.onActivated.addListener(({ tabId, windowId }) => track((active, left) => {
  if (active[windowId] !== undefined && active[windowId] !== tabId) left[active[windowId]] = Date.now();
  active[windowId] = tabId;
}));
chrome.tabs.onRemoved.addListener((tabId) => track((_, left) => { delete left[tabId]; }));
// With the short timer running, the alarm only needs to wake a stopped worker.
chrome.alarms.onAlarm.addListener(() => { if (!timer) enforce(); });
// Installing or reloading the extension (not a browser update).
chrome.runtime.onInstalled.addListener(async ({ reason }) => {
  if (reason !== "chrome_update") {
    await chrome.storage.local.set({ reloaded: true });
    await status();
  }
  watchOpenTabs();
  start();
});
chrome.runtime.onStartup.addListener(start);
start();
listenForClicks();
