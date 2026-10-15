mod commands;
mod media;
mod store;
mod vault;
mod watcher;

use std::sync::{mpsc, Arc, Mutex};
use std::time::Duration;

use tauri::menu::{Menu, MenuItem, PredefinedMenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{AppHandle, Emitter, Manager, WindowEvent};
use tauri_plugin_global_shortcut::{Code, GlobalShortcutExt, Modifiers, Shortcut, ShortcutState};

use commands::App;
use store::{Clip, Store};
use vault::{Vault, AUTO_LOCK_MINUTES};
use watcher::{Captured, Suppressor};

/// Super+V. GNOME binds this to the message tray out of the box; clearing that
/// binding is part of installing this app, see README.
fn default_shortcut() -> Shortcut {
    Shortcut::new(Some(Modifiers::SUPER), Code::KeyV)
}

fn toggle_window(app: &AppHandle) {
    let Some(win) = app.get_webview_window("main") else { return };
    if win.is_visible().unwrap_or(false) {
        let _ = win.hide();
    } else {
        let _ = win.center();
        let _ = win.show();
        let _ = win.set_focus();
        let _ = app.emit("window-shown", ());
    }
}

/// Re-exported so the `setclipimage` example can exercise the copy-back path.
pub fn media_to_rgba(png: &[u8]) -> anyhow::Result<(u32, u32, Vec<u8>)> {
    media::to_rgba(png)
}

pub fn run() {
    env_logger::Builder::from_env(env_logger::Env::default().default_filter_or("info")).init();

    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![
            commands::list_entries,
            commands::copy_entry,
            commands::entry_content,
            commands::set_pinned,
            commands::delete_entry,
            commands::clear_history,
            commands::vault_status,
            commands::vault_init,
            commands::vault_unlock,
            commands::vault_lock,
            commands::vault_items,
            commands::vault_add,
            commands::vault_absorb_entry,
            commands::vault_reveal,
            commands::vault_copy,
            commands::vault_delete,
        ])
        .setup(|app| {
            let handle = app.handle().clone();

            let db_path = app.path().app_data_dir()?.join("clipvault.db");
            log::info!("store: {}", db_path.display());

            let suppress = Arc::new(Suppressor::default());
            app.manage(App {
                store: Mutex::new(Store::open(&db_path)?),
                vault: Mutex::new(Vault::default()),
                clipboard: Mutex::new(arboard::Clipboard::new()?),
                suppress: suppress.clone(),
            });

            // Clipboard watcher -> store, on two threads so neither X nor
            // SQLite can stall the UI.
            let (tx, rx) = mpsc::channel::<Captured>();
            watcher::spawn(tx);

            // Decoding and hashing happen here rather than on the X thread, so a
            // multi-megabyte screenshot cannot delay the next capture.
            let recorder = handle.clone();
            let recorder_suppress = suppress.clone();
            std::thread::Builder::new()
                .name("clipboard-recorder".into())
                .spawn(move || {
                    while let Ok(captured) = rx.recv() {
                        let clip = match captured {
                            Captured::Text(t) => Clip::Text(t),
                            Captured::Image(bytes) => match media::prepare(&bytes) {
                                Ok(img) => Clip::Image(img),
                                Err(e) => {
                                    log::warn!("ignoring unreadable image on clipboard: {e}");
                                    continue;
                                }
                            },
                        };
                        if recorder_suppress.should_skip(&clip.hash()) {
                            continue;
                        }

                        let state = recorder.state::<App>();
                        let result = state.store.lock().unwrap().record(&clip);
                        match result {
                            Ok(Some(_)) => {
                                let _ = recorder.emit("history-changed", ());
                            }
                            Ok(None) => {}
                            Err(e) => log::error!("failed to record clipboard entry: {e}"),
                        }
                    }
                })?;

            // Auto-lock sweeper. Without it the vault would only lock on the
            // next command, which may never come while the window sits idle.
            let locker = handle.clone();
            std::thread::Builder::new()
                .name("vault-autolock".into())
                .spawn(move || loop {
                    std::thread::sleep(Duration::from_secs(15));
                    let state = locker.state::<App>();
                    let locked = state.vault.lock().unwrap().lock_if_idle(AUTO_LOCK_MINUTES);
                    if locked {
                        log::info!("vault auto-locked after {AUTO_LOCK_MINUTES} idle minutes");
                        let _ = locker.emit("vault-locked", ());
                    }
                })?;

            // Tray
            let open = MenuItem::with_id(app, "open", "Open ClipVault", true, None::<&str>)?;
            let lock = MenuItem::with_id(app, "lock", "Lock vault", true, None::<&str>)?;
            let quit = MenuItem::with_id(app, "quit", "Quit", true, None::<&str>)?;
            let menu = Menu::with_items(
                app,
                &[&open, &lock, &PredefinedMenuItem::separator(app)?, &quit],
            )?;

            TrayIconBuilder::with_id("tray")
                .icon(app.default_window_icon().unwrap().clone())
                .tooltip("ClipVault")
                .menu(&menu)
                .show_menu_on_left_click(false)
                .on_menu_event(|app, event| match event.id.as_ref() {
                    "open" => toggle_window(app),
                    "lock" => {
                        app.state::<App>().vault.lock().unwrap().lock();
                        let _ = app.emit("vault-locked", ());
                    }
                    "quit" => app.exit(0),
                    _ => {}
                })
                .on_tray_icon_event(|tray, event| {
                    if let TrayIconEvent::Click {
                        button: MouseButton::Left,
                        button_state: MouseButtonState::Up,
                        ..
                    } = event
                    {
                        toggle_window(tray.app_handle());
                    }
                })
                .build(app)?;

            // Global shortcut
            let shortcut = default_shortcut();
            app.handle().plugin(
                tauri_plugin_global_shortcut::Builder::new()
                    .with_handler(move |app, triggered, event| {
                        if triggered == &shortcut && event.state() == ShortcutState::Pressed {
                            toggle_window(app);
                        }
                    })
                    .build(),
            )?;
            if let Err(e) = app.global_shortcut().register(shortcut) {
                log::warn!("could not register Super+V ({e}); open from the tray icon instead");
            }

            Ok(())
        })
        .on_window_event(|window, event| {
            // Closing the window parks the app in the tray; quitting is an
            // explicit choice from the tray menu.
            if let WindowEvent::CloseRequested { api, .. } = event {
                api.prevent_close();
                let _ = window.hide();
            }
        })
        .build(tauri::generate_context!())
        .expect("failed to start ClipVault")
        .run(|_app, event| {
            if let tauri::RunEvent::ExitRequested { code, api, .. } = event {
                // No exit code means the last window closed, which parks the app
                // in the tray. An exit code means someone asked to quit, and
                // blocking that is what made the tray's Quit do nothing.
                if code.is_none() {
                    api.prevent_exit();
                }
            }
        });
}
