"""Turns the raw captures of Make-Screenshots.ps1 into the files the README embeds: crops the
empty space off, pixelates the few values on the live pages that identify a real network (this
computer's private address, the gateway, the provider's hops), and writes PNGs into
docs/screenshots. The seeded pages need no pixelating - their network is made up.

Usage: py -3 finish.py <work-dir> <docs/screenshots>   (needs Pillow: py -3 -m pip install pillow)
"""
import os, sys
from PIL import Image

work, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)


def load(name):
    return Image.open(os.path.join(work, name)).convert("RGB")


def pixelate(img, box, factor=9):
    region = img.crop(box)
    small = region.resize((max(1, region.width // factor), max(1, region.height // factor)), Image.BILINEAR)
    img.paste(small.resize(region.size, Image.NEAREST), box)


def save(name, img, crop=None):
    if crop is not None:
        img = img.crop(crop)
    img.save(os.path.join(out, name), optimize=True)
    print(f"  {name} {img.size[0]}x{img.size[1]}")


# Live pages: the address values of steps 3 and 4 on Status (same layout in every theme and
# language), and the hops before the destination on the traceroute.
STATUS_BOXES = [(464, 609, 540, 630), (464, 655, 521, 676)]
TRACE_BOXES = [(280, 266, 345, 286), (280, 315, 810, 352), (280, 364, 372, 384), (280, 413, 355, 433)]

status = load("status.png")
for box in STATUS_BOXES:
    pixelate(status, box)
save("status.png", status, (0, 0, 1164, 870))

dark = load("status-dark.png")
for box in STATUS_BOXES:
    pixelate(dark, box)
save("status-dark.png", dark, (0, 0, 1164, 870))

trace = load("traceroute.png")
for box in TRACE_BOXES:
    pixelate(trace, box)
save("traceroute.png", trace, (0, 0, 1164, 520))

save("speedtest.png", load("speedtest.png"), (0, 0, 1164, 380))
save("mtu.png", load("mtu.png"), (0, 0, 1164, 420))
save("diagnostics.png", load("diagnostics.png"), (0, 0, 1164, 850))

# Seeded pages.
save("reports.png", load("reports.png"), (0, 0, 1164, 560))
save("incidents.png", load("incidents.png"))
save("incident-outage.png", load("incident-outage.png"))
save("incident-network-change.png", load("incident-network-change.png"), (0, 0, 1164, 880))
save("incident-packet-loss.png", load("incident-packet-loss.png"), (0, 0, 1164, 760))
save("history.png", load("history.png"))
save("history-wifi.png", load("history-wifi.png"))
save("history-traffic.png", load("history-traffic.png"))
save("history-packet-loss.png", load("history-packet-loss.png"))
save("history-speedtest.png", load("history-speedtest.png"))
save("settings-network.png", load("settings-network.png"), (0, 232, 1164, 705))  # the Network changes and Network health cards
save("provider-report.png", load("provider-report.png"))
