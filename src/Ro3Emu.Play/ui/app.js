// RO3Emu launcher page: shows the start flow (PlayController) and sends the player's choices back.
"use strict";
const $ = id => document.getElementById(id);
const send = msg => window.chrome?.webview?.postMessage(msg);
const order = ["client", "data", "server", "patch", "bots", "running"];
const mostBots = 0.9; // the share of fake players online before Play may be pressed
const state = { phase: "starting", bots: null, game: false, server: false, settings: null, ended: null };

// ---------- window ----------
$("drag").addEventListener("mousedown", e => { if (e.button === 0 && !e.target.closest("button")) send({ cmd: "drag" }); });
$("btn-min").onclick = () => send({ cmd: "minimize" });
$("btn-close").onclick = () => send({ cmd: "close" });

// ---------- drawers ----------
const toggle = (id, show) => { $(id).hidden = show === undefined ? !$(id).hidden : !show; };
$("btn-settings").onclick = () => { toggle("log", false); toggle("settings"); fillSettings(); };
$("btn-log").onclick = () => { toggle("settings", false); toggle("log"); scrollLog(); };
$("set-cancel").onclick = () => toggle("settings", false);
$("log-close").onclick = () => toggle("log", false);
document.querySelectorAll("[data-open]").forEach(b => b.onclick = () => send({ cmd: "open", what: b.dataset.open }));
$("set-save").onclick = () => send({
  cmd: "settings",
  bots: Number($("set-bots").value), expRate: Number($("set-exp").value),
  bonusTimeDrain: Number($("set-drain").value), crystalDropChance: Number($("set-crystal").value),
});

function fillSettings() {
  const s = state.settings;
  if (!s) return;
  $("set-bots").value = s.bots;
  $("set-exp").value = s.expRate;
  selectNearest($("set-drain"), s.bonusTimeDrain);
  selectNearest($("set-crystal"), s.crystalDropChance);
  $("set-client").textContent = s.clientDir ? "Game: " + s.clientDir : "";
  $("version").textContent = s.version ? "RO3Emu " + s.version : "";
  $("set-saved").hidden = true;
}
function selectNearest(select, value) {
  let best = select.options[0];
  for (const o of select.options) if (Math.abs(o.value - value) < Math.abs(best.value - value)) best = o;
  select.value = best.value;
}

// ---------- buttons ----------
$("btn-play").onclick = () => {
  if (state.ended !== null) { state.ended = null; send({ cmd: "start" }); clearLog(); }
  else send({ cmd: "play" });
};
$("btn-stop").onclick = () => send({ cmd: "stop" });

// ---------- the launcher's update ----------
$("btn-update").onclick = () => {
  const running = state.server && state.ended === null;
  confirmHere(`Update RO3Emu to ${state.update.version}?`
    + (running ? " The server stops first (your characters are saved)." : "")
    + " The launcher starts again when it is done.", () => {
    state.updating = true;
    showCurtain("Starting the update…");
    send({ cmd: "update" });
  });
};
function showCurtain(text) { $("curtain-text").textContent = text; $("curtain").hidden = false; }
// A question of the page's own (the flow's questions come through "ask").
function confirmHere(question, onYes) {
  $("modal-title").textContent = "Confirm";
  $("modal-text").textContent = question;
  $("modal-input").hidden = true;
  const button = (text, primary, action) => {
    const b = document.createElement("button");
    b.className = "cream" + (primary ? " primary" : "");
    b.textContent = text;
    b.onclick = () => { $("modal").hidden = true; action?.(); };
    return b;
  };
  $("modal-buttons").replaceChildren(button("Not now", false), button("Update", true, onYes));
  $("modal").hidden = false;
}

function refreshButtons() {
  const play = $("btn-play"), label = $("play-label"), sub = $("play-sub");
  let enabled = false, text = "Starting…", hint = "", ready = false;
  if (state.ended !== null) { enabled = true; text = "Start"; hint = state.ended === 0 ? "The server is stopped" : "Try again"; }
  else if (state.phase === "stopping") { text = "Stopping…"; hint = "Saving your characters"; }
  else if (state.phase === "bots") {
    // Greyed out until most fake players are in: their logins are the server's heaviest load (the game starts by
    // itself at 98%, Flows.WaitForBotsAsync).
    const { online = 0, total = 0 } = state.bots ?? {};
    enabled = total === 0 || online >= total * mostBots;
    text = enabled ? "Play now" : "Please wait";
    hint = enabled ? "Players are still logging in" : `${online} of ${total} adventurers have arrived`;
  }
  else if (state.phase === "running") {
    if (state.game) { text = "In game"; hint = "Have fun!"; }
    else { enabled = true; ready = true; text = "Play"; hint = "Start the game again"; }
  }
  play.disabled = !enabled;
  play.classList.toggle("ready", ready || state.phase === "bots" && enabled);
  label.textContent = text;
  sub.textContent = hint;
  $("btn-stop").disabled = !(state.server && state.ended === null && state.phase !== "stopping");
}

// ---------- the journey ----------
function setPhase(phase) {
  state.phase = phase;
  const at = order.indexOf(phase);
  document.querySelectorAll("#steps li").forEach((li, i) => {
    const step = order.indexOf(li.dataset.phase);
    li.classList.toggle("done", at > step || phase === "done");
    li.classList.toggle("active", at === step);
  });
  const words = {
    starting: "Preparing…", client: "Finding the game", data: "Reading the game data", server: "Starting the server",
    patch: "Patching the game for this server", bots: "Players are logging in", running: "The realm is open",
    stopping: "Stopping the server…", done: "The server is stopped", failed: "Something went wrong",
  };
  $("status-text").textContent = words[phase] ?? phase;
  if (phase === "running") showBar(1, true);
  else if (phase === "bots") showBots();
  else if (phase !== "done" && phase !== "failed") showBar(null);
  $("curtain").hidden = !state.updating && (phase !== "stopping" || !state.closing);
  refreshButtons();
}

function showBar(fraction, done = false) {
  const bar = $("bar");
  bar.classList.toggle("indeterminate", fraction === null || fraction === undefined);
  bar.classList.toggle("done", done);
  bar.classList.remove("error");
  $("bar-fill").style.width = fraction == null ? "" : Math.round(fraction * 100) + "%";
  $("status-pct").textContent = fraction == null || done ? "" : Math.round(fraction * 100) + "%";
}

function showBots() {
  if (!state.bots) { showBar(null); return; }
  const { online, total } = state.bots;
  showBar(total ? online / total : 1);
  $("detail").textContent = `${online} of ${total} adventurers have arrived`;
}

// ---------- the log ----------
const logLines = [];
function addLog(kind, text) {
  logLines.push({ kind, text });
  if (logLines.length > 400) logLines.shift();
  if (!$("log").hidden) renderLog();
}
function clearLog() { logLines.length = 0; renderLog(); }
function renderLog() {
  const ul = $("log-lines");
  ul.replaceChildren(...logLines.map(l => { const li = document.createElement("li"); li.className = l.kind; li.textContent = l.text; return li; }));
  scrollLog();
}
function scrollLog() { renderLogIfNeeded(); const ul = $("log-lines"); ul.scrollTop = ul.scrollHeight; }
function renderLogIfNeeded() { if ($("log-lines").children.length !== logLines.length) renderLog(); }

// ---------- questions ----------
function ask(m) {
  $("modal-title").textContent = m.yesNo ? "Confirm" : "Question";
  $("modal-text").textContent = m.question;
  const input = $("modal-input");
  input.hidden = m.yesNo;
  input.value = m.yesNo ? "" : m.default ?? "";
  const buttons = $("modal-buttons");
  const answer = value => { $("modal").hidden = true; send({ cmd: "answer", id: m.id, value }); };
  const button = (text, value, primary) => {
    const b = document.createElement("button");
    b.className = "cream" + (primary ? " primary" : "");
    b.textContent = text;
    b.onclick = () => answer(value());
    return b;
  };
  buttons.replaceChildren(...(m.yesNo
    ? [button("No", () => "no", m.default === "no"), button("Yes", () => "yes", m.default !== "no")]
    : [button("OK", () => input.value, true)]));
  $("modal").hidden = false;
  if (!m.yesNo) { input.focus(); input.onkeydown = e => { if (e.key === "Enter") answer(input.value); }; }
}

// ---------- status ----------
function showStatus(s) {
  state.server = s.server;
  state.game = s.game;
  const dot = $("srv-dot");
  dot.className = "dot" + (s.server ? (s.busy > 0.85 ? " busy" : " on") : "");
  $("srv-text").textContent = s.server ? `Online · ${s.tps.toFixed(1)} TPS` : "Offline";
  $("srv-load").style.width = s.server ? Math.min(100, Math.round(s.busy * 100)) + "%" : "0";
  $("srv-load-text").textContent = s.server ? Math.round(s.busy * 100) + "%" : "–";
  $("srv-uptime").textContent = s.server ? duration(s.uptime) : "–";
  $("players").textContent = s.server ? s.inWorld : 0;
  refreshButtons();
}
function duration(seconds) {
  const h = Math.floor(seconds / 3600), m = Math.floor(seconds % 3600 / 60);
  return h > 0 ? `${h} h ${String(m).padStart(2, "0")} min` : `${m} min`;
}
function showSettings(s) {
  state.settings = s;
  $("rate-exp").textContent = "×" + s.expRate;
  $("rate-auto").textContent = s.bonusTimeDrain === 0 ? "∞" : s.bonusTimeDrain >= 1 ? "1×" : Math.round(1 / s.bonusTimeDrain) + "×";
  $("rate-crystal").textContent = s.crystalDropChance > 0 ? Math.round(s.crystalDropChance * 100) + "%" : "Off";
  if (!state.bots) $("bots-text").textContent = s.bots > 0 ? `0 / ${s.bots}` : "Off";
  if (!$("settings").hidden) fillSettings();
}

// ---------- messages from the window ----------
window.chrome?.webview?.addEventListener("message", e => {
  const m = e.data;
  switch (m.type) {
    case "line":
      addLog(m.kind, m.text);
      if (m.kind !== "title") {
        const d = $("detail");
        d.textContent = m.text;
        d.className = "detail" + (m.kind === "warn" ? " warn" : m.kind === "error" ? " error" : "");
      }
      if (m.kind === "error") { $("bar").classList.add("error"); toggle("log", true); renderLog(); }
      break;
    case "progress":
      showBar(m.fraction ?? null);
      $("detail").textContent = m.text;
      break;
    case "progress-end": if (state.phase !== "bots") showBar(1, true); break;
    case "phase": setPhase(m.phase); break;
    case "bots":
      state.bots = { online: m.online, total: m.total };
      $("bots-text").textContent = `${m.online} / ${m.total}`;
      $("bots-bar").style.width = (m.total ? Math.round(m.online * 100 / m.total) : 0) + "%";
      if (state.phase === "bots") { showBots(); refreshButtons(); }
      break;
    case "status": showStatus(m); break;
    case "settings": showSettings(m); break;
    case "saved": $("set-saved").hidden = false; break;
    case "ask": ask(m); break;
    case "ended":
      state.ended = m.code;
      setPhase(m.code === 0 ? "done" : "failed");
      if (m.code !== 0) $("bar").classList.add("error");
      break;
    case "closing": state.closing = true; $("curtain").hidden = false; break;
    case "update":
      state.update = m;
      $("update-text").textContent = `RO3Emu ${m.version} is out (you have ${m.current}).`;
      $("update").hidden = false;
      break;
    case "updating": showCurtain(m.text); break;
    case "update-failed":
      state.updating = false;
      $("curtain").hidden = true;
      $("update-text").textContent = "The update did not work: " + m.text;
      break;
  }
});

send({ cmd: "ready" });
