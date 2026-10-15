//! Test helper: puts text on the CLIPBOARD selection and holds it briefly,
//! standing in for a real application doing Ctrl+C.
fn main() {
    let text = std::env::args().nth(1).unwrap_or_else(|| "hello".into());
    let mut cb = arboard::Clipboard::new().expect("clipboard");
    cb.set_text(&text).expect("set");
    std::thread::sleep(std::time::Duration::from_millis(1200));
}
