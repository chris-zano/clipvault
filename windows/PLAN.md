# ClipVault for Windows — .NET port plan

A native Windows port of ClipVault (the Tauri/Rust app in the parent folder),
built on .NET 9 and WPF. It lives entirely in `windows/` and shares no build
with the Rust app: `cargo`/`npm` never see it, and `dotnet` never sees them.

The goal is feature parity with the Rust app, using Windows' own mechanisms
where the Linux version uses X11 ones.

## Solution layout

```
windows/
  ClipVault.sln
  src/
    ClipVault.Core/         net9.0   — no Windows APIs; everything testable lives here
    ClipVault.App/          net9.0-windows, WPF — clipboard, tray, hotkey, UI
  tests/
    ClipVault.Core.Tests/   xUnit — ports of every Rust #[test]
```

`Core` holds the logic that the Rust code already tests in isolation (store,
vault, media, suppressor, command line). `App` holds everything that needs a
window handle. Keeping that split is what lets the Rust test suite be ported
almost line for line.

## Feature parity map

| Rust module / feature | Linux mechanism | Windows / .NET equivalent | Project |
|---|---|---|---|
| `lib.rs` `intent_of`, `--help`, `--no-hotkey` | argv | `CommandLine.IntentOf` (**done**) | Core |
| Single instance, forward argv to running copy (**done**) | `tauri-plugin-single-instance` | Named `Mutex` + named pipe carrying the args | App |
| `watcher.rs` — capture every copy, no keyboard hooks | XFixes selection-owner events | `AddClipboardFormatListener` on a message-only window → `WM_CLIPBOARDUPDATE` | App |
| Text capture, ≤ 2 MB (**done**) | UTF8_STRING / text/plain | `CF_UNICODETEXT`, falling back to `CF_TEXT` | App |
| Image capture, ≤ 32 MB, normalised to PNG (**done**) | image/png, jpeg, bmp, gif targets + INCR | A "PNG" format first (browsers/Office offer it directly), then `CF_DIBV5`, then `CF_DIB` wrapped in a BMP file header | App |
| Text wins when both are offered (**done**) | target ordering | Same ordering when checking formats | App |
| Skip password-manager copies (**done**) | `x-kde-passwordManagerHint: secret` | `CanIncludeInClipboardHistory` = 0 | App |
| Skip file lists (**done**) | ignored targets | Ignore `CF_HDROP` | App |
| `Suppressor` — don't re-record our own writes (**done**) | hash match / unconditional, 3 s deadline | Same class, ported as-is; vault copies also set `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory=0` and `CanUploadToCloudClipboard=0` (verified present on the clipboard after a secret copy) so Windows' own Win+V history and Cloud Clipboard skip secrets too | Core + App |
| Recorder thread (decode/hash off the watcher thread) (**done**) | `mpsc` + thread | `System.Threading.Channels` + a background `Task` | App |
| `store.rs` — SQLite history, dedupe by hash, pins, search, trim (**done**) | rusqlite | `Microsoft.Data.Sqlite`, **identical schema and SQL**, incl. the in-place image-column migration | Core |
| Caps: 500 unpinned / 256 MB, newest always survives | window-function DELETE | Same SQL | Core |
| Literal substring search (`%`, `_` escaped) | `LIKE … ESCAPE '\'` | Same | Core |
| `media.rs` — decode, pixel hash, PNG normalise, 160 px thumbnail (**done**) | `image` crate | SkiaSharp (MIT) — decode/encode/resize without a Windows dependency, so it stays in Core and stays testable | Core |
| `vault.rs` — Argon2id (64 MiB, 3 passes, 4 lanes) (**done**) | `argon2` | `Konscious.Security.Cryptography.Argon2` (supports lanes = 4, so parameters match exactly) | Core |
| XChaCha20-Poly1305, fresh 24-byte nonce per entry (**done**) | `chacha20poly1305` | libsodium via `Geralt` (BCL only has 12-byte-nonce ChaCha20-Poly1305) | Core |
| Key wiped on lock (`Zeroizing`) | zeroize | Pinned `byte[]` (`GC.AllocateArray(pinned: true)`) + `CryptographicOperations.ZeroMemory` | Core |
| Sealed verifier, min 8-char password, no password stored | — | Same | Core |
| 10-minute idle auto-lock + 15 s sweeper (**done**) | thread loop | `PeriodicTimer` on a background task, raises a `VaultLocked` event | Core + App |
| Tray: Open / Lock vault / Quit, left-click toggles (**done**) | Tauri tray | WinForms `NotifyIcon` (`UseWindowsForms`), no third-party dependency | App |
| Close hides to tray; Quit exits (**done**) | `prevent_close` | `Window.OnClosing` → `e.Cancel = true; Hide()`; `ShutdownMode=OnExplicitShutdown` so hiding never ends the app | App |
| Global hotkey (**done**) | Super+V via X grab | **Win+Alt+V** via `RegisterHotKey` on a message-only window (Win+V is the shell's; see decision 1) | App |
| History tab: search, thumbnails, pin ★, move to vault 🔒, delete ✕ with confirm (**done**) | HTML/TS | Rows built imperatively in code-behind, same full-rebuild-on-refresh style as the TS version, not `ListView`/`DataTemplate` bindings | App |
| Vault tab: create/unlock gate, add, copy, reveal (10 s), delete, lock, countdown (**done**) | HTML/TS | WPF view with a `PasswordBox` gate | App |
| Keys: ↑ ↓ Enter, Ctrl+P, Delete, Tab, Esc (**done**) | DOM keydown | One `Window.PreviewKeyDown` handler, same precedence as the TS `document` keydown listener | App |
| Events `history-changed`, `vault-locked`, `window-shown` (**done**) | Tauri emit | Plain C# events (`ClipboardRecorder.HistoryChanged`, `AppState.VaultLocked`) called directly from `App.xaml.cs`; `window-shown` is just `MainWindow.OnShown()` called from `ShowWindow()` | App |
| Data location (**done**) | `~/.local/share/dev.local.clipvault/clipvault.db` | `%LOCALAPPDATA%\ClipVault\clipvault.db` | App |
| Autostart (**done**) | `~/.config/autostart/*.desktop` | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, opt-in "Start with Windows" tray menu toggle | App |
| Packaging | .deb / AppImage | `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`; MSIX or Inno Setup later | — |

### Things the port gets for free

- **Idle memory.** The Rust README notes 137 MB idle because the WebView is
  created at startup. Here the window is created lazily on first show, so idle
  cost is the watcher plus SQLite.
- **No timestamp/INCR dance.** `server_time()` and `read_incr()` exist because
  of X11 selection semantics. Windows hands over the whole clipboard on
  `OpenClipboard`, so neither has an equivalent. What does carry over is the
  need to retry: `OpenClipboard` fails while another process holds it, so the
  watcher retries a few times with a short delay, mirroring the Rust
  "owner refused the first ask" retry.

## Phases

Each phase ends with the solution building and the tests passing.

1. **Core: store (done)** — ported `store.rs` and its 19 tests as
   [Store.cs](src/ClipVault.Core/Store.cs) /
   [StoreTests.cs](tests/ClipVault.Core.Tests/StoreTests.cs). Schema and SQL
   are byte-for-byte the same, via `Microsoft.Data.Sqlite`. The image-related
   tests use a fabricated `ImagePayload` ([TestImages.cs](tests/ClipVault.Core.Tests/TestSupport/TestImages.cs))
   standing in for `media::prepare`, since `Store` never decodes pixels itself —
   phase 2 wires in the real decoder.
2. **Core: media (done)** — ported `media.rs` as [Media.cs](src/ClipVault.Core/Media.cs)
   /[MediaTests.cs](tests/ClipVault.Core.Tests/MediaTests.cs), using SkiaSharp
   to decode into straight (non-premultiplied) RGBA8888, matching the `image`
   crate's `to_rgba8()`, so the pixel hash and the clipboard round trip behave
   the same way. [TestImages.cs](tests/ClipVault.Core.Tests/TestSupport/TestImages.cs)
   now builds a real PNG fixture and calls `Media.Prepare` on it, replacing
   the fabricated payload from phase 1 — the store's image tests exercise the
   real decode/hash/thumbnail path, same as `store.rs`'s `image_clip` helper
   calling `crate::media::prepare`.
3. **Core: vault + suppressor (done)** — ported `vault.rs` as
   [Vault.cs](src/ClipVault.Core/Vault.cs) / [VaultTests.cs](tests/ClipVault.Core.Tests/VaultTests.cs)
   (9 tests) and the `Suppressor` from `watcher.rs` as
   [Suppressor.cs](src/ClipVault.Core/Suppressor.cs) / [SuppressorTests.cs](tests/ClipVault.Core.Tests/SuppressorTests.cs)
   (4 tests). KDF is `Konscious.Security.Cryptography.Argon2id` (64 MiB, 3
   passes, 4 lanes); AEAD is `Geralt.XChaCha20Poly1305` (libsodium). The key
   lives in a pinned `byte[32]`, wiped with `CryptographicOperations.ZeroMemory`
   on lock; a decrypted secret comes back as a `string`, which .NET cannot
   reliably scrub (unlike Rust's `Zeroizing<String>`) — noted as a comment in
   `Vault.cs` rather than quietly dropped.

   **Real cross-check, not just known-answer vectors.** Rust/cargo turned out
   to be available on this machine, so [VaultCrossCheckTests.cs](tests/ClipVault.Core.Tests/VaultCrossCheckTests.cs)
   uses an actual fixture captured from `clipvault_lib` (rustc 1.99.1): a
   vault's salt/nonce/ciphertext and one encrypted secret, produced by a
   temporary `#[test]` added to `src-tauri/src/vault.rs` and reverted
   immediately after (`git checkout -- src-tauri/src/vault.rs`; the Rust tree
   is unmodified). The .NET vault unlocks that fixture and decrypts the
   secret, which proves the KDF and AEAD agree byte-for-byte with the Rust
   build, not just with the Argon2id/XChaCha20-Poly1305 specs. To regenerate:
   add a test like this to `vault.rs`'s test module, run it, copy the hex it
   prints, then revert the file —

   ```rust
   #[test]
   fn print_cross_check_fixture() {
       let (s, mut v, _d) = fixture();
       v.init(&s, "cross-check password").unwrap();
       let (salt, vnonce, vct) = s.vault_meta().unwrap().unwrap();
       let (snonce, sct) = v.encrypt("a secret for the cross-check").unwrap();
       fn hex(b: &[u8]) -> String { b.iter().map(|x| format!("{:02x}", x)).collect() }
       println!("salt={} vnonce={} vct={} snonce={} sct={}",
           hex(&salt), hex(&vnonce), hex(&vct), hex(&snonce), hex(&sct));
   }
   ```

   ```powershell
   cargo test --lib vault::tests::print_cross_check_fixture -- --exact --nocapture
   ```

   (On Windows, `cargo build`/`test` needs `src-tauri/icons/icon.ico` to
   exist for `tauri-build`'s resource step; it is not part of this port, so
   generate a throwaway one and delete it afterward rather than committing it —
   e.g. wrap `icons/128x128.png` in a minimal ICO header.)
4. **App: background shell (done)** — [App.xaml.cs](src/ClipVault.App/App.xaml.cs)
   wires up everything a daemon launch needs, with no UI yet beyond an empty
   placeholder window:
   - [SingleInstance.cs](src/ClipVault.App/SingleInstance.cs): a named
     `Mutex` decides who's first; a later launch sends its argv over a named
     pipe to the running copy and exits. Manually verified: a second launch
     exits in a few ms, leaves exactly one `clipvault` process running, and
     still toggles that process's window.
   - [HotkeyManager.cs](src/ClipVault.App/HotkeyManager.cs): Win+Alt+V via
     `RegisterHotKey` on a message-only window (`HWND_MESSAGE`), so no
     visible window is needed just to pump `WM_HOTKEY`. Verified the app
     holds the hotkey exclusively (a second `RegisterHotKey` for the same
     combination from outside the app fails while it is running).
   - [TrayIcon.cs](src/ClipVault.App/TrayIcon.cs): WinForms `NotifyIcon`
     (`UseWindowsForms` enabled just for this) with Open / Lock vault / Quit,
     left-click toggles - same menu and behaviour as the Rust tray. Icon is a
     stock system icon for now; phase 7 swaps in the real one.
   - [AppState.cs](src/ClipVault.App/AppState.cs): opens the store at
     `%LOCALAPPDATA%\ClipVault\clipvault.db`, owns the `Vault` and
     `Suppressor`, and runs the 15-second auto-lock sweeper on a
     `PeriodicTimer`.
   - `MainWindow`: created lazily on first show, not at startup;
     [MainWindow.xaml.cs](src/ClipVault.App/MainWindow.xaml.cs) overrides
     `OnClosing` to cancel and hide instead, with `ShutdownMode=OnExplicitShutdown`
     so only the tray's Quit actually exits. Verified `clipvault ui` shows
     the window, `clipvault --toggle` hides it, and a simulated `WM_CLOSE`
     hides it rather than ending the process.
   - [ConsoleInterop.cs](src/ClipVault.App/ConsoleInterop.cs): `--help` needs
     to print somewhere even though a `WinExe` has no console of its own -
     writes directly when stdout is already redirected (piped, captured by a
     script), otherwise attaches to the launching console and detaches again.
     Verified both paths.
5. **App: clipboard watcher and writer (done)** — under
   [src/ClipVault.App/Clipboard/](src/ClipVault.App/Clipboard/):
   - [NativeClipboard.cs](src/ClipVault.App/Clipboard/NativeClipboard.cs):
     the shared Win32 plumbing - `OpenClipboard` retried a handful of times
     with a short delay (Windows hands over the whole clipboard on open,
     unlike X11's per-request `ConvertSelection`, so this retry is the only
     thing that carries over from the timestamp/INCR handling in `watcher.rs`),
     plus the global-memory read/write helpers both sides need.
   - [ClipboardWatcher.cs](src/ClipVault.App/Clipboard/ClipboardWatcher.cs):
     a message-only window listening for `WM_CLIPBOARDUPDATE`. Checks
     `CanIncludeInClipboardHistory` first (the Windows analogue of the KDE
     password-manager hint), then text (`CF_UNICODETEXT`, falling back to
     `CF_TEXT`) before image (a "PNG" format some apps offer directly, else
     `CF_DIBV5`/`CF_DIB` wrapped in a BMP file header so SkiaSharp can decode
     them), applying the same 2 MB / 32 MB limits as the Rust build.
   - [ClipboardRecorder.cs](src/ClipVault.App/Clipboard/ClipboardRecorder.cs):
     an unbounded channel plus a background task, so decoding an image and
     writing to SQLite never happens on the thread pumping clipboard messages.
   - [ClipboardWriter.cs](src/ClipVault.App/Clipboard/ClipboardWriter.cs):
     `SetText`/`SetImagePng` for copying history back out, and `SetSecretText`
     for the vault, each arming the `Suppressor` first. A vault secret also
     sets `ExcludeClipboardContentFromMonitorProcessing`,
     `CanIncludeInClipboardHistory=0` and `CanUploadToCloudClipboard=0`, so it
     stays out of Windows' own Win+V history and Cloud Clipboard sync too -
     neither of which the Linux build has to think about, since X11 has no
     equivalent of either.

   **Manually verified against the real OS clipboard**, not just by reading
   the code: `Set-Clipboard` (text) and a WinForms-drawn bitmap (image) both
   showed up in `clipvault.db` through the real watcher → recorder → store
   path; a temporary `CLIPVAULT_DEBUG_WRITE_TEXT`/`_SECRET` env-var hook (the
   writer has no caller until phase 6's UI) confirmed `SetText` is suppressed
   from self-recording while an unrelated copy straight after still records,
   `SetSecretText` never reaches the store, and all three Windows exclusion
   formats are actually present on the clipboard afterwards - then the hook
   was removed, the same add-verify-revert cycle phase 3 used for the Rust
   cross-check fixture.
6. **App: UI (done)** — [MainWindow.xaml](src/ClipVault.App/MainWindow.xaml) /
   [MainWindow.xaml.cs](src/ClipVault.App/MainWindow.xaml.cs) implement both
   tabs, built imperatively (rows and the gate's conditional fields are
   constructed in code, not `DataTemplate`s) the same way `main.ts` rebuilds
   its `<ul>` from scratch on every refresh, rather than via WPF data binding.
   [PromptDialog.xaml](src/ClipVault.App/PromptDialog.xaml) is a small 1-or-2
   -field modal standing in for the web UI's `prompt()` (label/secret entry,
   the "move into vault" label - prefilled from the entry's preview, same as
   Rust); delete confirmations use `MessageBox.Show`, matching `confirm()`'s
   messages and the "cannot be undone" wording exactly. One `PreviewKeyDown`
   handler on the window reproduces the TS `document` keydown listener's
   exact precedence: Escape and Tab always act first, then Up/Down/Enter/
   Ctrl+P/Delete only when the History tab is active and has entries - so
   typing in the search box still lets arrow keys navigate the list, same as
   the web version. [Theme.cs](src/ClipVault.App/Theme.cs) reads
   `AppsUseLightTheme` from the registry once at startup and applies the
   light/dark palette from `styles.css` as resource brushes (not tracked
   live - a theme change needs a restart, unlike the web UI's
   `prefers-color-scheme` media query). A genuine, non-placeholder app icon
   was made for this phase too (see "App icon" below) rather than waiting for
   phase 7, and is wired into the window, exe and tray icon already.

   **Manually verified end-to-end against the real running app** (UI
   Automation + real mouse clicks + real `Set-Clipboard`/image copies, not
   just code review): live capture into the history list, search filtering,
   pin toggle (mouse and `Ctrl+P`), click-to-copy and `Enter`-to-copy (both
   correctly hide the window and leave the right text on the clipboard),
   `Delete`/✕ with both Cancel (entry survives) and OK (entry removed)
   confirmed on real dialogs, "move into vault" with its pre-filled label
   (confirmed the entry moves rather than duplicates), vault creation,
   wrong-password rejection, add-secret, reveal, copy (confirmed it never
   reaches history), lock, the lock countdown text, vault entry deletion, and
   tab switching. One real WPF/UIA lesson from this pass: an owned window
   (a `MessageBox` or a `Window` with `Owner` set, as `PromptDialog` and the
   delete confirmation both are) shows up in UI Automation as a descendant of
   its owner, not as a sibling of it at the desktop root - a plain
   `RootElement.FindFirst(Children, ...)` will never find it; search
   `TreeScope.Subtree` from the owner (or the desktop) instead.

   **App icon.** [icon.ico](src/ClipVault.App/Assets/icon.ico) is an
   original padlock design (SkiaSharp-rendered rounded-square badge, 7 sizes
   from 16 to 256px) in ClipVault's own palette (`--panel`/`--accent` from
   `styles.css`), not the Rust build's `src-tauri/icons` (which are per the
   Tauri/Linux build and were not reused, by request) and not a copy of any
   reference image. Wired in three places from one embedded file: the
   csproj's `ApplicationIcon` (exe/taskbar), `MainWindow.xaml`'s `Icon`
   (titlebar), and `TrayIcon.cs` loading the same WPF resource stream for the
   `NotifyIcon` (replacing the stock system icon placeholder). Confirmed
   visually with a real screenshot, cropped to the titlebar and the system
   tray overflow flyout.
7. **Ship (done)**
   - **Single-file publish**: `ClipVault.App.csproj` declares
     `RuntimeIdentifiers` (plural - keeps `dotnet build`/`dotnet test`
     RID-agnostic), `SelfContained`, `PublishSingleFile`, and
     `IncludeNativeLibrariesForSelfExtract` (folds SQLite/SkiaSharp/libsodium's
     native libraries into the exe too). The exact command is in
     [README.md](README.md#publishing). Verified: copied only
     `clipvault.exe` (nothing else) into an empty folder and ran `--help`,
     the tray daemon, and the full UI from there - no missing-runtime or
     missing-DLL errors. One incidental artifact: SkiaSharp's NuGet package
     ships its own native `libSkiaSharp.pdb` (tens of MB) alongside
     regardless of `-p:DebugType=none`; noted in the README as safe to delete.
   - **Autostart toggle**: [Autostart.cs](src/ClipVault.App/Autostart.cs)
     reads/writes `HKCU\...\CurrentVersion\Run`; wired into
     [TrayIcon.cs](src/ClipVault.App/TrayIcon.cs) as a checkable "Start with
     Windows" item. No `--no-hotkey`-equivalent is needed here (unlike the
     Linux README's autostart advice), since there is no desktop-keybinding
     conflict to work around on Windows. Verified both directions on a real
     published exe: checking it writes the exact running exe's path to the
     registry; unchecking it removes the value.
   - **Manual test scripts**: [scripts/copy-text.ps1](scripts/copy-text.ps1),
     [scripts/copy-image.ps1](scripts/copy-image.ps1) and
     [scripts/check-history-exclusion.ps1](scripts/check-history-exclusion.ps1)
     play the same role as the Linux README's `gtkcopy.py`/`gtkpasteimage.py`
     - documented in [README.md](README.md#testing-by-hand). All three were
     run for real: text and a random-pixel image both landed in
     `clipvault.db` through the live watcher; the exclusion check correctly
     reported all three formats missing after an ordinary copy and all three
     present after copying an actual vault secret.
   - **README**: [README.md](README.md) was filled out in full (features,
     running, publishing, command line, keys, autostart, testing by hand,
     limits), and the root [README.md](../README.md) now points to it.
     That root-level edit is the one change this port makes outside
     `windows/` - everything else stays confined there, confirmed with
     `git status` after every phase.

With phase 7 done, the .NET/WPF port has reached feature parity with the
Rust/Tauri build end to end - store, media, vault (cross-checked against a
real Rust-produced database), the background shell, the clipboard watcher
and writer, the full UI, and now packaging - with every phase manually
verified against the real running app, not just reviewed as code.

## Decisions and open questions

1. **Hotkey (decided).** Tested with `RegisterHotKey` on Windows 11 (2026-10-31):

   | Combination | Result |
   |---|---|
   | Win+V | taken by the shell (clipboard history), error 1409 |
   | Win+Shift+V | taken by the shell (focus a notification), error 1409 |
   | Win+Ctrl+V | taken by the shell, error 1409 |
   | Win+Alt+V | free |
   | Ctrl+Alt+V | free, but some apps use it for Paste Special |
   | Ctrl+Shift+Alt+V | free |

   **Decided: Win+Alt+V**, registered with `RegisterHotKey` (`MOD_WIN |
   MOD_ALT | MOD_NOREPEAT`). Win+Shift+V was considered, but only a low-level
   keyboard hook could take it from the shell, which would break the Rust
   app's "nothing reads the keyboard" design. The hotkey is configurable, and
   `--no-hotkey` still works for binding `clipvault ui` some other way.
2. **Vault database compatibility with Linux (verified).** Schema and crypto
   parameters are identical, and `VaultCrossCheckTests` proves a vault
   written by the Rust build unlocks and decrypts in .NET — so a
   `clipvault.db` really can move between the two apps. If that is not
   wanted, `Geralt.Argon2id` (libsodium's own pwhash, fixed at 1 lane) could
   replace Konscious and drop one dependency, at the cost of this guarantee.
3. **Rich text / HTML.** The Rust app keeps plain text only. The port matches
   that; capturing `CF_HTML`/RTF alongside would be a later addition.
