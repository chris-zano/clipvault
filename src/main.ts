import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";

type Entry = {
  id: number;
  kind: "text" | "image";
  preview: string;
  truncated: boolean;
  bytes: number;
  pinned: boolean;
  created_at: number;
  last_used_at: number;
  use_count: number;
  width: number | null;
  height: number | null;
  thumb: string | null;
};

type VaultItem = { id: number; label: string; created_at: number };
type VaultStatus = { exists: boolean; unlocked: boolean; seconds_until_lock: number | null };

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

const search = $<HTMLInputElement>("search");
const list = $<HTMLUListElement>("list");
const status = $<HTMLElement>("status");
const gateError = $<HTMLElement>("gate-error");

let tab: "history" | "vault" = "history";
let entries: Entry[] = [];
let vaultItems: VaultItem[] = [];
let selected = 0;
let vault: VaultStatus = { exists: false, unlocked: false, seconds_until_lock: null };

const win = getCurrentWindow();

function say(msg: string, ms = 2200) {
  status.textContent = msg;
  if (ms) window.setTimeout(() => { if (status.textContent === msg) status.textContent = ""; }, ms);
}

function ago(ts: number) {
  const s = Math.max(0, Math.floor(Date.now() / 1000) - ts);
  if (s < 60) return "just now";
  if (s < 3600) return `${Math.floor(s / 60)}m ago`;
  if (s < 86400) return `${Math.floor(s / 3600)}h ago`;
  return `${Math.floor(s / 86400)}d ago`;
}

function size(bytes: number) {
  return bytes < 1024 ? `${bytes} B` : `${(bytes / 1024).toFixed(1)} KB`;
}

// ---- history ------------------------------------------------------------

async function refreshHistory() {
  entries = await invoke<Entry[]>("list_entries", { query: search.value, limit: 300 });
  if (selected >= entries.length) selected = Math.max(0, entries.length - 1);
  renderHistory();
}

function renderHistory() {
  $("empty").classList.toggle("hidden", entries.length > 0);
  list.replaceChildren(
    ...entries.map((e, i) => {
      const li = document.createElement("li");
      li.className = "row" + (i === selected ? " sel" : "");
      li.tabIndex = -1;

      if (e.kind === "image" && e.thumb) {
        const thumb = document.createElement("img");
        thumb.className = "thumb";
        thumb.src = e.thumb;
        thumb.alt = e.preview;
        thumb.title = "Click to copy";
        thumb.onclick = () => copy(e.id);
        li.append(thumb);
      }

      const text = document.createElement("span");
      text.className = "text" + (e.kind === "image" ? " image" : "");
      text.textContent = e.preview + (e.truncated ? " …" : "");
      text.title = "Click to copy";
      text.onclick = () => copy(e.id);

      const meta = document.createElement("span");
      meta.className = "meta";
      meta.textContent = `${size(e.bytes)} · ${ago(e.last_used_at)}`;

      const actions = document.createElement("span");
      actions.className = "actions";

      const pin = document.createElement("button");
      pin.textContent = e.pinned ? "★" : "☆";
      pin.title = e.pinned ? "Unpin" : "Pin";
      if (e.pinned) pin.classList.add("pinned");
      pin.onclick = async (ev) => {
        ev.stopPropagation();
        await invoke("set_pinned", { id: e.id, pinned: !e.pinned });
        refreshHistory();
      };

      const stash = document.createElement("button");
      stash.textContent = "🔒";
      stash.title = "Move into vault";
      stash.onclick = (ev) => { ev.stopPropagation(); absorb(e); };

      const del = document.createElement("button");
      del.textContent = "✕";
      del.title = "Delete";
      del.className = "danger";
      del.onclick = async (ev) => {
        ev.stopPropagation();
        await invoke("delete_entry", { id: e.id });
        refreshHistory();
      };

      actions.append(pin);
      if (e.kind === "text") actions.append(stash);
      actions.append(del);
      li.append(text, meta, actions);
      li.onmouseenter = () => { selected = i; markSelection(); };
      return li;
    }),
  );
}

function markSelection() {
  list.querySelectorAll(".row").forEach((el, i) => el.classList.toggle("sel", i === selected));
}

async function copy(id: number) {
  await invoke("copy_entry", { id });
  await win.hide();
  refreshHistory();
}

async function absorb(e: Entry) {
  if (!vault.exists) { say("Set up the vault first (Vault tab)"); switchTab("vault"); return; }
  if (!vault.unlocked) { say("Unlock the vault first"); switchTab("vault"); return; }
  const label = prompt("Label for this secret:", e.preview.slice(0, 40));
  if (!label) return;
  try {
    await invoke("vault_absorb_entry", { id: e.id, label });
    say("Moved into the vault");
    refreshHistory();
  } catch (err) {
    say(String(err));
  }
}

// ---- vault --------------------------------------------------------------

async function refreshVault() {
  vault = await invoke<VaultStatus>("vault_status");
  const gate = $("vault-gate");
  const open = $("vault-open");
  gate.classList.toggle("hidden", vault.unlocked);
  open.classList.toggle("hidden", !vault.unlocked);

  const setup = !vault.exists;
  $("gate-title").textContent = setup ? "Create your vault" : "Unlock vault";
  $("gate-hint").textContent = setup
    ? "Pick a master password. It is never stored — it derives the key that encrypts your secrets, so there is no way to recover it if you forget it."
    : `Locked. Auto-locks after 10 idle minutes.`;
  $("gate-pw2").classList.toggle("hidden", !setup);
  $("gate-submit").textContent = setup ? "Create vault" : "Unlock";

  if (vault.unlocked) {
    vaultItems = await invoke<VaultItem[]>("vault_items");
    renderVault();
  }
  updateTimer();
}

function updateTimer() {
  const t = $("vault-timer");
  if (!vault.unlocked || vault.seconds_until_lock == null) { t.textContent = ""; return; }
  const m = Math.floor(vault.seconds_until_lock / 60);
  const s = vault.seconds_until_lock % 60;
  t.textContent = `locks in ${m}:${String(s).padStart(2, "0")}`;
}

function renderVault() {
  $("vault-empty").classList.toggle("hidden", vaultItems.length > 0);
  $<HTMLUListElement>("vault-list").replaceChildren(
    ...vaultItems.map((v) => {
      const li = document.createElement("li");
      li.className = "row";

      const text = document.createElement("span");
      text.className = "text";
      text.textContent = v.label;

      const meta = document.createElement("span");
      meta.className = "meta";
      meta.textContent = "••••••••";

      const actions = document.createElement("span");
      actions.className = "actions";

      const cp = document.createElement("button");
      cp.textContent = "copy";
      cp.title = "Copy to clipboard (not recorded in history)";
      cp.onclick = async () => {
        try { await invoke("vault_copy", { id: v.id }); say("Copied — not saved to history"); refreshVault(); }
        catch (e) { say(String(e)); }
      };

      const show = document.createElement("button");
      show.textContent = "reveal";
      show.onclick = async () => {
        try {
          const secret = await invoke<string>("vault_reveal", { id: v.id });
          meta.textContent = secret.length > 48 ? secret.slice(0, 48) + "…" : secret;
          window.setTimeout(() => { meta.textContent = "••••••••"; }, 10_000);
        } catch (e) { say(String(e)); }
      };

      const del = document.createElement("button");
      del.textContent = "✕";
      del.className = "danger";
      del.onclick = async () => {
        if (!confirm(`Delete "${v.label}" from the vault? This cannot be undone.`)) return;
        await invoke("vault_delete", { id: v.id });
        refreshVault();
      };

      actions.append(cp, show, del);
      li.append(text, meta, actions);
      return li;
    }),
  );
}

$<HTMLFormElement>("gate-form").onsubmit = async (ev) => {
  ev.preventDefault();
  gateError.textContent = "";
  const pw = $<HTMLInputElement>("gate-pw").value;
  const pw2 = $<HTMLInputElement>("gate-pw2").value;
  try {
    if (!vault.exists) {
      if (pw !== pw2) { gateError.textContent = "Passwords do not match"; return; }
      await invoke("vault_init", { password: pw });
    } else {
      await invoke("vault_unlock", { password: pw });
    }
    $<HTMLInputElement>("gate-pw").value = "";
    $<HTMLInputElement>("gate-pw2").value = "";
    refreshVault();
  } catch (e) {
    gateError.textContent = String(e);
  }
};

$("vault-new").onclick = async () => {
  const label = prompt("Label:");
  if (!label) return;
  const secret = prompt(`Secret value for "${label}":`);
  if (!secret) return;
  try { await invoke("vault_add", { label, secret }); refreshVault(); }
  catch (e) { say(String(e)); }
};

$("vault-lock-btn").onclick = async () => {
  await invoke("vault_lock");
  refreshVault();
};

// ---- shell --------------------------------------------------------------

function switchTab(next: "history" | "vault") {
  tab = next;
  document.querySelectorAll("#tabs button").forEach((b) =>
    b.classList.toggle("active", (b as HTMLElement).dataset.tab === next),
  );
  $("view-history").classList.toggle("hidden", next !== "history");
  $("view-vault").classList.toggle("hidden", next !== "vault");
  search.classList.toggle("hidden", next !== "history");
  if (next === "vault") refreshVault(); else refreshHistory();
}

document.querySelectorAll("#tabs button").forEach((b) => {
  (b as HTMLElement).onclick = () => switchTab((b as HTMLElement).dataset.tab as "history" | "vault");
});

search.oninput = () => { selected = 0; refreshHistory(); };

document.addEventListener("keydown", async (e) => {
  if (e.key === "Escape") { await win.hide(); return; }
  if (e.key === "Tab") { e.preventDefault(); switchTab(tab === "history" ? "vault" : "history"); return; }
  if (tab !== "history" || entries.length === 0) return;

  if (e.key === "ArrowDown") {
    e.preventDefault(); selected = Math.min(selected + 1, entries.length - 1); markSelection();
  } else if (e.key === "ArrowUp") {
    e.preventDefault(); selected = Math.max(selected - 1, 0); markSelection();
  } else if (e.key === "Enter") {
    e.preventDefault(); copy(entries[selected].id);
  } else if (e.key === "p" && e.ctrlKey) {
    e.preventDefault();
    const cur = entries[selected];
    await invoke("set_pinned", { id: cur.id, pinned: !cur.pinned });
    refreshHistory();
  } else if (e.key === "Delete") {
    e.preventDefault();
    await invoke("delete_entry", { id: entries[selected].id });
    refreshHistory();
  }
});

listen("history-changed", () => { if (tab === "history") refreshHistory(); });
listen("vault-locked", () => { if (tab === "vault") refreshVault(); say("Vault locked"); });
listen("window-shown", () => { search.focus(); search.select(); refreshHistory(); });

window.setInterval(() => {
  if (tab === "vault" && vault.unlocked && vault.seconds_until_lock != null) {
    vault.seconds_until_lock = Math.max(0, vault.seconds_until_lock - 1);
    updateTimer();
  }
}, 1000);

search.focus();
refreshHistory();
invoke<VaultStatus>("vault_status").then((v) => { vault = v; });
