# ClipVault

Clipboard history with an encrypted secrets vault, for X11 on Linux. Tauri v2:
Rust daemon, TypeScript UI, 7.2 MB binary.

Measured 137 MB resident at idle, because the WebView is created at startup even
though the window stays hidden. Creating the window lazily on first open would
cut idle usage to the Rust daemon alone; it is not done yet.

## How it captures copies

It watches the X11 CLIPBOARD selection through XFixes rather than hooking keys.
Ctrl+C, Ctrl+X, a context menu, an in-app copy button and `xdotool` all change
the selection owner, so all of them are captured, and nothing reads the keyboard.

Two details that are easy to get wrong and cost real debugging here:

- A `ConvertSelection` must carry a timestamp the owner accepts, and owners may
  refuse one that predates their ownership. `server_time()` in `watcher.rs` asks
  the server for a real timestamp with a zero-length property append rather than
  reusing the XFixes event time, which is not guaranteed to be late enough.
- Writing to the clipboard ourselves fires our own watcher. `Suppressor` skips
  that echo — by content hash for history copies, and unconditionally for vault
  copies, so a secret cannot land in the plaintext history table.

## Vault

- argon2id (64 MiB, 3 passes) derives a 32-byte key from the master password
- XChaCha20-Poly1305 per entry, fresh nonce each time
- The key lives in `Zeroizing<[u8; 32]>` and is wiped on lock
- Auto-locks after 10 idle minutes; a background sweeper enforces it even while
  the window sits untouched
- The master password is never stored. A sealed verifier blob checks it.

**Labels are stored in plaintext**, so the vault is listable while locked. Name
entries in a way you would not mind someone reading. The secret values are the
encrypted part.

## Running it

```bash
npm install
npm run tauri dev        # dev
npm run tauri build      # .deb and AppImage in src-tauri/target/release/bundle
cargo test --lib --manifest-path src-tauri/Cargo.toml
```

Data lives in `~/.local/share/dev.local.clipvault/clipvault.db`.

## Keys

| | |
|---|---|
| `Super+Shift+V` | show/hide (global; Super+V is GNOME's message tray) |
| `↑` `↓` `Enter` | navigate, copy and close |
| `Ctrl+P` | pin or unpin |
| `Delete` | remove entry |
| `Tab` | switch History / Vault |
| `Esc` | hide |

Closing the window parks it in the tray. Quit from the tray menu.

## Testing the watcher by hand

`scripts/gtkcopy.py` and `scripts/gtkpaste.py` act as a real GTK clipboard owner
and consumer. Use these rather than an `arboard`-based helper: arboard's X11
owner refuses our `ConvertSelection` even though real GTK/Qt apps serve it, which
makes it look like the watcher is broken when it is not.

```bash
python3 scripts/gtkcopy.py "hello"   # then check the history list
```

## Limits

- X11 only. Wayland has no equivalent of this API; it needs a portal or a
  compositor-specific protocol.
- Text only. Images and file lists are skipped, as are payloads over 2 MB and
  `INCR` transfers.
- PRIMARY (mouse-select) is not captured — it would bury the list in noise.
- Copies tagged `x-kde-passwordManagerHint: secret` are deliberately ignored.
