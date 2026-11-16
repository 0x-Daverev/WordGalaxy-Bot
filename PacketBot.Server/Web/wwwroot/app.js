"use strict";
// PacketBot web panel. Text from the game (NPC and box names) is only ever
// inserted with textContent, never as HTML.

const $ = (id) => document.getElementById(id);
let current = 1;          // selected instance number (as in /status)
let instances = [];
let settings = null;
let refreshTimer = null;

async function api(path, body) {
  const options = body === undefined
    ? { credentials: "same-origin" }
    : { method: "POST", credentials: "same-origin",
        headers: { "Content-Type": "application/json", "X-PacketBot": "1" },
        body: JSON.stringify(body) };
  const response = await fetch("/api" + path, options);
  if (response.status === 401 && path !== "/login") { showLogin(); throw new Error("401"); }
  const data = await response.json().catch(() => ({}));
  if (!response.ok && data.message) throw new Error(data.message);
  if (!response.ok) throw new Error("Error " + response.status);
  return data;
}

function toast(message, bad) {
  const t = $("toast");
  t.textContent = message;
  t.className = "toast" + (bad ? " bad" : "");
  t.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => (t.hidden = true), 4000);
}

function show(section) {
  for (const id of ["login", "empty", "panel"]) $(id).hidden = id !== section;
  $("logout").hidden = section === "login";
}

function showLogin() {
  clearInterval(refreshTimer);
  clearInterval(start.mapTimer);
  refreshTimer = null;
  show("login");
  $("password").focus();
}

async function reply(promise) {
  try {
    const r = await promise;
    toast(r.message, !r.ok);
    return r;
  } catch (e) {
    if (e.message !== "401") toast(e.message, true);
  }
}

// ---- instances and status ----

async function loadInstances() {
  instances = await api("/instances");
  if (instances.length === 0) { show("empty"); return; }
  if (!instances.some((i) => i.number === current)) current = instances[0].number;
  renderTabs();
  renderStatus();
  show("panel");
}

function renderTabs() {
  const tabs = $("tabs");
  tabs.replaceChildren(...instances.map((i) => {
    const b = document.createElement("button");
    b.textContent = i.number + ". " + i.name;
    b.setAttribute("aria-selected", String(i.number === current));
    b.addEventListener("click", async () => {
      current = i.number;
      renderTabs();
      renderStatus();
      await loadSettings();
      refreshMinimap();
      refreshItems();
    });
    return b;
  }));
}

function renderStatus() {
  const i = instances.find((x) => x.number === current);
  if (!i) return;
  $("name").textContent = i.name;
  const state = $("state");
  state.textContent = i.running ? "En marcha" : i.active ? "Arrancando" : "Parado";
  state.className = "pill" + (i.running ? " on" : "");
  $("tcp").textContent = i.tcp ? "Conectado" : "Desconectado";
  $("map").textContent = i.mapName;
  $("status").textContent = i.status;
  $("start").disabled = i.active;
  $("stop").disabled = !i.active;
}

// ---- settings ----

function fillSelect(select, options, value) {
  select.replaceChildren(...options.map(([v, label]) => {
    const o = document.createElement("option");
    o.value = v;
    o.textContent = label;
    return o;
  }));
  select.value = String(value);
}

function checklist(container, items, onChange) {
  container.replaceChildren(...items.map((item) => {
    const label = document.createElement("label");
    const box = document.createElement("input");
    box.type = "checkbox";
    box.checked = item.selected;
    box.value = item.name;
    box.addEventListener("change", onChange);
    label.append(box, document.createTextNode(item.name));
    return label;
  }));
  if (items.length === 0) {
    const p = document.createElement("p");
    p.className = "hint";
    p.textContent = "Todavía no hay nada visto en este mapa.";
    container.append(p);
  }
}

const checked = (container) =>
  [...container.querySelectorAll("input:checked")].map((c) => c.value);

async function loadSettings() {
  settings = await api("/instances/" + current + "/settings");
  const s = settings;
  const maps = [["0", "Sin seleccionar"], ...s.maps.map((m) => [m.value, m.label])];
  const ammo = s.ammunition.map((a) => [a, a.toUpperCase()]);
  fillSelect($("collect-map"), maps, s.collectMapId);
  fillSelect($("npc-map"), maps, s.npcKillerMapId);
  fillSelect($("npc-ammo"), ammo, s.npcKillerAmmunition);
  fillSelect($("gate"), [["", "Sin seleccionar"], ...s.gates.map((g) => [g, g])], s.gate);
  fillSelect($("spins"), s.spinOptions.map((n) => [String(n), String(n)]), s.spins);
  fillSelect($("gg-ammo"), ammo, s.gateAmmunition);
  $("attack").value = s.attackMode;
  for (const [id, value] of [["run-formation", s.runFormation], ["attack-formation", s.attackFormation],
                             ["final-formation", s.finalFormation]]) {
    const names = new Set(s.formations);
    if (value) names.add(value); // keep a saved one even before the game lists it
    fillSelect($(id), [["", "Ninguna"], ...[...names].map((f) => [f, f])], value);
  }
  $("formations-hint").textContent = s.formations.length
    ? "Formaciones de tu nave: " + s.formations.join(", ")
    : "Las formaciones aparecen cuando el juego las envía tras iniciar sesión.";
  checklist($("boxes"), s.boxes, () => reply(api("/instances/" + current + "/boxes", { selected: checked($("boxes")) })));
  checklist($("npcs"), s.npcs, () => reply(api("/instances/" + current + "/npcs", { selected: checked($("npcs")) })));
  checklist($("autobuy"), s.autoBuy, (e) =>
    change("autocompra", e.target.value + (e.target.checked ? " si" : " no"), false));
  $("invites").checked = s.acceptInvites;
  $("leech").checked = s.energyLeech;
  $("path").textContent = "Ajustes: " + s.path;
  renderModule(s.module);
}

function renderModule(module) {
  const value = { "Collect": "collect", "NPC Killer": "npc", "GGs": "gg" }[module] || "";
  for (const b of $("module").querySelectorAll("button"))
    b.setAttribute("aria-checked", String(b.dataset.value === value));
  for (const card of document.querySelectorAll("[data-section]"))
    card.dataset.active = String(card.dataset.section === value);
}

// key/value go through the same validation as Telegram's /ajuste.
async function change(key, value, reload = true) {
  const r = await reply(api("/instances/" + current + "/settings", { key, value }));
  if (reload || !r || !r.ok) await loadSettings();
}

function wire() {
  $("login-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    $("login-error").textContent = "";
    try {
      await api("/login", { password: $("password").value });
      $("password").value = "";
      await start();
    } catch (err) {
      $("login-error").textContent = err.message;
    }
  });
  $("logout").addEventListener("click", async () => { await api("/logout", {}); showLogin(); });
  const action = (id, path, confirmText) => $(id).addEventListener("click", async () => {
    if (confirmText && !confirm(confirmText)) return;
    await reply(api("/instances/" + current + "/" + path, {}));
    setTimeout(refresh, 600);
  });
  action("start", "start");
  action("stop", "stop");
  action("tcp-restart", "tcp");
  action("restart", "restart", "¿Reiniciar la instancia? Vuelve a iniciar sesión y deja el bot parado.");
  for (const b of $("module").querySelectorAll("button"))
    b.addEventListener("click", () => change("modulo", b.dataset.value));
  for (const select of document.querySelectorAll("select[data-key]"))
    select.addEventListener("change", () => change(select.dataset.key,
      select.value === "" && select.hasAttribute("data-formation") ? "ninguna" : select.value));
  $("invites").addEventListener("change", (e) => change("invitaciones", e.target.checked ? "si" : "no", false));
  $("leech").addEventListener("change", (e) => change("leech", e.target.checked ? "si" : "no", false));
  wireZoneDrawing();
}

// ---- minimap: the same picture as the Windows window (RenderManager) ----

const minimap = { image: null, mapId: 0, busy: false, last: null };
// Drawing preferred zones on the minimap: start/end in game coordinates.
const zoneDraw = { enabled: false, start: null, end: null, startPx: null };

function loadMapImage(mapId) {
  if (minimap.mapId === mapId) return;
  minimap.mapId = mapId;
  minimap.image = null;
  const img = new Image();
  img.onload = () => { if (minimap.mapId === mapId) minimap.image = img; };
  img.src = "/api/minimap/" + mapId + ".jpg";
}

function drawMinimap(m) {
  minimap.last = m;
  const canvas = $("minimap");
  const ratio = window.devicePixelRatio || 1;
  canvas.style.aspectRatio = m.width + " / " + m.height;
  const w = canvas.clientWidth, h = canvas.clientHeight;
  if (!w || !h) return;
  if (canvas.width !== Math.round(w * ratio)) { canvas.width = Math.round(w * ratio); canvas.height = Math.round(h * ratio); }
  const g = canvas.getContext("2d");
  g.setTransform(ratio, 0, 0, ratio, 0, 0);
  const sx = w / m.width, sy = h / m.height;
  const X = (p) => p.x * sx, Y = (p) => p.y * sy;
  g.fillStyle = "#000";
  g.fillRect(0, 0, w, h);
  if (minimap.image) g.drawImage(minimap.image, 0, 0, w, h);
  // Barriers (NoAccess zones), as the game collides with them. Inverted
  // zones block a band of `margin` around them (a thick wall when the zone
  // is a line or a point): fill that band.
  g.fillStyle = "rgba(255, 60, 60, 0.35)";
  g.strokeStyle = "rgba(255, 80, 80, 0.9)";
  g.lineWidth = 1;
  (m.barriers || []).forEach((b) => {
    g.beginPath();
    if (b.shape === "circle") {
      const r = b.inverted ? b.radius + b.margin : b.radius;
      g.ellipse(b.x * sx, b.y * sy, r * sx, r * sy, 0, 0, Math.PI * 2);
      if (b.inverted && b.radius > 0) {
        g.moveTo((b.x + b.radius) * sx, b.y * sy);
        g.ellipse(b.x * sx, b.y * sy, b.radius * sx, b.radius * sy, 0, 0, Math.PI * 2, true);
      }
    } else {
      const k = b.inverted ? b.margin : 0;
      g.rect((b.x - k) * sx, (b.y - k) * sy, (b.width + 2 * k) * sx, (b.height + 2 * k) * sy);
      if (b.inverted && b.width > 0 && b.height > 0) g.rect(b.x * sx, b.y * sy, b.width * sx, b.height * sy);
    }
    g.fill("evenodd");
    g.stroke();
  });
  // Preferred NPC Killer zones (green) and the one being drawn.
  const zone = (z, dashed) => {
    g.fillStyle = "rgba(60, 220, 120, 0.18)";
    g.strokeStyle = "rgba(90, 240, 140, 0.95)";
    g.lineWidth = 1.5;
    g.setLineDash(dashed ? [5, 4] : []);
    g.fillRect(z.x * sx, z.y * sy, z.width * sx, z.height * sy);
    g.strokeRect(z.x * sx, z.y * sy, z.width * sx, z.height * sy);
    g.setLineDash([]);
  };
  (m.preferred || []).forEach((z, i) => {
    zone(z, false);
    g.fillStyle = "rgba(90, 240, 140, 0.95)"; g.font = "bold 11px system-ui, sans-serif"; g.textAlign = "left";
    g.fillText(String(i + 1), z.x * sx + 4, z.y * sy + 13);
  });
  if (zoneDraw.start && zoneDraw.end) {
    const x = Math.min(zoneDraw.start.x, zoneDraw.end.x), y = Math.min(zoneDraw.start.y, zoneDraw.end.y);
    zone({ x, y, width: Math.abs(zoneDraw.end.x - zoneDraw.start.x), height: Math.abs(zoneDraw.end.y - zoneDraw.start.y) }, true);
  }
  // Stations of the map (bases, repair docks, hangars…).
  (m.stations || []).forEach((s) => {
    const x = s.x * sx, y = s.y * sy;
    g.fillStyle = "rgba(70, 140, 255, 0.85)"; g.strokeStyle = "#fff"; g.lineWidth = 1;
    g.fillRect(x - 5, y - 5, 10, 10); g.strokeRect(x - 5, y - 5, 10, 10);
    g.fillStyle = "#fff"; g.font = "bold 10px system-ui, sans-serif"; g.textAlign = "center";
    g.fillText(s.short, x, y - 8);
  });
  const dot = (p, r, fill, stroke) => {
    g.beginPath();
    g.arc(X(p), Y(p), r, 0, Math.PI * 2);
    if (fill) { g.fillStyle = fill; g.fill(); }
    if (stroke) { g.strokeStyle = stroke; g.lineWidth = 1.2; g.stroke(); }
  };
  if (m.destination) {
    g.strokeStyle = "#fff"; g.lineWidth = 1;
    g.beginPath(); g.moveTo(X(m.hero), Y(m.hero)); g.lineTo(X(m.destination), Y(m.destination)); g.stroke();
  }
  m.boxes.forEach((p) => dot(p, 2.2, "#ffd600"));
  m.portals.forEach((p) => dot(p, 8, null, "#fff"));
  m.npcs.forEach((p) => dot(p, 2.4, "#e53935"));
  if (m.pet) dot(m.pet, 4.5, "#00e5ff", "#fff");
  m.enemies.forEach((p) => dot(p, 3.6, "#e53935"));
  m.allies.forEach((p) => dot(p, 2.4, "#3d7bff"));
  if (m.target) dot(m.target, 5, "#ff4500");
  dot(m.hero, 5, "#fff", "#000");

  // Bars as in Windows: own ship bottom left, target bottom right.
  const bw = Math.min(200, w / 2 - 20), bh = 10;
  const bar = (x, y, value, back, front, fromRight) => {
    g.fillStyle = back; g.fillRect(x, y, bw, bh);
    const f = Math.max(0, Math.min(1, value)) * bw;
    g.fillStyle = front; g.fillRect(fromRight ? x + bw - f : x, y, f, bh);
  };
  const frac = (b) => (b && b.max > 0 ? b.value / b.max : 0);
  g.font = "bold 12px system-ui, sans-serif";
  g.fillStyle = "#fff";
  g.textAlign = "left";
  if (m.heroName) g.fillText(m.heroName, 10, h - 36);
  bar(10, h - 30, frac(m.hp), "#0b4d0b", "#7ee07e");
  bar(10, h - 16, m.shield.max > 0 ? frac(m.shield) : 1, "#0b1f66", "#8fc9ff");
  if (m.targetName) {
    g.fillStyle = "#fff"; g.textAlign = "right";
    g.fillText(m.targetName, w - 10, h - 36);
    if (!m.targetConfirmed) {
      // The server has not confirmed the selection (packet 11468).
      g.fillStyle = "#ffcc33";
      g.fillText("sin confirmar", w - 10, h - 50);
    }
    if (m.targetHp) bar(w - 10 - bw, h - 30, frac(m.targetHp), "#e57373", "#8b0000");
    if (m.targetShield) bar(w - 10 - bw, h - 16, frac(m.targetShield), "#8fc9ff", "#0b1f66");
  }
}

// Game coordinates of a pointer event on the minimap.
function gamePoint(e) {
  const m = minimap.last, rect = $("minimap").getBoundingClientRect();
  return {
    x: Math.round((e.clientX - rect.left) / rect.width * m.width),
    y: Math.round((e.clientY - rect.top) / rect.height * m.height),
  };
}

let zoneDrawingWired = false;
function wireZoneDrawing() {
  if (zoneDrawingWired) return;
  zoneDrawingWired = true;
  const canvas = $("minimap");
  $("zone-draw").addEventListener("change", (e) => {
    zoneDraw.enabled = e.target.checked;
    // While drawing, touches draw instead of scrolling the page.
    canvas.style.touchAction = zoneDraw.enabled ? "none" : "";
    canvas.style.cursor = zoneDraw.enabled ? "crosshair" : "";
  });
  $("zone-clear").addEventListener("click", async () => {
    if (!confirm("¿Borrar todas las zonas preferidas del mapa en el que está la nave?")) return;
    await reply(api("/instances/" + current + "/zones/clear", {}));
    refreshMinimap();
  });
  canvas.addEventListener("pointerdown", (e) => {
    if (!zoneDraw.enabled || !minimap.last) return;
    e.preventDefault();
    canvas.setPointerCapture(e.pointerId);
    zoneDraw.start = zoneDraw.end = gamePoint(e);
    zoneDraw.startPx = { x: e.clientX, y: e.clientY };
  });
  canvas.addEventListener("pointermove", (e) => {
    if (!zoneDraw.start) return;
    zoneDraw.end = gamePoint(e);
    drawMinimap(minimap.last);
  });
  const finish = async (e, cancelled) => {
    if (!zoneDraw.start) return;
    const start = zoneDraw.start, end = gamePoint(e);
    const moved = Math.hypot(e.clientX - zoneDraw.startPx.x, e.clientY - zoneDraw.startPx.y);
    zoneDraw.start = zoneDraw.end = zoneDraw.startPx = null;
    if (cancelled) { drawMinimap(minimap.last); return; }
    if (moved > 8)
      await reply(api("/instances/" + current + "/zones", { x1: start.x, y1: start.y, x2: end.x, y2: end.y }));
    else
      await reply(api("/instances/" + current + "/zones/remove", { x: end.x, y: end.y }));
    refreshMinimap();
  };
  canvas.addEventListener("pointerup", (e) => finish(e, false));
  canvas.addEventListener("pointercancel", (e) => finish(e, true));
}

async function refreshMinimap() {
  if (document.hidden || minimap.busy || $("panel").hidden) return;
  minimap.busy = true;
  try {
    const m = await api("/instances/" + current + "/minimap");
    $("map-name").textContent = m.mapName;
    loadMapImage(m.mapId);
    drawMinimap(m);
  } catch { /* next tick */ }
  finally { minimap.busy = false; }
}

async function refresh() {
  try { await loadInstances(); } catch { /* shown by api() */ }
}

// What the ship has (item bar), grouped by category, as /nave in Telegram.
const amountFormat = new Intl.NumberFormat("es-ES");
async function refreshItems() {
  if (!current) return;
  let items;
  try { items = await api("/instances/" + current + "/items"); } catch { return; }
  const box = $("items");
  box.replaceChildren();
  if (!items.length) {
    const p = document.createElement("p");
    p.className = "hint";
    p.textContent = "Todavía no ha llegado la barra de objetos (llega al iniciar sesión).";
    box.append(p);
    return;
  }
  let table = null;
  let category = null;
  for (const item of items) {
    if (item.category !== category) {
      category = item.category;
      const h = document.createElement("h3");
      h.textContent = category;
      table = document.createElement("table");
      box.append(h, table);
    }
    const row = table.insertRow();
    row.insertCell().textContent = item.name;
    const amount = row.insertCell();
    amount.className = "num";
    const max = item.maximum && item.maximum > 0 && item.maximum < 99999 ? " / " + amountFormat.format(item.maximum) : "";
    amount.textContent = amountFormat.format(item.amount) + max;
  }
}

async function start() {
  await loadInstances();
  if (instances.length) await loadSettings();
  clearInterval(refreshTimer);
  refreshTimer = setInterval(refresh, 3000);
  clearInterval(start.mapTimer);
  start.mapTimer = setInterval(refreshMinimap, 1000);
  refreshMinimap();
  clearInterval(start.itemsTimer);
  start.itemsTimer = setInterval(refreshItems, 5000);
  refreshItems();
}

wire();
start().catch(() => {});
