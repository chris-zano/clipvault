# Test helper: reads the clipboard the way a real GTK app does on Ctrl+V.
import time, gi
gi.require_version("Gtk", "3.0")
from gi.repository import Gtk, Gdk
time.sleep(0.4)
clip = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
text = clip.wait_for_text()
print("PASTED:", repr(text))
