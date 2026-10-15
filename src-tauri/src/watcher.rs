//! Watches the X11 CLIPBOARD selection for changes.
//!
//! This deliberately does not touch the keyboard. Grabbing Ctrl+C would steal
//! the keystroke from the focused application and would still miss every copy
//! that does not come from that shortcut (context menus, app buttons, scripts).
//! XFixes tells us whenever the selection owner changes, which covers all of
//! them, and requires no access to what the user types.

use anyhow::{anyhow, bail, Result};
use std::collections::VecDeque;
use std::sync::mpsc::Sender;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use x11rb::connection::Connection;
use x11rb::protocol::xfixes::{self, ConnectionExt as XfixesConnectionExt, SelectionEventMask};
use x11rb::protocol::xproto::{
    Atom, AtomEnum, ConnectionExt as XprotoConnectionExt, CreateWindowAux, EventMask, PropMode,
    SelectionNotifyEvent, Window, WindowClass,
};
use x11rb::protocol::Event;
use x11rb::rust_connection::RustConnection;
use x11rb::wrapper::ConnectionExt as _;

use crate::store::hash_of;

const FETCH_TIMEOUT: Duration = Duration::from_millis(500);
/// Anything larger is almost certainly not something you want in a history list.
const MAX_BYTES: usize = 2 * 1024 * 1024;

/// Lets the app suppress capture of clipboard writes it made itself, so that
/// pasting from history does not create a duplicate entry and a secret copied
/// out of the vault never reaches the plaintext history table.
#[derive(Default)]
pub struct Suppressor {
    inner: Mutex<Option<(Option<String>, Instant)>>,
}

impl Suppressor {
    /// Skip the next capture only if its content matches `text`.
    pub fn expect(&self, text: &str) {
        *self.inner.lock().unwrap() =
            Some((Some(hash_of(text)), Instant::now() + Duration::from_secs(3)));
    }

    /// Skip the next capture whatever it contains. Used for vault secrets.
    pub fn expect_any(&self) {
        *self.inner.lock().unwrap() = Some((None, Instant::now() + Duration::from_secs(3)));
    }

    fn should_skip(&self, hash: &str) -> bool {
        let mut guard = self.inner.lock().unwrap();
        // The deadline keeps a suppression that never fired from swallowing an
        // unrelated copy later on.
        match guard.as_ref() {
            Some((_, deadline)) if Instant::now() > *deadline => {
                *guard = None;
                false
            }
            Some((Some(expected), _)) if expected == hash => {
                *guard = None;
                true
            }
            Some((None, _)) => {
                *guard = None;
                true
            }
            _ => false,
        }
    }
}

struct Atoms {
    clipboard: Atom,
    targets: Atom,
    incr: Atom,
    prop: Atom,
    time_probe: Atom,
    password_hint: Atom,
    text_targets: Vec<Atom>,
}

struct Watcher {
    conn: RustConnection,
    win: Window,
    atoms: Atoms,
    queued: VecDeque<Event>,
}

fn intern(conn: &RustConnection, name: &str) -> Result<Atom> {
    Ok(conn.intern_atom(false, name.as_bytes())?.reply()?.atom)
}

impl Watcher {
    fn new() -> Result<Self> {
        let (conn, screen_num) = x11rb::connect(None)
            .map_err(|e| anyhow!("cannot connect to the X server: {e}"))?;
        let root = conn.setup().roots[screen_num].root;

        // XFixes requires a version handshake before any of its requests work.
        xfixes::query_version(&conn, 5, 0)?.reply()?;

        let win = conn.generate_id()?;
        conn.create_window(
            x11rb::COPY_DEPTH_FROM_PARENT,
            win,
            root,
            0,
            0,
            1,
            1,
            0,
            WindowClass::INPUT_OUTPUT,
            x11rb::COPY_FROM_PARENT,
            &CreateWindowAux::new().event_mask(EventMask::PROPERTY_CHANGE),
        )?;

        let atoms = Atoms {
            clipboard: intern(&conn, "CLIPBOARD")?,
            targets: intern(&conn, "TARGETS")?,
            incr: intern(&conn, "INCR")?,
            prop: intern(&conn, "CLIPVAULT_SELECTION")?,
            time_probe: intern(&conn, "CLIPVAULT_TIME_PROBE")?,
            password_hint: intern(&conn, "x-kde-passwordManagerHint")?,
            text_targets: vec![
                intern(&conn, "text/plain;charset=utf-8")?,
                intern(&conn, "UTF8_STRING")?,
                intern(&conn, "text/plain")?,
                Atom::from(AtomEnum::STRING),
            ],
        };

        conn.xfixes_select_selection_input(
            win,
            atoms.clipboard,
            SelectionEventMask::SET_SELECTION_OWNER
                | SelectionEventMask::SELECTION_WINDOW_DESTROY
                | SelectionEventMask::SELECTION_CLIENT_CLOSE,
        )?;
        conn.flush()?;

        Ok(Watcher { conn, win, atoms, queued: VecDeque::new() })
    }

    fn next_event(&mut self) -> Result<Event> {
        if let Some(e) = self.queued.pop_front() {
            return Ok(e);
        }
        Ok(self.conn.wait_for_event()?)
    }

    /// Waits for the SelectionNotify answering a ConvertSelection, holding on to
    /// any other events that arrive meanwhile so they are not lost.
    fn await_notify(&mut self, deadline: Instant) -> Result<Option<SelectionNotifyEvent>> {
        loop {
            if Instant::now() > deadline {
                return Ok(None);
            }
            match self.conn.poll_for_event()? {
                Some(Event::SelectionNotify(e)) if e.requestor == self.win => return Ok(Some(e)),
                Some(other) => self.queued.push_back(other),
                None => std::thread::sleep(Duration::from_millis(4)),
            }
        }
    }

    /// Obtains a current server timestamp the ICCCM way: a zero-length append
    /// to a property on our own window comes back as a PropertyNotify carrying
    /// the server's clock.
    ///
    /// ConvertSelection wants the timestamp of the event that prompted it, and
    /// owners may refuse a time that predates their ownership. Since we only ask
    /// after seeing the ownership change, a timestamp fetched here is always
    /// late enough, which the XFixes event time is not guaranteed to be.
    fn server_time(&mut self) -> Result<u32> {
        self.conn.change_property8(
            PropMode::APPEND,
            self.win,
            self.atoms.time_probe,
            AtomEnum::STRING,
            &[],
        )?;
        self.conn.flush()?;

        let deadline = Instant::now() + Duration::from_millis(300);
        loop {
            if Instant::now() > deadline {
                return Ok(x11rb::CURRENT_TIME);
            }
            match self.conn.poll_for_event()? {
                Some(Event::PropertyNotify(e))
                    if e.window == self.win && e.atom == self.atoms.time_probe =>
                {
                    return Ok(e.time)
                }
                Some(other) => self.queued.push_back(other),
                None => std::thread::sleep(Duration::from_millis(2)),
            }
        }
    }

    /// Asks the current owner for the selection in `target`.
    fn fetch(&mut self, target: Atom) -> Result<Option<Vec<u8>>> {
        let time = self.server_time()?;
        for attempt in 0..2 {
            self.conn.delete_property(self.win, self.atoms.prop)?;
            self.conn.convert_selection(
                self.win,
                self.atoms.clipboard,
                target,
                self.atoms.prop,
                time,
            )?;
            self.conn.flush()?;

            let notify = match self.await_notify(Instant::now() + FETCH_TIMEOUT)? {
                Some(n) => n,
                None => {
                    log::debug!("selection fetch timed out (target {target})");
                    return Ok(None);
                }
            };
            if notify.property == x11rb::NONE {
                // An owner that is still starting up can refuse the first ask.
                if attempt == 0 {
                    std::thread::sleep(Duration::from_millis(60));
                    continue;
                }
                log::debug!("owner refused target {target}");
                return Ok(None);
            }

            let reply = self
                .conn
                .get_property(true, self.win, self.atoms.prop, AtomEnum::ANY, 0, 0x1FFF_FFFF)?
                .reply()?;

            if reply.type_ == self.atoms.incr {
                // Incremental transfers are only used for very large payloads,
                // which we would discard on size anyway.
                log::debug!("skipping INCR clipboard transfer");
                return Ok(None);
            }
            return Ok(Some(reply.value));
        }
        Ok(None)
    }

    fn targets(&mut self) -> Result<Vec<Atom>> {
        let raw = match self.fetch(self.atoms.targets)? {
            Some(v) => v,
            None => return Ok(vec![]),
        };
        Ok(raw
            .chunks_exact(4)
            .map(|c| u32::from_ne_bytes([c[0], c[1], c[2], c[3]]))
            .collect())
    }

    /// Returns the clipboard text, or None when it holds something we skip:
    /// non-text content, an oversized payload, or a password manager's secret.
    fn read_text(&mut self) -> Result<Option<String>> {
        let targets = self.targets()?;
        if targets.is_empty() {
            return Ok(None);
        }

        // Password managers tag their copies so that history tools leave them
        // alone. Honour that - those belong in the vault, by hand, not here.
        if targets.contains(&self.atoms.password_hint) {
            if let Some(v) = self.fetch(self.atoms.password_hint)? {
                if v.eq_ignore_ascii_case(b"secret") {
                    log::info!("skipped a copy flagged as a password manager secret");
                    return Ok(None);
                }
            }
        }

        let chosen = self
            .atoms
            .text_targets
            .iter()
            .copied()
            .find(|t| targets.contains(t));
        let Some(target) = chosen else {
            return Ok(None); // image, file list, or some app-private format
        };

        let Some(bytes) = self.fetch(target)? else {
            return Ok(None);
        };
        if bytes.len() > MAX_BYTES {
            log::debug!("skipping {} byte clipboard payload", bytes.len());
            return Ok(None);
        }

        // STRING is latin-1 by spec; everything else we ask for is UTF-8.
        let text = if target == Atom::from(AtomEnum::STRING) {
            bytes.iter().map(|&b| b as char).collect()
        } else {
            String::from_utf8_lossy(&bytes).into_owned()
        };
        Ok(Some(text))
    }

    fn run(&mut self, tx: Sender<String>, suppress: Arc<Suppressor>) -> Result<()> {
        loop {
            let event = self.next_event()?;
            let Event::XfixesSelectionNotify(e) = event else { continue };
            // owner == NONE is the previous owner going away, not a new copy.
            if e.owner == self.win || e.owner == x11rb::NONE {
                continue;
            }

            // The new owner has just taken the selection; give it a moment to be
            // ready to answer ConvertSelection.
            std::thread::sleep(Duration::from_millis(20));

            let text = match self.read_text() {
                Ok(Some(t)) => t,
                Ok(None) => continue,
                Err(err) => {
                    log::warn!("clipboard read failed: {err}");
                    continue;
                }
            };
            if text.trim().is_empty() {
                continue;
            }
            if suppress.should_skip(&hash_of(&text)) {
                continue;
            }
            if tx.send(text).is_err() {
                bail!("clipboard consumer went away");
            }
        }
    }
}

/// Starts the watcher on its own thread with its own X connection.
pub fn spawn(tx: Sender<String>, suppress: Arc<Suppressor>) {
    std::thread::Builder::new()
        .name("clipboard-watcher".into())
        .spawn(move || match Watcher::new() {
            Ok(mut w) => {
                log::info!("clipboard watcher running");
                if let Err(e) = w.run(tx, suppress) {
                    log::error!("clipboard watcher stopped: {e}");
                }
            }
            Err(e) => log::error!("clipboard watcher failed to start: {e}"),
        })
        .expect("failed to spawn clipboard watcher thread");
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn suppresses_only_the_matching_text() {
        let s = Suppressor::default();
        s.expect("ours");
        assert!(!s.should_skip(&hash_of("someone else's")));
        s.expect("ours");
        assert!(s.should_skip(&hash_of("ours")));
    }

    #[test]
    fn suppression_is_consumed_once() {
        let s = Suppressor::default();
        s.expect("ours");
        assert!(s.should_skip(&hash_of("ours")));
        assert!(!s.should_skip(&hash_of("ours")), "must not swallow a real re-copy");
    }

    #[test]
    fn blanket_suppression_skips_any_content() {
        let s = Suppressor::default();
        s.expect_any();
        assert!(s.should_skip(&hash_of("a vault secret nobody predicted")));
    }

    #[test]
    fn stale_suppression_does_not_eat_a_later_copy() {
        let s = Suppressor::default();
        *s.inner.lock().unwrap() =
            Some((None, Instant::now() - Duration::from_secs(1)));
        assert!(!s.should_skip(&hash_of("unrelated copy")));
    }
}
