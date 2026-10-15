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

Text and images are both captured, screenshots included. Text wins when an app
offers both, since copying a file or rich content offers an image alongside the
text you actually wanted.

Three details that are easy to get wrong and cost real debugging here:

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

Data lives in `~/.local/share/dev.local.clipvault/clipvault.db`. A database from
before image support is migrated in place on first open.

`Super+V` is GNOME's message-tray binding out of the box. Clear it with:

```bash
gsettings set org.gnome.shell.keybindings toggle-message-tray "[]"
```

## Command line

Only one copy ever runs; launching it again signals the running one.

```
clipvault              # start in the tray, or toggle the window if running
clipvault ui           # open the window, starting the app first if needed
clipvault --toggle     # show, or hide if already showing
clipvault --no-hotkey  # do not grab Super+V
clipvault --help
```

Two ways to get Super+V, and you want exactly one of them:

1. **Let the app grab it.** Clear GNOME's message-tray binding and run
   `clipvault` with no flags.
2. **Let the desktop own it.** Bind `clipvault ui` in Settings > Keyboard >
   Custom Shortcuts, and run the app with `--no-hotkey` so the two do not fight
   over the same key. More reliable, since it does not depend on the app's own
   X grab winning the race at login.

## Keys

| | |
|---|---|
| `Super+V` | show/hide (global) |
| `↑` `↓` `Enter` | navigate, copy and close |
| `Ctrl+P` | pin or unpin |
| `Delete` | remove entry |
| `Tab` | switch History / Vault |
| `Esc` | hide |

Closing the window parks it in the tray. Quit from the tray menu.

To start it at login, put a desktop entry in `~/.config/autostart/` whose
`Exec=` is `/usr/bin/clipvault` (add `--no-hotkey` if the desktop owns Super+V).

## Testing the watcher by hand

`scripts/gtkcopy.py` and `scripts/gtkpaste.py` act as a real GTK clipboard owner
and consumer. Use these rather than an `arboard`-based helper: arboard's X11
owner refuses our `ConvertSelection` even though real GTK/Qt apps serve it, which
makes it look like the watcher is broken when it is not.

```bash
python3 scripts/gtkcopy.py "hello"          # text
python3 scripts/gtkcopyimage.py 1200 900    # large image, forces an INCR transfer
python3 scripts/gtkpasteimage.py            # read an image back
```

`gtkcopyimage.py` uses random pixels on purpose: a solid fill compresses to a few
hundred bytes and never exercises `INCR`.

## Limits

- X11 only. Wayland has no equivalent of this API; it needs a portal or a
  compositor-specific protocol.
- Text and images (PNG, JPEG, BMP, GIF, normalised to PNG). File lists are
  skipped, as is text over 2 MB and any image over 32 MB.
- Unpinned history is capped at 500 entries and 256 MB, oldest evicted first.
  Pins are exempt from both; the newest entry always survives.
- Images go in the history, not the vault, which holds text secrets only.
- PRIMARY (mouse-select) is not captured — it would bury the list in noise.
- Copies tagged `x-kde-passwordManagerHint: secret` are deliberately ignored.
