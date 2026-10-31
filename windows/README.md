# ClipVault for Windows

Clipboard history with an encrypted secrets vault, for Windows. A .NET 9 /
WPF port of the Rust/Tauri app in the parent folder - same features, same
database schema, same crypto, independent build. See [PLAN.md](PLAN.md) for
how the port maps onto the Rust source and what was verified along the way.

## How it captures copies

It listens for `WM_CLIPBOARDUPDATE`, Windows' notification that the clipboard
changed, rather than hooking keys - the same reasoning as the Linux build's
XFixes watcher: Ctrl+C, Ctrl+X, a context menu, an in-app copy button and a
script-driven copy all change the clipboard, and nothing reads the keyboard.

Text and images are both captured, screenshots included. Text wins when an
app offers both, since copying a file or rich content offers an image
alongside the text you actually wanted.

Where the Linux build has to fetch a server timestamp and unwind an INCR
transfer for a large X11 selection, Windows hands over the whole clipboard on
`OpenClipboard`, so there is no equivalent of either here. What does carry
over is retrying: `OpenClipboard` fails while another process holds it, so
the watcher retries a few times with a short delay.

## Vault

- Argon2id (64 MiB, 3 passes, 4 lanes) derives a 32-byte key from the master
  password, via `Konscious.Security.Cryptography.Argon2`
- XChaCha20-Poly1305 per entry, fresh nonce each time, via `Geralt` (libsodium)
- The key lives in a pinned `byte[32]`, wiped with `CryptographicOperations.ZeroMemory`
  on lock. A decrypted secret, once it comes back as a `string`, cannot be
  reliably scrubbed from memory in .NET the way the Rust build's
  `Zeroizing<String>` can - a real gap, noted in `Vault.cs` rather than
  quietly dropped
- Auto-locks after 10 idle minutes; a background sweeper enforces it even
  while the window sits untouched
- The master password is never stored. A sealed verifier blob checks it
- **The database is interchangeable with the Linux build.** Same schema, same
  KDF and AEAD parameters - `VaultCrossCheckTests` in the test suite proves a
  vault written by the Rust build unlocks and decrypts in .NET

A secret copied out of the vault also sets three clipboard formats
(`ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory`,
`CanUploadToCloudClipboard`) so it stays out of Windows' own Win+V clipboard
history and Cloud Clipboard sync too - neither of which the Linux build has
to think about, since X11 has no equivalent of either.

**Labels are stored in plaintext**, so the vault is listable while locked.
Name entries in a way you would not mind someone reading. The secret values
are the encrypted part.

## Running it

```powershell
dotnet build
dotnet test
dotnet run --project src/ClipVault.App -- ui
```

Data lives in `%LOCALAPPDATA%\ClipVault\clipvault.db`.

### Publishing

```powershell
dotnet publish src/ClipVault.App -c Release -r win-x64 --self-contained -o publish -p:DebugType=none
```

Produces `publish\clipvault.exe` and nothing it needs beside it - not even a
separately installed .NET runtime; `-p:DebugType=none` skips our own debug
symbol files. SkiaSharp's NuGet package ships its own native symbol file
(`libSkiaSharp.pdb`, tens of MB) alongside regardless; it is not needed to
run and is safe to delete, or just ignore. Copy `clipvault.exe` itself
wherever you like - confirmed to run standalone from an empty folder with
nothing else beside it.

## Command line

Only one copy ever runs; launching it again signals the running one.

```
clipvault              # start in the tray, or toggle the window if running
clipvault ui           # open the window, starting the app first if needed
clipvault --toggle     # show, or hide if already showing
clipvault --no-hotkey  # do not grab Win+Alt+V
clipvault --help
```

## Keys

| | |
|---|---|
| `Win+Alt+V` | show/hide (global) |
| `↑` `↓` `Enter` | navigate, copy and close |
| `Ctrl+P` | pin or unpin |
| `Delete` | remove entry |
| `Tab` | switch History / Vault |
| `Esc` | hide |

Closing the window parks it in the tray. Quit from the tray menu.

Windows keeps **Win+V** (its own clipboard history) and **Win+Shift+V**
(focus a notification) for itself; `RegisterHotKey` cannot take either. See
PLAN.md's hotkey decision for the combinations that were actually tested.

## Autostart

Right-click the tray icon and check **Start with Windows**. This writes
`clipvault.exe`'s path to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`;
unchecking it removes the entry. Unlike the Linux build, there is no
Super+V-style conflict to work around, so autostart always launches with the
hotkey enabled - no `--no-hotkey` equivalent is needed here.

## Testing by hand

`scripts/copy-text.ps1` and `scripts/copy-image.ps1` put real text and a
random-pixel test image on the clipboard, retrying the few-second window
where `Set-Clipboard`/`Clipboard.SetImage` can transiently fail. They play
the same role as `scripts/gtkcopy.py` and `scripts/gtkcopyimage.py` in the
Linux build.

```powershell
.\scripts\copy-text.ps1 "hello"             # text
.\scripts\copy-image.ps1 1200 900           # large random-pixel image
```

After copying a secret out of the vault (its "copy" button), run
`check-history-exclusion.ps1` to confirm it is tagged out of Windows' own
Win+V history and Cloud Clipboard sync - there is no public API to query the
Win+V panel directly, so this checks the same formats Windows itself reads,
which is the closest a script gets:

```powershell
.\scripts\check-history-exclusion.ps1
```

## Limits

Same as the Linux build: text and images (PNG, JPEG, BMP, GIF, normalised to
PNG) up to 2 MB / 32 MB; unpinned history capped at 500 entries and 256 MB;
the vault holds text secrets only; PRIMARY-selection-style capture does not
apply on Windows (there is no equivalent); copies an app marks
`CanIncludeInClipboardHistory = 0` (its own opt-out, e.g. many password
managers) are skipped, the Windows analogue of the Linux build's
`x-kde-passwordManagerHint` check.
