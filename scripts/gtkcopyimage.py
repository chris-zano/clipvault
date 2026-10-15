# Test helper: puts an image on the clipboard the way a real GTK app does.
# Random pixels on purpose - the PNG stays incompressible and large enough to
# force an INCR transfer, which is the path screenshots take.
import os, sys, gi
gi.require_version("Gtk", "3.0")
from gi.repository import Gtk, Gdk, GdkPixbuf, GLib

w, h = int(sys.argv[1]), int(sys.argv[2])
data = GLib.Bytes.new(os.urandom(w * h * 3))
pb = GdkPixbuf.Pixbuf.new_from_bytes(data, GdkPixbuf.Colorspace.RGB, False, 8, w, h, w * 3)
Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD).set_image(pb)
print(f"offering {w}x{h} image (~{w*h*3/1048576:.1f} MB raw)")
GLib.timeout_add(4000, Gtk.main_quit)
Gtk.main()
