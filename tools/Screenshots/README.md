# Screenshots

The pictures in the main README (`docs/screenshots`) are regenerated at every release, so they
show the version being shipped. One command does it:

```powershell
.\tools\Screenshots\Make-Screenshots.ps1
```

It builds the app in Debug, seeds a synthetic month of history and incidents (`seed-data.py` -
192.168.1.x, a made-up Wi-Fi network, made-up hardware addresses), opens each page over that data
and saves the window (`capture.ps1`), exports the provider report with the app's own `--export`
and renders it with Edge, and finally crops, pixelates and writes the PNGs (`finish.py`).

Three pages are live rather than seeded - Status, Diagnostics and the Tools (traceroute, speed
test, MTU) - because they show the connection the app is actually on. `finish.py` pixelates the
private address, the gateway and the provider's hops on those, so nothing in `docs/screenshots`
identifies a real network. Look at the result before committing; the live pages change with the
machine.

Needs Python 3 with Pillow (`py -3 -m pip install pillow`), Microsoft Edge, a primary screen of
at least 1250x950, and no Internet Monitor running (the app is single-instance per session). The
raw captures land in `%TEMP%\im-screenshots` for inspection; `-SkipBuild` reuses the last Debug
build, `-Only history,mtu` recaptures just those pictures, and `-SkipCapture` only redoes the
report and the finishing step.

Click positions in `Make-Screenshots.ps1` are window-image coordinates at the 1164x852 window
`capture.ps1` opens. When a page's layout changes, update them there and check the picture.
`capture.ps1` sends every click twice (a synthetic click is occasionally lost while a page is
still laying out; a third field of `1` sends it once, for buttons that start something) and
scrolls a page by dragging its scrollbar thumb (`-Drag "x1,y1,x2,y2"`), because wheel notches go
to the focused control rather than to the page. A picture whose clicks missed is redone with
`-Only`.

Two pictures the README describes in words only: the captive-portal state cannot be staged, and
the incident pop-up does not appear while the main window is open. The network scan is left out
because it can only show real devices.
