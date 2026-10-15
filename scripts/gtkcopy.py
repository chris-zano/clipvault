# Test helper: a real GTK clipboard owner, the way an ordinary app copies.
import sys, gi
gi.require_version("Gtk", "3.0")
from gi.repository import Gtk, Gdk, GLib

text = sys.argv[1]
clip = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
clip.set_text(text, -1)
GLib.timeout_add(1500, Gtk.main_quit)
Gtk.main()
