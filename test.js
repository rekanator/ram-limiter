// Runs the extension against a fake browser: node test.js
const assert = require("assert");
const fs = require("fs");
const vm = require("vm");

const load = (file, context) =>
  vm.runInNewContext(fs.readFileSync(`${__dirname}/extension/${file}`, "utf8"), context);
const settle = () => new Promise((resolve) => setTimeout(resolve, 100));

async function run({ enabled = true, used, interval = 60, grace = 0, never = [], protectTyped = false,
  typed = [], protectBusy = false, busy = [], left = {}, switches = [], jumps = [], reason, outage }) {
  const tab = (id, lastAccessed, extra = {}) =>
    ({ id, windowId: 1, lastAccessed, title: `Tab ${id}`, url: `https://www.site${id}.com/x`, ...extra });
  const tabs = [
    tab(1, 50), tab(2, 10), tab(3, 30),
    tab(4, 1, { active: true }), tab(5, 2, { pinned: true }), tab(6, 3, { audible: true }),
    tab(7, 4, { discarded: true }), tab(8, 5, { autoDiscardable: false }),
  ];
  const unloaded = [];
  const jumped = [];
  const queue = [...jumps];
  const reports = [];
  const timers = [];
  const pending = []; // what each recorded timer would run
  const store = { left };
  const local = {};
  let alarm, activated, installed, alarmFired;
  let closed = false, resumed = false;
  const noop = { addListener() {} };
  const context = {
    console, URL, Date, btoa,
    OffscreenCanvas: class { constructor() { throw new Error("no canvas here"); } },
    // The 3s settle wait runs at once; other timers are only recorded.
    setTimeout: (fn, ms) => ([500, 3000].includes(ms) ? setImmediate(fn) : (pending.push(fn), timers.push(ms))),
    clearTimeout: () => {},
    fetch: async (url, request) => {
      if (url.endsWith("/jump")) {
        if (!queue.length) return new Promise(() => {}); // held open, like the app
        const jump = queue.shift();
        return { json: async () => jump };
      }
      if (closed) throw new Error("app closed");
      reports.push(JSON.parse(request.body));
      return { json: async () => ({ enabled, limit: 3, used: used - unloaded.length, interval, grace, never, protectTyped, protectBusy }) };
    },
    chrome: {
      tabs: {
        query: async (q) => tabs.filter((t) => !q.active || t.active),
        discard: async (id) => { unloaded.push(id); tabs.find((t) => t.id === id).discarded = true; },
        onActivated: { addListener: (fn) => { activated = fn; } },
        onRemoved: noop,
        update: async (id) => jumped.push(["show", id]),
        create: async ({ url }) => jumped.push(["open", url]),
      },
      windows: { update: async () => {} },
      scripting: { executeScript: async ({ target, world }) =>
        [{ result: (world === "MAIN" ? busy : typed).includes(target.tabId) }] },
      storage: { local: {
        get: async () => ({ ...local }),
        set: async (value) => Object.assign(local, value),
        remove: async (key) => delete local[key],
      }, session: {
        get: async () => structuredClone(store),
        set: async (value) => Object.assign(store, structuredClone(value)),
      } },
      alarms: { get: async () => alarm, create: async (_, a) => { alarm = a; },
        onAlarm: { addListener: (fn) => { alarmFired = fn; } } },
      runtime: { onInstalled: { addListener: (fn) => { installed = fn; } }, onStartup: noop, getPlatformInfo: async () => {} },
    },
  };
  load("background.js", context);
  for (const tabId of switches) await activated({ tabId, windowId: 1 });
  await settle();
  if (reason) {
    await installed({ reason });
    await settle();
  }
  if (outage) {
    closed = true; // the short timer runs while the app is closed
    await pending.at(-1)();
    closed = false;
    const before = reports.length;
    alarmFired();
    await settle();
    resumed = reports.length > before;
  }
  // Copied so results compare equal outside the sandbox.
  return JSON.parse(JSON.stringify({ unloaded, jumped, reports, timers, store, resumed, period: alarm.periodInMinutes }));
}

// typed.js against a fake page.
function typedPage() {
  const listeners = {};
  const context = { addEventListener: (type, fn) => { listeners[type] = fn; } };
  context.globalThis = context;
  load("typed.js", context);
  const field = (props, attributes = {}) => ({
    isConnected: true, value: "hello", getAttribute: (n) => attributes[n] ?? null, closest: () => null, ...props,
  });
  const type = (el, isTrusted = true) => listeners.input({ isTrusted, composedPath: () => [el] });
  return { has: () => context.ramLimiterHasTyped(), field, type, submit: (target) => listeners.submit({ target }) };
}

(async () => {
  const unloads = async (options) => (await run(options)).unloaded;
  assert.deepStrictEqual(await unloads({ enabled: false, used: 9 }), [], "off: nothing unloads");
  assert.deepStrictEqual(await unloads({ used: 3 }), [], "at limit: nothing unloads");
  assert.deepStrictEqual(await unloads({ used: 5 }), [2, 3], "stops once under, oldest first");
  assert.deepStrictEqual(await unloads({ used: 99 }), [2, 3, 1], "protected tabs stay");
  assert.deepStrictEqual(await unloads({ used: 99, never: ["site2.com"] }), [3, 1], "never-unload site stays");
  assert.deepStrictEqual(await unloads({ used: 99, grace: 300, left: { 2: Date.now() } }), [3, 1],
    "just-left tab stays during grace period");
  assert.deepStrictEqual(await unloads({ used: 99, grace: 300, left: { 2: Date.now() - 600000 } }), [3, 1, 2],
    "grace period expires; order follows when you left");
  assert.deepStrictEqual(await unloads({ used: 99, protectTyped: true, typed: [2] }), [3, 1], "typed text stays");
  assert.deepStrictEqual(await unloads({ used: 99, typed: [2] }), [2, 3, 1], "protection can be switched off");

  const switched = await run({ used: 1, switches: [2, 3] });
  assert.ok(switched.store.left[4] && switched.store.left[2], "records the tabs you leave");
  assert.ok(!switched.store.left[3], "current tab not marked as left");

  const logged = await run({ used: 4 });
  assert.deepStrictEqual(logged.reports.flatMap((r) => r.unloaded.map((u) => [u.title, u.site])),
    [["Tab 2", "site2.com"]], "each unload is reported once");
  assert.deepStrictEqual(logged.reports[0].tabs, { loaded: 7, total: 8 }, "sends tab counts");

  for (const [interval, period, timer] of [[10, 0.5, 10000], [20, 0.5, 20000], [30, 0.5], [60, 1]]) {
    const result = await run({ enabled: false, used: 1, interval });
    assert.strictEqual(result.period, period, `${interval}s alarm`);
    assert.strictEqual(result.timers.at(-1) ?? undefined, timer, `${interval}s timer`);
  }

  assert.ok((await run({ enabled: false, used: 1, interval: 10, outage: true })).resumed,
    "checks start again after the app was closed");

  const clicked = await run({ used: 1, jumps: [
    { url: "https://www.site2.com/x", tab: 2 }, { url: "https://www.site3.com/x", tab: 999 },
    { url: "https://closed.com/", tab: 1 }, {},
  ] });
  assert.deepStrictEqual(clicked.jumped, [["show", 2], ["show", 3], ["open", "https://closed.com/"]],
    "log click shows the tab, or reopens a closed page");
  assert.deepStrictEqual(logged.reports.flatMap((r) => r.unloaded).map((u) => [u.url, u.tab]),
    [["https://www.site2.com/x", 2]], "log entry carries address and tab");

  assert.deepStrictEqual(await unloads({ used: 99, protectBusy: true, busy: [2] }), [3, 1], "busy tab stays");
  assert.deepStrictEqual(await unloads({ used: 99, busy: [2] }), [2, 3, 1], "busy protection can be switched off");
  const flags = async (reason) => (await run({ used: 1, reason })).reports.map((r) => r.reloaded);
  assert.deepStrictEqual(await flags(), [false], "ordinary check isn't a reload");
  assert.deepStrictEqual((await flags("update")).filter(Boolean), [true], "a reload is reported once");
  assert.ok(!(await flags("chrome_update")).includes(true), "a browser update isn't a reload");

  const page = typedPage();
  assert.strictEqual(page.has(), false, "untouched page");
  page.type(page.field({ tagName: "INPUT", type: "text" }, { name: "q" }));
  page.type(page.field({ tagName: "INPUT", type: "password" }));
  page.type(page.field({ tagName: "TEXTAREA" }), false);
  assert.strictEqual(page.has(), false, "search, password and script input ignored");
  const box = page.field({ tagName: "TEXTAREA" });
  page.type(box);
  assert.strictEqual(page.has(), true, "typed draft");
  box.value = "";
  assert.strictEqual(page.has(), false, "emptied draft");
  box.value = "again";
  page.submit({ contains: (el) => el === box });
  assert.strictEqual(page.has(), false, "submitted draft");
  page.type(page.field({ isContentEditable: true, value: undefined, textContent: "mail body" }));
  assert.strictEqual(page.has(), true, "rich text editor");

  console.log("all tests passed");
})();
