use anyhow::Result;
use rusqlite::{params, Connection, OptionalExtension};
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::path::Path;
use std::time::{SystemTime, UNIX_EPOCH};

/// Unpinned entries beyond this count are dropped, oldest first. Pins never expire.
const MAX_UNPINNED: usize = 500;
const PREVIEW_CHARS: usize = 220;

#[derive(Serialize)]
pub struct Entry {
    pub id: i64,
    pub preview: String,
    pub truncated: bool,
    pub bytes: i64,
    pub pinned: bool,
    pub created_at: i64,
    pub last_used_at: i64,
    pub use_count: i64,
}

#[derive(Serialize)]
pub struct VaultItem {
    pub id: i64,
    pub label: String,
    pub created_at: i64,
}

pub struct Store {
    conn: Connection,
}

pub fn now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

pub fn hash_of(text: &str) -> String {
    let mut h = Sha256::new();
    h.update(text.as_bytes());
    format!("{:x}", h.finalize())
}

fn preview_of(text: &str) -> (String, bool) {
    let collapsed = text.split_whitespace().collect::<Vec<_>>().join(" ");
    let src = if collapsed.is_empty() { text.trim() } else { &collapsed };
    let mut out = String::new();
    for (i, c) in src.chars().enumerate() {
        if i >= PREVIEW_CHARS {
            return (out, true);
        }
        out.push(c);
    }
    (out, false)
}

impl Store {
    pub fn open(path: &Path) -> Result<Self> {
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let conn = Connection::open(path)?;
        conn.pragma_update(None, "journal_mode", "WAL")?;
        conn.pragma_update(None, "synchronous", "NORMAL")?;
        let store = Store { conn };
        store.migrate()?;
        Ok(store)
    }

    fn migrate(&self) -> Result<()> {
        self.conn.execute_batch(
            r#"
            CREATE TABLE IF NOT EXISTS entries (
                id           INTEGER PRIMARY KEY,
                content      TEXT    NOT NULL,
                hash         TEXT    NOT NULL UNIQUE,
                preview      TEXT    NOT NULL,
                truncated    INTEGER NOT NULL DEFAULT 0,
                bytes        INTEGER NOT NULL,
                pinned       INTEGER NOT NULL DEFAULT 0,
                created_at   INTEGER NOT NULL,
                last_used_at INTEGER NOT NULL,
                use_count    INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_entries_recent
                ON entries (pinned DESC, last_used_at DESC);

            CREATE TABLE IF NOT EXISTS vault_meta (
                id            INTEGER PRIMARY KEY CHECK (id = 1),
                salt          BLOB NOT NULL,
                verify_nonce  BLOB NOT NULL,
                verify_ct     BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS vault_entries (
                id         INTEGER PRIMARY KEY,
                label      TEXT NOT NULL,
                nonce      BLOB NOT NULL,
                ciphertext BLOB NOT NULL,
                created_at INTEGER NOT NULL
            );
            "#,
        )?;
        Ok(())
    }

    // ---- history -------------------------------------------------------

    /// Returns the row id, or None when the text was empty/whitespace only.
    pub fn record(&self, text: &str) -> Result<Option<i64>> {
        if text.trim().is_empty() {
            return Ok(None);
        }
        let hash = hash_of(text);
        let ts = now();

        // Re-copying something already in history moves it back to the top
        // rather than creating a duplicate row.
        let existing: Option<i64> = self
            .conn
            .query_row("SELECT id FROM entries WHERE hash = ?1", params![hash], |r| r.get(0))
            .optional()?;

        if let Some(id) = existing {
            self.conn.execute(
                "UPDATE entries SET last_used_at = ?1, use_count = use_count + 1 WHERE id = ?2",
                params![ts, id],
            )?;
            return Ok(Some(id));
        }

        let (preview, truncated) = preview_of(text);
        self.conn.execute(
            "INSERT INTO entries (content, hash, preview, truncated, bytes, created_at, last_used_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?6)",
            params![text, hash, preview, truncated as i64, text.len() as i64, ts],
        )?;
        let id = self.conn.last_insert_rowid();
        self.trim()?;
        Ok(Some(id))
    }

    fn trim(&self) -> Result<()> {
        self.conn.execute(
            "DELETE FROM entries WHERE pinned = 0 AND id NOT IN (
                 SELECT id FROM entries WHERE pinned = 0
                 ORDER BY last_used_at DESC LIMIT ?1
             )",
            params![MAX_UNPINNED as i64],
        )?;
        Ok(())
    }

    pub fn list(&self, query: &str, limit: i64) -> Result<Vec<Entry>> {
        let q = query.trim();
        // Substring match, not full-text: clipboard searches are things like
        // "AKIA" inside a key or a fragment of a URL, which a word tokenizer
        // would never match.
        let (sql, like) = if q.is_empty() {
            (
                "SELECT id, preview, truncated, bytes, pinned, created_at, last_used_at, use_count
                 FROM entries
                 ORDER BY pinned DESC, last_used_at DESC LIMIT ?1"
                    .to_string(),
                None,
            )
        } else {
            (
                "SELECT id, preview, truncated, bytes, pinned, created_at, last_used_at, use_count
                 FROM entries
                 WHERE content LIKE ?2 ESCAPE '\\'
                 ORDER BY pinned DESC, last_used_at DESC LIMIT ?1"
                    .to_string(),
                Some(format!(
                    "%{}%",
                    q.replace('\\', "\\\\").replace('%', "\\%").replace('_', "\\_")
                )),
            )
        };

        let mut stmt = self.conn.prepare(&sql)?;
        let map = |r: &rusqlite::Row| -> rusqlite::Result<Entry> {
            Ok(Entry {
                id: r.get(0)?,
                preview: r.get(1)?,
                truncated: r.get::<_, i64>(2)? != 0,
                bytes: r.get(3)?,
                pinned: r.get::<_, i64>(4)? != 0,
                created_at: r.get(5)?,
                last_used_at: r.get(6)?,
                use_count: r.get(7)?,
            })
        };
        let rows = match like {
            Some(l) => stmt.query_map(params![limit, l], map)?.collect::<Result<Vec<_>, _>>()?,
            None => stmt.query_map(params![limit], map)?.collect::<Result<Vec<_>, _>>()?,
        };
        Ok(rows)
    }

    pub fn content(&self, id: i64) -> Result<Option<String>> {
        Ok(self
            .conn
            .query_row("SELECT content FROM entries WHERE id = ?1", params![id], |r| r.get(0))
            .optional()?)
    }

    pub fn mark_used(&self, id: i64) -> Result<()> {
        self.conn.execute(
            "UPDATE entries SET last_used_at = ?1, use_count = use_count + 1 WHERE id = ?2",
            params![now(), id],
        )?;
        Ok(())
    }

    pub fn set_pinned(&self, id: i64, pinned: bool) -> Result<()> {
        self.conn.execute(
            "UPDATE entries SET pinned = ?1 WHERE id = ?2",
            params![pinned as i64, id],
        )?;
        Ok(())
    }

    pub fn delete(&self, id: i64) -> Result<()> {
        self.conn.execute("DELETE FROM entries WHERE id = ?1", params![id])?;
        Ok(())
    }

    /// Clears history. Pinned entries survive unless `include_pinned`.
    pub fn clear(&self, include_pinned: bool) -> Result<()> {
        if include_pinned {
            self.conn.execute("DELETE FROM entries", [])?;
        } else {
            self.conn.execute("DELETE FROM entries WHERE pinned = 0", [])?;
        }
        Ok(())
    }

    // ---- vault ---------------------------------------------------------

    pub fn vault_meta(&self) -> Result<Option<(Vec<u8>, Vec<u8>, Vec<u8>)>> {
        Ok(self
            .conn
            .query_row(
                "SELECT salt, verify_nonce, verify_ct FROM vault_meta WHERE id = 1",
                [],
                |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
            )
            .optional()?)
    }

    pub fn set_vault_meta(&self, salt: &[u8], nonce: &[u8], ct: &[u8]) -> Result<()> {
        self.conn.execute(
            "INSERT OR REPLACE INTO vault_meta (id, salt, verify_nonce, verify_ct)
             VALUES (1, ?1, ?2, ?3)",
            params![salt, nonce, ct],
        )?;
        Ok(())
    }

    pub fn vault_list(&self) -> Result<Vec<VaultItem>> {
        let mut stmt = self
            .conn
            .prepare("SELECT id, label, created_at FROM vault_entries ORDER BY label COLLATE NOCASE")?;
        let rows = stmt
            .query_map([], |r| {
                Ok(VaultItem { id: r.get(0)?, label: r.get(1)?, created_at: r.get(2)? })
            })?
            .collect::<Result<Vec<_>, _>>()?;
        Ok(rows)
    }

    pub fn vault_insert(&self, label: &str, nonce: &[u8], ct: &[u8]) -> Result<i64> {
        self.conn.execute(
            "INSERT INTO vault_entries (label, nonce, ciphertext, created_at)
             VALUES (?1, ?2, ?3, ?4)",
            params![label, nonce, ct, now()],
        )?;
        Ok(self.conn.last_insert_rowid())
    }

    pub fn vault_get(&self, id: i64) -> Result<Option<(Vec<u8>, Vec<u8>)>> {
        Ok(self
            .conn
            .query_row(
                "SELECT nonce, ciphertext FROM vault_entries WHERE id = ?1",
                params![id],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )
            .optional()?)
    }

    pub fn vault_delete(&self, id: i64) -> Result<()> {
        self.conn.execute("DELETE FROM vault_entries WHERE id = ?1", params![id])?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn store() -> (Store, tempdir::Dir) {
        let dir = tempdir::Dir::new();
        let s = Store::open(&dir.path().join("t.db")).unwrap();
        (s, dir)
    }

    #[test]
    fn records_and_reads_back() {
        let (s, _d) = store();
        let id = s.record("hello world").unwrap().unwrap();
        assert_eq!(s.content(id).unwrap().as_deref(), Some("hello world"));
    }

    #[test]
    fn ignores_blank_copies() {
        let (s, _d) = store();
        assert!(s.record("   \n\t ").unwrap().is_none());
        assert_eq!(s.list("", 10).unwrap().len(), 0);
    }

    #[test]
    fn recopying_moves_to_top_instead_of_duplicating() {
        let (s, _d) = store();
        let first = s.record("repeated").unwrap().unwrap();
        s.record("other").unwrap();
        let again = s.record("repeated").unwrap().unwrap();

        assert_eq!(first, again, "same text should reuse the row");
        let rows = s.list("", 10).unwrap();
        assert_eq!(rows.len(), 2);
        assert_eq!(rows[0].id, first, "the re-copied entry comes back first");
        assert_eq!(rows[0].use_count, 1);
    }

    #[test]
    fn search_matches_inside_words() {
        let (s, _d) = store();
        s.record("https://example.com/p?token=AKIAIOSFODNN7EXAMPLE").unwrap();
        s.record("nothing to see").unwrap();

        // The substring is mid-token - a word tokenizer would miss this.
        assert_eq!(s.list("AKIA", 10).unwrap().len(), 1);
        assert_eq!(s.list("token=", 10).unwrap().len(), 1);
        assert_eq!(s.list("absent", 10).unwrap().len(), 0);
    }

    #[test]
    fn search_treats_wildcards_literally() {
        let (s, _d) = store();
        s.record("plain text").unwrap();
        s.record("50% off").unwrap();
        // '%' must not act as a LIKE wildcard matching everything.
        let hits = s.list("%", 10).unwrap();
        assert_eq!(hits.len(), 1);
        assert!(hits[0].preview.contains("50%"));
    }

    #[test]
    fn pinned_entries_sort_first_and_survive_clear() {
        let (s, _d) = store();
        let old = s.record("pin me").unwrap().unwrap();
        s.record("newer").unwrap();
        s.set_pinned(old, true).unwrap();

        assert_eq!(s.list("", 10).unwrap()[0].id, old);

        s.clear(false).unwrap();
        let left = s.list("", 10).unwrap();
        assert_eq!(left.len(), 1);
        assert_eq!(left[0].id, old);

        s.clear(true).unwrap();
        assert_eq!(s.list("", 10).unwrap().len(), 0);
    }

    #[test]
    fn trim_drops_oldest_unpinned_but_keeps_pins() {
        let (s, _d) = store();
        let pinned = s.record("keep forever").unwrap().unwrap();
        s.set_pinned(pinned, true).unwrap();
        for i in 0..MAX_UNPINNED + 25 {
            s.record(&format!("entry {i}")).unwrap();
        }
        let all = s.list("", 2000).unwrap();
        assert_eq!(all.len(), MAX_UNPINNED + 1, "pin plus the capped unpinned set");
        assert!(all.iter().any(|e| e.id == pinned));
    }

    #[test]
    fn preview_is_truncated_and_flagged() {
        let (s, _d) = store();
        let long = "x".repeat(PREVIEW_CHARS + 50);
        let id = s.record(&long).unwrap().unwrap();
        let row = s.list("", 10).unwrap().into_iter().find(|e| e.id == id).unwrap();
        assert!(row.truncated);
        assert_eq!(row.preview.chars().count(), PREVIEW_CHARS);
        // The full text is still retrievable even though the preview is cut.
        assert_eq!(s.content(id).unwrap().unwrap().len(), long.len());
    }
}

/// Minimal self-cleaning temp directory for the tests.
#[cfg(test)]
pub mod tempdir {
    use std::path::{Path, PathBuf};

    pub struct Dir(PathBuf);

    impl Dir {
        pub fn new() -> Self {
            let p = std::env::temp_dir().join(format!(
                "clipvault-test-{}-{:?}",
                std::process::id(),
                std::thread::current().id()
            ));
            let _ = std::fs::remove_dir_all(&p);
            std::fs::create_dir_all(&p).unwrap();
            Dir(p)
        }
        pub fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for Dir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
}
