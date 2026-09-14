/* Optional, allowlisted interaction counts. No autocapture, replay or identity. */
(() => {
  "use strict";
  const rawEndpoint = window.__ATLAS__?.usageAnalytics?.endpoint;
  let endpoint = "";
  try {
    const url = new URL(rawEndpoint);
    if (url.protocol === "https:" && !url.username && !url.password && !url.search && !url.hash) endpoint = url.href;
  } catch { /* Disabled for absent or invalid configuration. */ }
  const storageKey = "atlas-usage-consent-v1";
  let consent = false, step = 0, pending = [], timer = null, started = 0;
  const inflight = new Set(), cells = Array(24).fill(0), counts = new Map();
  const views = new Set(["landscape", "systems", "capabilities", "roadmaps", "lifecycle", "reviews"]);
  let lastView = "landscape";
  const controls = new Map([
    ["refresh", "refresh"], ["newAsset", "new-asset"], ["contextPack", "context-pack"],
    ["aiChatButton", "ai-open"], ["setupCopilot", "getting-started"], ["export", "export"],
    ["import", "import"], ["pasteLandscape", "paste-landscape"]
  ]);

  const style = document.createElement("style");
  style.textContent = `
    .usage-dialog { width: min(580px, 94vw); max-height: 85vh; overflow: auto; border: 1px solid #d0d5dd; border-radius: 12px; padding: 24px; color: #202734; }
    .usage-dialog::backdrop { background: rgba(16,24,40,.4); }
    .usage-dialog button { margin: 6px 8px 6px 0; padding: 8px 12px; cursor: pointer; }
    .usage-grid { display: grid; grid-template-columns: repeat(6,1fr); gap: 3px; }
    .usage-grid span { text-align: center; padding: 8px; border: 1px solid #d0d5dd; }
    #usagePrivacy { margin-left: 8px; cursor: pointer; }
  `;
  document.head.appendChild(style);
  const trigger = document.createElement("button");
  trigger.id = "usagePrivacy"; trigger.type = "button"; trigger.textContent = "Usage privacy";
  document.querySelector("body > header")?.appendChild(trigger);
  const dialog = document.createElement("dialog");
  dialog.className = "usage-dialog"; dialog.id = "usageConsent";
  dialog.setAttribute("aria-labelledby", "usageTitle");
  dialog.innerHTML = `<h2 id="usageTitle">Optional usage data</h2>
    <p id="usageExplanation"></p><p id="usageDestination"></p>
    <button id="usageDecline" autofocus>Decline</button><button id="usageAccept">Allow usage data</button>
    <button id="usageWithdraw">Withdraw consent</button><button id="usageClose">Close</button>
    <details id="usageDashboard"><summary>This tab’s heatmap and navigation counts</summary>
      <p>Coarse click cells across the landscape area (6 × 4). Counts stay in this tab’s memory.</p>
      <div class="usage-grid" id="usageGrid"></div><p id="usageFunnel"></p><ul id="usageCounts"></ul>
    </details>`;
  document.body.appendChild(dialog);
  const el = id => document.getElementById(id);
  const writeChoice = accepted => {
    try { sessionStorage.setItem(storageKey, JSON.stringify({ endpoint, accepted })); } catch { /* Memory-only consent remains valid. */ }
  };
  function clear() {
    pending = []; clearTimeout(timer); timer = null;
    for (const request of inflight) request.abort();
    inflight.clear(); cells.fill(0); counts.clear(); step = 0;
  }
  function decline() { consent = false; clear(); writeChoice(false); }
  function updateDialog() {
    el("usageExplanation").textContent = !endpoint ? "Usage collection is disabled by the operator. Nothing is collected or sent." :
      "May Atlas collect click and navigation counts to improve the map? Only fixed control labels, coarse click cells, step numbers and broad timing buckets are sent. No landscape content, names, asset IDs, tenant data, input text or session identifiers are included. The app works fully if you decline.";
    el("usageDestination").textContent = endpoint ? "Operator-configured receiver: " + endpoint + ". Like any web request, transport metadata is visible to the receiver." : "";
    el("usageAccept").hidden = !endpoint || consent;
    el("usageDecline").hidden = consent || !endpoint;
    el("usageWithdraw").hidden = !consent;
    el("usageDashboard").hidden = !consent;
    renderDashboard();
  }
  function openDialog() {
    updateDialog(); if (!dialog.open) dialog.showModal();
    (consent ? el("usageWithdraw") : endpoint ? el("usageDecline") : el("usageClose")).focus();
  }
  trigger.addEventListener("click", openDialog);
  el("usageDecline").addEventListener("click", () => { decline(); dialog.close(); });
  el("usageAccept").addEventListener("click", () => {
    if (!endpoint) return;
    clear(); consent = true; started = performance.now(); writeChoice(true); dialog.close();
  });
  el("usageWithdraw").addEventListener("click", () => { decline(); updateDialog(); el("usageClose").focus(); });
  el("usageClose").addEventListener("click", () => { if (!consent) decline(); dialog.close(); });
  dialog.addEventListener("cancel", () => { if (!consent) decline(); });

  function renderDashboard() {
    el("usageGrid").replaceChildren();
    const max = Math.max(1, ...cells);
    cells.forEach((count, index) => {
      const cell = document.createElement("span"); cell.textContent = String(count);
      cell.title = "Cell " + index;
      cell.style.background = `rgba(76,100,220,${count / max * .55})`;
      el("usageGrid").appendChild(cell);
    });
    el("usageCounts").replaceChildren();
    for (const [control, count] of counts) {
      const li = document.createElement("li"); li.textContent = `${control}: ${count}`; el("usageCounts").appendChild(li);
    }
    el("usageFunnel").textContent = `Find → inspect: search ${counts.get("search") || 0} → asset opens ${counts.get("asset-open") || 0} → dependency views ${counts.get("dependencies-visible") || 0}`;
  }
  async function flush() {
    timer = null;
    if (!consent || !endpoint || !pending.length) return;
    const events = pending.splice(0, 20), controller = new AbortController();
    inflight.add(controller);
    const timeout = setTimeout(() => controller.abort(), 5000);
    try {
      await fetch(endpoint, { method: "POST", credentials: "omit", referrerPolicy: "no-referrer", redirect: "error",
        cache: "no-store", mode: "cors", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ version: 1, events }), signal: controller.signal });
    } catch { /* Best-effort counts never affect catalogue use; no retries or persistence. */ }
    finally { clearTimeout(timeout); inflight.delete(controller); }
    if (consent && pending.length && !timer) timer = setTimeout(flush, 500);
  }
  function record(control, from, to, cell = null) {
    if (!consent || step >= 250) return;
    const seconds = (performance.now() - started) / 1000;
    pending.push({ view: from, control, from, to, step: ++step,
      elapsed: seconds < 5 ? "under-5s" : seconds < 30 ? "5-30s" : "over-30s", cell });
    counts.set(control, (counts.get(control) || 0) + 1);
    if (cell !== null) cells[cell]++;
    if (!timer) timer = setTimeout(flush, 500);
    if (pending.length > 20) pending.shift();
  }
  function currentView() {
    const selected = document.querySelector('[data-route][aria-current="page"]')?.dataset.route;
    if (views.has(selected)) return selected;
    return document.querySelector('[data-view="table"][aria-pressed="true"]') ? "systems" : lastView;
  }
  document.addEventListener("click", event => {
    if (!consent || !(event.target instanceof Element) || event.target.closest(".usage-dialog, #usagePrivacy")) return;
    const from = currentView(), routeButton = event.target.closest("[data-route], [data-view]");
    if (routeButton) {
      const to = routeButton.dataset.route || ({ graph: "landscape", table: "systems" })[routeButton.dataset.view];
      if (views.has(to)) { record("view", from, to); lastView = to; } return;
    }
    const button = event.target.closest("button");
    if (button && controls.has(button.id)) { record(controls.get(button.id), from, from); return; }
    if (event.target.closest("#toolbar .chip")) { record("kind-filter", from, from); return; }
    const canvas = document.getElementById("canvas");
    if (!canvas?.contains(event.target)) return;
    const bounds = canvas.getBoundingClientRect();
    const cell = event.detail > 0 && bounds.width > 0 && bounds.height > 0
      ? Math.min(3, Math.max(0, Math.floor((event.clientY - bounds.top) / bounds.height * 4))) * 6 +
        Math.min(5, Math.max(0, Math.floor((event.clientX - bounds.left) / bounds.width * 6))) : null;
    const asset = event.target.closest(".node, .asset-table tbody tr");
    record(asset ? "asset-open" : "canvas", from, asset ? "details" : from, cell);
    if (asset) setTimeout(() => {
      if (document.querySelector("#detail .dep-chain")) record("dependencies-visible", from, "details");
    }, 0);
  }, true);
  el("search")?.addEventListener("change", () => record("search", currentView(), currentView()));
  window.addEventListener("pagehide", () => { pending = []; clearTimeout(timer); timer = null; });
  // Consent is scoped to this tab and exact endpoint; changing the destination requires new consent.
  let choice = null;
  try { choice = JSON.parse(sessionStorage.getItem(storageKey)); } catch { /* Default is decline. */ }
  if (endpoint && choice?.endpoint === endpoint) {
    consent = choice.accepted === true; started = performance.now();
  } else if (endpoint) openDialog();
})();
