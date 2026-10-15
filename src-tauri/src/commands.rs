use serde::Serialize;
use std::sync::{Arc, Mutex};
use tauri::State;

use crate::media;
use crate::store::{Entry, Store, VaultItem};
use crate::vault::{Vault, AUTO_LOCK_MINUTES};
use crate::watcher::Suppressor;

pub struct App {
    pub store: Mutex<Store>,
    pub vault: Mutex<Vault>,
    pub clipboard: Mutex<arboard::Clipboard>,
    pub suppress: Arc<Suppressor>,
}

type Out<T> = Result<T, String>;

fn err<E: std::fmt::Display>(e: E) -> String {
    e.to_string()
}

impl App {
    /// Applies the idle timeout before any vault operation, so a vault left
    /// open behind a locked screen is already shut by the time it is asked for.
    fn vault_checked(&self) -> std::sync::MutexGuard<'_, Vault> {
        let mut v = self.vault.lock().unwrap();
        v.lock_if_idle(AUTO_LOCK_MINUTES);
        v
    }

    fn put_on_clipboard(&self, text: &str) -> Result<(), String> {
        self.clipboard.lock().unwrap().set_text(text).map_err(err)
    }

    fn put_image_on_clipboard(&self, png: &[u8]) -> Result<(), String> {
        let (w, h, rgba) = media::to_rgba(png).map_err(err)?;
        self.clipboard
            .lock()
            .unwrap()
            .set_image(arboard::ImageData {
                width: w as usize,
                height: h as usize,
                bytes: rgba.into(),
            })
            .map_err(err)
    }
}

// ---- history ------------------------------------------------------------

#[tauri::command]
pub fn list_entries(app: State<'_, App>, query: String, limit: i64) -> Out<Vec<Entry>> {
    app.store.lock().unwrap().list(&query, limit.clamp(1, 1000)).map_err(err)
}

#[tauri::command]
pub fn copy_entry(app: State<'_, App>, id: i64) -> Out<()> {
    let kind = {
        let store = app.store.lock().unwrap();
        store.kind(id).map_err(err)?.ok_or("entry no longer exists")?
    };

    if kind == "image" {
        let png = {
            let store = app.store.lock().unwrap();
            store.image_png(id).map_err(err)?.ok_or("image is missing")?
        };
        // The hash is over pixels, so it still matches after arboard re-encodes
        // the image on its way back onto the clipboard.
        let clip = media::prepare(&png).map_err(err)?;
        app.suppress.expect_hash(&clip.hash);
        app.put_image_on_clipboard(&png)?;
    } else {
        let content = {
            let store = app.store.lock().unwrap();
            store.content(id).map_err(err)?.ok_or("entry no longer exists")?
        };
        // Tell the watcher to ignore the selection change we are about to cause,
        // otherwise pasting from history re-records the same text.
        app.suppress.expect(&content);
        app.put_on_clipboard(&content)?;
    }
    app.store.lock().unwrap().mark_used(id).map_err(err)
}

#[tauri::command]
pub fn entry_content(app: State<'_, App>, id: i64) -> Out<String> {
    app.store
        .lock()
        .unwrap()
        .content(id)
        .map_err(err)?
        .ok_or_else(|| "entry no longer exists".to_string())
}

#[tauri::command]
pub fn set_pinned(app: State<'_, App>, id: i64, pinned: bool) -> Out<()> {
    app.store.lock().unwrap().set_pinned(id, pinned).map_err(err)
}

#[tauri::command]
pub fn delete_entry(app: State<'_, App>, id: i64) -> Out<()> {
    app.store.lock().unwrap().delete(id).map_err(err)
}

#[tauri::command]
pub fn clear_history(app: State<'_, App>, include_pinned: bool) -> Out<()> {
    app.store.lock().unwrap().clear(include_pinned).map_err(err)
}

// ---- vault --------------------------------------------------------------

#[derive(Serialize)]
pub struct VaultStatus {
    exists: bool,
    unlocked: bool,
    seconds_until_lock: Option<u64>,
}

#[tauri::command]
pub fn vault_status(app: State<'_, App>) -> Out<VaultStatus> {
    let exists = app.store.lock().unwrap().vault_meta().map_err(err)?.is_some();
    let vault = app.vault_checked();
    Ok(VaultStatus {
        exists,
        unlocked: vault.is_unlocked(),
        seconds_until_lock: vault.seconds_until_lock(AUTO_LOCK_MINUTES),
    })
}

#[tauri::command]
pub fn vault_init(app: State<'_, App>, password: String) -> Out<()> {
    let store = app.store.lock().unwrap();
    app.vault.lock().unwrap().init(&store, &password).map_err(err)
}

#[tauri::command]
pub fn vault_unlock(app: State<'_, App>, password: String) -> Out<()> {
    let store = app.store.lock().unwrap();
    app.vault.lock().unwrap().unlock(&store, &password).map_err(err)
}

#[tauri::command]
pub fn vault_lock(app: State<'_, App>) -> Out<()> {
    app.vault.lock().unwrap().lock();
    Ok(())
}

#[tauri::command]
pub fn vault_items(app: State<'_, App>) -> Out<Vec<VaultItem>> {
    app.store.lock().unwrap().vault_list().map_err(err)
}

#[tauri::command]
pub fn vault_add(app: State<'_, App>, label: String, secret: String) -> Out<i64> {
    let label = label.trim().to_string();
    if label.is_empty() {
        return Err("give the entry a label".into());
    }
    if secret.is_empty() {
        return Err("nothing to store".into());
    }
    let vault = app.vault_checked();
    let (nonce, ct) = vault.encrypt(&secret).map_err(err)?;
    app.store.lock().unwrap().vault_insert(&label, &nonce, &ct).map_err(err)
}

/// Moves a history entry into the vault: encrypts it, then removes the
/// plaintext row so the secret exists in one place only.
#[tauri::command]
pub fn vault_absorb_entry(app: State<'_, App>, id: i64, label: String) -> Out<i64> {
    let label = label.trim().to_string();
    if label.is_empty() {
        return Err("give the entry a label".into());
    }
    let content = {
        let store = app.store.lock().unwrap();
        if store.kind(id).map_err(err)?.as_deref() == Some("image") {
            return Err("the vault holds text secrets, not images".into());
        }
        store.content(id).map_err(err)?.ok_or("entry no longer exists")?
    };
    let vault = app.vault_checked();
    let (nonce, ct) = vault.encrypt(&content).map_err(err)?;
    drop(vault);

    let store = app.store.lock().unwrap();
    let new_id = store.vault_insert(&label, &nonce, &ct).map_err(err)?;
    store.delete(id).map_err(err)?;
    Ok(new_id)
}

#[tauri::command]
pub fn vault_reveal(app: State<'_, App>, id: i64) -> Out<String> {
    let (nonce, ct) = {
        let store = app.store.lock().unwrap();
        store.vault_get(id).map_err(err)?.ok_or("vault entry no longer exists")?
    };
    let mut vault = app.vault_checked();
    let secret = vault.decrypt(&nonce, &ct).map_err(err)?;
    vault.touch();
    Ok(secret.to_string())
}

#[tauri::command]
pub fn vault_copy(app: State<'_, App>, id: i64) -> Out<()> {
    let (nonce, ct) = {
        let store = app.store.lock().unwrap();
        store.vault_get(id).map_err(err)?.ok_or("vault entry no longer exists")?
    };
    let mut vault = app.vault_checked();
    let secret = vault.decrypt(&nonce, &ct).map_err(err)?;
    vault.touch();
    drop(vault);

    // Unconditional suppression: a vault secret must never land in the
    // plaintext history table, whatever it happens to contain.
    app.suppress.expect_any();
    app.put_on_clipboard(&secret)
}

#[tauri::command]
pub fn vault_delete(app: State<'_, App>, id: i64) -> Out<()> {
    app.store.lock().unwrap().vault_delete(id).map_err(err)
}
