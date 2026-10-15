//! Test helper: exercises the exact copy-back path `copy_entry` uses for an
//! image - read the stored PNG, decode to RGBA, hand it to arboard.
fn main() {
    let path = std::env::args().nth(1).expect("png path");
    let png = std::fs::read(path).expect("read png");
    let (w, h, rgba) = clipvault_lib::media_to_rgba(&png).expect("decode");
    let mut cb = arboard::Clipboard::new().expect("clipboard");
    cb.set_image(arboard::ImageData {
        width: w as usize,
        height: h as usize,
        bytes: rgba.into(),
    })
    .expect("set image");
    println!("put {w}x{h} on the clipboard");
    std::thread::sleep(std::time::Duration::from_millis(3000));
}
