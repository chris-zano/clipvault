# Test helper: reads an image off the clipboard the way a real GTK app does.
import time, gi
gi.require_version("Gtk", "3.0")
from gi.repository import Gtk, Gdk
time.sleep(0.5)
pb = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD).wait_for_image()
print("PASTED IMAGE:", f"{pb.get_width()}x{pb.get_height()}" if pb else None)
