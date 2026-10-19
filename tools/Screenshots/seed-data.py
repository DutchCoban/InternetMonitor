"""Writes a data directory with thirty days of plausible, synthetic history and a varied set of
incidents, for the README screenshots. Nothing in it is a real network: 192.168.1.x, a fictional
SSID, made-up MAC addresses.

Usage: py -3 seed-data.py <out-dir> [--no-endpoints] [--theme Light|Dark] [--language en|nl|...]

Run by Make-Screenshots.ps1; the variants are the same data with different settings, so every
page is shot over the same month."""
import io, json, os, sys, datetime, random, uuid, math, argparse
args = argparse.ArgumentParser()
args.add_argument("out")
args.add_argument("--no-endpoints", action="store_true", help="disable the configured endpoints (for the live pages, so nothing shows as unreachable)")
args.add_argument("--theme", default="Light")
args.add_argument("--language", default="en")
args = args.parse_args()
out = args.out
hist = os.path.join(out, "history")
os.makedirs(hist, exist_ok=True)
random.seed(2026)
UTC = datetime.timezone.utc
EPOCH = datetime.datetime(1, 1, 1, tzinfo=UTC)
def ticks(t): return int((t - EPOCH).total_seconds() * 10_000_000)
def iso(t): return t.astimezone(UTC).strftime("%Y-%m-%dT%H:%M:%S.000+00:00")
def ident(t): return t.astimezone().strftime("INC-%Y%m%d-%H%M%S.000")
def local(t): return t.astimezone().strftime("%H:%M:%S")
now = datetime.datetime.now(UTC).replace(microsecond=0)
start = now - datetime.timedelta(days=30)
EP_PORTAL, EP_NAS = "a1b2c3d4e5f60718293a4b5c6d7e8f90", "0f1e2d3c4b5a69788796a5b4c3d2e1f0"
AP1, AP2 = "a4:2b:8c:10:20:01", "a4:2b:8c:10:20:02"

# ---- incidents (moments in UTC) ------------------------------------------------------------
t_internet = (now - datetime.timedelta(days=3)).replace(hour=19, minute=42, second=18)
t_dns = (now - datetime.timedelta(days=1)).replace(hour=18, minute=3, second=19)
t_gateway = (now - datetime.timedelta(days=5)).replace(hour=7, minute=12, second=4)
t_link = (now - datetime.timedelta(days=9)).replace(hour=21, minute=5, second=40)
t_move = (now - datetime.timedelta(days=2)).replace(hour=14, minute=20, second=11)
t_apipa = t_move + datetime.timedelta(seconds=15)
t_back = t_move + datetime.timedelta(minutes=33)
t_router = (now - datetime.timedelta(days=12)).replace(hour=10, minute=31, second=2)
t_loss = (now - datetime.timedelta(days=6)).replace(hour=20, minute=14, second=0)
dns_blips = [start + datetime.timedelta(seconds=random.randint(0, 30 * 86400)) for _ in range(44)] + [t_dns + datetime.timedelta(seconds=3)] + [now - datetime.timedelta(minutes=m) for m in (13, 14, 38, 52, 131, 205)]

windows = [  # (start, end, cause key, id) for the outage series
    (t_internet, t_internet + datetime.timedelta(minutes=14, seconds=6), "internet", ident(t_internet + datetime.timedelta(seconds=13))),
    (t_dns, t_dns + datetime.timedelta(seconds=20), "dns", ident(t_dns + datetime.timedelta(seconds=13))),
    (t_gateway, t_gateway + datetime.timedelta(minutes=1, seconds=10), "gateway", ident(t_gateway + datetime.timedelta(seconds=13))),
    (t_link, t_link + datetime.timedelta(minutes=2, seconds=31), "link", ident(t_link + datetime.timedelta(seconds=13))),
    (t_move, t_back, "networkChange", ident(t_move)),
    (t_loss, t_loss + datetime.timedelta(minutes=1), "networkHealth", ident(t_loss)),
]
def in_outage(t):
    for s, e, c, i in windows:
        if s <= t <= e:
            return c, i
    return None

# ---- history ---------------------------------------------------------------------------------
rows = {k: [] for k in ["network", "link", "gateway", "internet", "dns", "https", "wifi-signal", "traffic-down", "traffic-up",
                        "outage", "metered", "ipv6", "dns-failures", f"endpoint-{EP_PORTAL}", f"endpoint-{EP_NAS}"]}
minute_rows = {"packet-loss": [], "jitter": []}
t = start
last_outage = None
while t <= now:
    step = 16 if t > now - datetime.timedelta(days=2) else 60
    hour_local = t.astimezone().hour
    evening = 1.0 + (2.5 if 19 <= hour_local <= 23 else 0.3 if 1 <= hour_local <= 7 else 1.0)
    out_state = in_outage(t)
    blip = any(abs((t - b).total_seconds()) < step for b in dns_blips)
    cause = out_state[0] if out_state else None
    rows["network"].append(f"{ticks(t)},1")
    rows["link"].append(f"{ticks(t)},{0 if cause == 'link' else 1}")
    rows["metered"].append(f"{ticks(t)},0")
    if cause not in ("link", "gateway"):
        rows["gateway"].append(f"{ticks(t)},{random.choice([1, 2, 2, 3, 4, 7])}")
    if cause not in ("link", "gateway", "internet"):
        base = 8 + 3 * math.sin(t.timestamp() / 7000)
        rows["internet"].append(f"{ticks(t)},{max(5, round(base + random.gauss(0, 1.5) + (40 if random.random() < 0.01 else 0)))}")
        rows["ipv6"].append(f"{ticks(t)},{0 if random.random() < 0.005 else 1}")
        if cause != "dns" and not blip:
            rows["dns"].append(f"{ticks(t)},{random.choice([12, 14, 15, 16, 18, 22, 31, 47])}")
        else:
            rows["dns-failures"].append(f"{ticks(t)},1,ownResolverFailed")
        if cause != "dns":
            rows["https"].append(f"{ticks(t)},{round(random.gauss(95, 18), 1)}")
            rows[f"endpoint-{EP_PORTAL}"].append(f"{ticks(t)},{round(random.gauss(140, 25))}")
    rows[f"endpoint-{EP_NAS}"].append(f"{ticks(t)},{random.choice([1, 1, 2, 2, 3])}")
    bssid = AP2 if (hour_local >= 9 and hour_local < 17 and t.astimezone().weekday() < 5) else AP1
    sig = 78 if bssid == AP2 else 86
    rows["wifi-signal"].append(f"{ticks(t)},{max(30, min(99, round(sig + random.gauss(0, 3))))},{bssid}")
    down = max(0.02, random.expovariate(1 / (2.5 * evening)) * (random.random() < 0.9) + (random.random() < 0.04) * random.uniform(40, 180))
    up = max(0.01, down / 12 + random.expovariate(1 / 0.1))
    rows["traffic-down"].append(f"{ticks(t)},{down:.2f},peak={down * random.uniform(1.0, 3.0):.2f}")
    rows["traffic-up"].append(f"{ticks(t)},{up:.2f},peak={up * random.uniform(1.0, 2.5):.2f}")
    state = f"{out_state[0]}|{out_state[1]}" if out_state else None
    rows["outage"].append(f"{ticks(t)},{1 if out_state else 0}" + (f",{state}" if state else ""))
    t += datetime.timedelta(seconds=step)

t = start.replace(second=13)
bad_day = (now - datetime.timedelta(days=6)).date()
while t <= now:
    bad = t.astimezone().date() == bad_day and 19 <= t.astimezone().hour <= 21
    loss = ((10 if random.random() < 0.012 else 0) if not bad else random.choice([0, 10, 10, 20, 30]))
    if in_outage(t) and in_outage(t)[0] in ("internet", "gateway", "link"):
        t += datetime.timedelta(minutes=1)
        continue
    minute_rows["packet-loss"].append(f"{ticks(t)},{loss}")
    minute_rows["jitter"].append(f"{ticks(t)},{round(abs(random.gauss(1.4, 0.6)) + (6 if bad else 0), 1)}")
    t += datetime.timedelta(minutes=1)

speed = {k: [] for k in ["speedtest-download", "speedtest-upload", "speedtest-ping", "speedtest-bufferbloat"]}
t = start + datetime.timedelta(hours=2)
while t <= now:
    slow = random.random() < 0.12
    speed["speedtest-download"].append(f"{ticks(t)},{(random.gauss(300, 25) if slow else random.gauss(488, 14)):.3f}")
    speed["speedtest-upload"].append(f"{ticks(t)},{random.gauss(47.5, 1.8):.3f}")
    speed["speedtest-ping"].append(f"{ticks(t)},{random.gauss(9, 1.2):.2f}")
    speed["speedtest-bufferbloat"].append(f"{ticks(t)},{round(random.choice([4, 6, 9, 14, 21, 38, 55, 120, 240]))}")
    t += datetime.timedelta(hours=6, minutes=random.randint(-20, 20))

for name, lines in {**rows, **minute_rows, **speed}.items():
    io.open(os.path.join(hist, f"{name}.csv"), "w", encoding="utf-8").write("\n".join(lines) + "\n")

# ---- incidents.json --------------------------------------------------------------------------
def meas(**kw): return kw
def steps(adapter="Ok", link="Ok", ip="Ok", gateway="Ok", internet="Ok", dns="Ok", target="Ok", link_text="Connected to Wi-Fi · 86% · 5 GHz",
          signal="86% (-54 dBm)", gw_text="192.168.1.1 reachable (2 ms)", inet_text="Reachable (8 ms)", dns_text="www.ripe.net resolved (16 ms)",
          target_text="3 of 3 targets reachable", dns_detail=None, gw_detail=None, inet_detail=None, target_detail=None, link_detail=None):
    def s(step, state, headline, detail, m):
        d = {"Step": step, "State": state, "Headline": headline, "Measurements": m}
        if detail: d["Detail"] = detail
        return d
    return [
        s("Adapter", adapter, "Wi-Fi is present and enabled", None, meas(**{"Adapter": "Wi-Fi", "Description": "Intel(R) Wi-Fi 6E AX211", "Type": "Wifi", "MAC address": "d4:3b:04:aa:10:55", "Link speed": "1201 Mbit/s"})),
        s("Link", link, link_text, link_detail, meas(**{"Link status": "Operational", "Adapter": "Wi-Fi", "Network (SSID)": "Maple Street", "Access point (BSSID)": AP1, "Signal": signal, "Band": "5 GHz · channel 36", "Wi-Fi standard": "Wi-Fi 6"})),
        s("IpAddress", ip, "192.168.1.42", None, meas(**{"IP address": "192.168.1.42", "Subnet mask": "255.255.255.0", "Gateway": "192.168.1.1", "DNS servers": "1.1.1.1", "DHCP": "On · server 192.168.1.1", "IPv6": "2a02:a45f:8c2e:1:5d3a:7f11:2c0b:91ee"})),
        s("Gateway", gateway, gw_text, gw_detail, meas(**{"Gateway": "192.168.1.1", "ICMP": "Success" if gateway == "Ok" else "TimedOut", "Latency": "2 ms" if gateway == "Ok" else "-"})),
        s("PublicIp", internet, inet_text, inet_detail, meas(**{"9.9.9.9": "8 ms" if internet == "Ok" else "no reply", "8.8.8.8": "9 ms" if internet == "Ok" else "no reply", "1.1.1.1": "7 ms" if internet == "Ok" else "no reply"})),
        s("Dns", dns, dns_text, dns_detail, meas(**{"Hostname": "www.ripe.net", "DNS servers": "1.1.1.1"} | ({"Answered by": "1.1.1.1", "Resolved to": "193.0.6.139", "Latency": "16 ms"} if dns == "Ok" else {"1.1.1.1": "No DNS answer, but reachable on port 443 - port 53 appears blocked", "9.9.9.9": "Resolved"}))),
        s("Target", target, target_text, target_detail, meas(**{"Internet": "HTTP 204 (81 ms)" if target == "Ok" else "No connection", "Company portal": "HTTP 200 (142 ms)" if target == "Ok" else "No connection", "NAS": "1 ms"})),
    ]
def window(frm, to, samples, avg_d, avg_u, pk_d, pk_u, peak_at, cap_d=488.0, cap_u=47.5):
    return {"FromUtc": iso(frm), "ToUtc": iso(to), "Samples": samples, "AverageDownMbps": avg_d, "AverageUpMbps": avg_u, "PeakDownMbps": pk_d, "PeakUpMbps": pk_u,
            "PeakAtUtc": iso(peak_at), "AverageUtilization": avg_d / cap_d, "PeakUtilization": pk_d / cap_d}
def throughput(s, e, during_samples, d_avg=0.0, d_pk=0.0):
    return {"Baseline": window(s - datetime.timedelta(minutes=20), s - datetime.timedelta(minutes=5), 54, 3.1, 0.3, 61.4, 2.1, s - datetime.timedelta(minutes=12)),
            "Before": window(s - datetime.timedelta(minutes=5), s, 18, 1.9, 0.2, 12.7, 0.9, s - datetime.timedelta(minutes=3)),
            "During": window(s, e, during_samples, d_avg, 0.0, d_pk, 0.0, s + datetime.timedelta(seconds=5)),
            "After": window(e, e + datetime.timedelta(minutes=5), 18, 4.2, 0.4, 24.0, 1.3, e + datetime.timedelta(minutes=1)),
            "CapacityDownMbps": 488.0, "CapacityUpMbps": 47.5, "CapacitySource": "SpeedTest", "AfterPending": False, "HasAnyWindow": True}
def hop(ttl, addr=None, host=None, owner=None, ms=None):
    h = {"Ttl": ttl}
    if addr: h["Address"] = addr
    if host: h["Hostname"] = host
    if owner: h["Owner"] = owner
    if ms is not None: h["LatencyMs"] = ms
    return h
ISP = "AS1136  KPN B.V.   KPN-NL   NL"
def dur(s, e):
    d = e - s
    return f"{d.seconds // 3600:02d}:{(d.seconds // 60) % 60:02d}:{d.seconds % 60:02d}.0000000"

incidents = []
# 1. provider outage
s, e = windows[0][0], windows[0][1]
incidents.append({
    "Id": windows[0][3], "Uuid": str(uuid.uuid4()), "Cause": "Internet", "StartUtc": iso(s), "EndUtc": iso(e), "Status": "Resolved",
    "Headline": "No public address reachable",
    "Explanation": {"What": "The router answers, but nothing beyond it does: the connection to the provider is down.",
                    "LikelyCause": "None of the 3 public addresses replied while the router was reachable. The fault is between the router and the provider - the modem, the line, or the provider's network.",
                    "WhatToDo": "Check the modem's lights and restart it if it shows no connection. If this keeps happening, this report is what to send the provider: the outages, their timing and that this computer's own traffic was low."},
    "AffectedSteps": ["PublicIp", "Dns", "Target"],
    "Timeline": [{"AtUtc": iso(s), "Text": "Incident opened: No public address reachable"},
                 {"AtUtc": iso(s + datetime.timedelta(minutes=9, seconds=40)), "Text": "Public internet recovered: Reachable (8 ms)"},
                 {"AtUtc": iso(s + datetime.timedelta(minutes=9, seconds=44)), "Text": "DNS recovered: www.ripe.net resolved (19 ms)"},
                 {"AtUtc": iso(s + datetime.timedelta(minutes=9, seconds=44)), "Text": "Target degraded: 2 of 3 targets reachable"},
                 {"AtUtc": iso(s + datetime.timedelta(minutes=11, seconds=2)), "Text": "Public internet failed again: No public address reachable"},
                 {"AtUtc": iso(s + datetime.timedelta(minutes=13, seconds=58)), "Text": "Public internet recovered: Reachable (9 ms)"},
                 {"AtUtc": iso(e), "Text": "Resolved after 14 min 6 s"}],
    "StepsAtStart": steps(internet="Failed", dns="Skipped", target="Skipped", inet_text="No public address reachable", inet_detail="None of 9.9.9.9, 8.8.8.8 and 1.1.1.1 replied to a ping.", dns_text="Skipped", target_text="Skipped"),
    "StepsAtResolution": steps(),
    "RecentChanges": [],
    "Traceroute": {"Target": "9.9.9.9", "AtUtc": iso(s + datetime.timedelta(seconds=13)), "Reached": False,
                   "Hops": [hop(1, "192.168.1.1", "router.lan", None, 1.4), hop(2, "10.100.4.1", None, ISP, 6.2), hop(3), hop(4), hop(5), hop(6)]},
    "Throughput": throughput(s, e, 51), "Note": "Ticket 4711: the provider confirmed a fibre fault in the street cabinet.",
    "Duration": dur(s, e),
})
# 2. DNS silent but reachable
s, e = windows[1][0], windows[1][1]
incidents.append({
    "Id": windows[1][3], "Uuid": str(uuid.uuid4()), "Cause": "Dns", "StartUtc": iso(s), "EndUtc": iso(e), "Status": "Resolved",
    "Headline": "Your DNS server is not answering",
    "Explanation": {"What": "The DNS server this computer uses is reachable but gives no DNS answer, while the rest of the connection is fine.",
                    "LikelyCause": "Looking up www.ripe.net failed through this adapter's DNS server (1.1.1.1), which did accept a connection on port 443 at that moment; a public DNS server answered the same question without trouble. Something between this computer and that server drops DNS traffic (port 53), or the server's DNS service stalled.",
                    "WhatToDo": "Check the router's and the network's DNS filtering or security features, and whether this repeats at the same times of day. A second DNS server in the network settings gives Windows a fallback in the meantime."},
    "AffectedSteps": ["Dns"],
    "Timeline": [{"AtUtc": iso(s), "Text": "Incident opened: Your DNS server is not answering"},
                 {"AtUtc": iso(s + datetime.timedelta(seconds=16)), "Text": "DNS recovered: www.ripe.net resolved (31 ms)"},
                 {"AtUtc": iso(e), "Text": "Resolved after 20s"}],
    "StepsAtStart": steps(dns="Failed", dns_text="Your DNS server is not answering", dns_detail="This adapter's DNS server (1.1.1.1) gave no answer on port 53 but did accept a connection on port 443, while 9.9.9.9 resolved www.ripe.net. The server is reachable; DNS traffic to it is being dropped on the way, or its DNS service has stalled."),
    "StepsAtResolution": steps(),
    "RecentChanges": [],
    "Traceroute": {"Target": "1.1.1.1", "AtUtc": iso(s + datetime.timedelta(seconds=13)), "Reached": True,
                   "Hops": [hop(1, "192.168.1.1", "router.lan", None, 1.2), hop(2, "10.100.4.1", None, ISP, 5.9), hop(3, "145.145.0.77", "core1.ams.example.net", ISP, 7.1),
                            hop(4, "141.101.65.1", None, "AS13335  Cloudflare, Inc.   CLOUDFLARENET   US", 7.8), hop(5, "1.1.1.1", "one.one.one.one", "AS13335  Cloudflare, Inc.   CLOUDFLARENET   US", 8.0)]},
    "Throughput": throughput(s, e, 1, 1.2, 4.4), "Duration": dur(s, e),
})
# 3. gateway
s, e = windows[2][0], windows[2][1]
incidents.append({
    "Id": windows[2][3], "Uuid": str(uuid.uuid4()), "Cause": "Gateway", "StartUtc": iso(s), "EndUtc": iso(e), "Status": "Resolved",
    "Headline": "192.168.1.1 not reachable",
    "Explanation": {"What": "The network adapter has a valid address, but the router does not answer.",
                    "LikelyCause": "Your router did not answer a ping or a connection attempt. It may be restarting, or something between you and it is blocking traffic.",
                    "WhatToDo": "Check the router's lights and whether other devices can reach it. If it restarted by itself, note the time: a router that reboots on its own is worth replacing."},
    "AffectedSteps": ["Gateway", "PublicIp", "Dns", "Target"],
    "Timeline": [{"AtUtc": iso(s), "Text": "Incident opened: 192.168.1.1 not reachable"},
                 {"AtUtc": iso(e - datetime.timedelta(seconds=6)), "Text": "Gateway recovered: 192.168.1.1 reachable (3 ms)"},
                 {"AtUtc": iso(e - datetime.timedelta(seconds=3)), "Text": "Public internet recovered: Reachable (8 ms)"},
                 {"AtUtc": iso(e), "Text": "Resolved after 1 min 10 s"}],
    "StepsAtStart": steps(gateway="Failed", internet="Skipped", dns="Skipped", target="Skipped", gw_text="192.168.1.1 not reachable", gw_detail="Your router did not answer a ping or a connection attempt.", inet_text="Skipped", dns_text="Skipped", target_text="Skipped"),
    "StepsAtResolution": steps(), "RecentChanges": [f"{local(s)}  Link speed changed from 1201 to 866 Mbit/s"],
    "Throughput": throughput(s, e, 4), "Duration": dur(s, e),
})
# 4. weak Wi-Fi link
s, e = windows[3][0], windows[3][1]
incidents.append({
    "Id": windows[3][3], "Uuid": str(uuid.uuid4()), "Cause": "Link", "StartUtc": iso(s), "EndUtc": iso(e), "Status": "Resolved",
    "Headline": "Wi-Fi connection lost",
    "Explanation": {"What": "The Wi-Fi adapter is on, but it is not connected to a network.",
                    "LikelyCause": "The Wi-Fi signal was only 24% when this started. At that level packets are lost and the connection to the router drops out, so the failure is most likely caused by the weak signal rather than by the router or the provider.",
                    "WhatToDo": "Move closer to the access point or router, remove obstacles in between, or use a cable. If this keeps happening in one place, an extra access point or mesh node there helps."},
    "AffectedSteps": ["Link", "IpAddress", "Gateway", "PublicIp", "Dns", "Target"],
    "Timeline": [{"AtUtc": iso(s), "Text": "Incident opened: Wi-Fi connection lost"},
                 {"AtUtc": iso(e - datetime.timedelta(seconds=4)), "Text": "Cable / link recovered: Connected to Wi-Fi · 71% · 5 GHz"},
                 {"AtUtc": iso(e), "Text": "Resolved after 2 min 31 s"}],
    "StepsAtStart": steps(link="Failed", ip="Skipped", gateway="Skipped", internet="Skipped", dns="Skipped", target="Skipped", link_text="Wi-Fi connection lost", signal="24% (-84 dBm)", link_detail="The adapter is not associated with any network.", gw_text="Skipped", inet_text="Skipped", dns_text="Skipped", target_text="Skipped"),
    "StepsAtResolution": steps(link_text="Connected to Wi-Fi · 71% · 5 GHz", signal="71% (-62 dBm)"),
    "RecentChanges": [f"{(s - datetime.timedelta(seconds=45)).astimezone().strftime('%H:%M:%S')}  Wi-Fi signal dropped from 83% to 31%", f"{local(s)}  Link state changed from Operational to Disconnected"],
    "Throughput": throughput(s, e, 9), "Duration": dur(s, e),
})
# 5. connection moved to another adapter (resolved) + apipa
incidents.append({
    "Id": windows[4][3], "Uuid": str(uuid.uuid4()), "Cause": "NetworkChange", "StartUtc": iso(t_move), "EndUtc": iso(t_back), "Status": "Resolved",
    "Headline": "Connection moved from Ethernet (192.168.1.42 via 192.168.1.1) to Wi-Fi (192.168.1.57 via 192.168.1.1)",
    "Explanation": {"What": "Windows now sends this computer's traffic over Wi-Fi instead of Ethernet. Everything on Wi-Fi's network is reachable; devices that only exist on Ethernet's network are not.",
                    "LikelyCause": "The connection over Ethernet stopped working, or Wi-Fi got a better route: a cable or switch port that dropped, a DHCP server that stopped answering, or a changed metric. Windows does not always move back by itself once Ethernet works again.",
                    "WhatToDo": "Check what happened to Ethernet at this time: its link, its address and its DHCP server. Look at the incidents just before and after this one - an automatic-private-address incident on Ethernet says its DHCP server did not answer. The timeline shows when Ethernet was usable again and whether the traffic moved back."},
    "AffectedSteps": [],
    "Timeline": [{"AtUtc": iso(t_move), "Text": "Incident opened: Connection moved from Ethernet (192.168.1.42 via 192.168.1.1) to Wi-Fi (192.168.1.57 via 192.168.1.1)"},
                 {"AtUtc": iso(t_move + datetime.timedelta(minutes=21)), "Text": "Ethernet has an address again (192.168.1.42) but traffic still goes over Wi-Fi (192.168.1.57 via 192.168.1.1)"},
                 {"AtUtc": iso(t_back), "Text": "Back on Ethernet after 33 min"}],
    "StepsAtStart": [], "StepsAtResolution": [], "RecentChanges": [f"{local(t_move)}  Link state changed from Operational to Disconnected", f"{local(t_move)}  Active adapter changed from Ethernet to Wi-Fi"],
    "ConditionKey": "pathChanged", "Subject": "{eth}", "Throughput": throughput(t_move, t_back, 120, 2.2, 31.0), "Duration": dur(t_move, t_back),
})
incidents.append({
    "Id": ident(t_apipa), "Uuid": str(uuid.uuid4()), "Cause": "NetworkChange", "StartUtc": iso(t_apipa), "EndUtc": iso(t_move + datetime.timedelta(minutes=21)), "Status": "Resolved",
    "Headline": "Ethernet got an automatic private address 169.254.17.4: no DHCP server answered",
    "Explanation": {"What": "Ethernet asked for an address and no DHCP server replied, so Windows gave it an emergency address in the 169.254.x.x range. Nothing on that network can be reached through it, even though the link is up.",
                    "LikelyCause": f"The DHCP server on that network (usually the router) did not answer: it is down, the cable or switch port lost its path to it, or the port was moved to a network without DHCP. Until {local(t_move - datetime.timedelta(seconds=15))} this adapter had 192.168.1.42 from DHCP server 192.168.1.1.",
                    "WhatToDo": "Check the other end of Ethernet: the switch port, the cable and the router's DHCP service. The moment this opened is when the lease could not be renewed or the link came up without an answer - ask who changed something on that network then."},
    "AffectedSteps": [],
    "Timeline": [{"AtUtc": iso(t_apipa), "Text": "Incident opened: Ethernet got an automatic private address 169.254.17.4: no DHCP server answered"},
                 {"AtUtc": iso(t_move + datetime.timedelta(minutes=21)), "Text": "Ethernet has an address again: 192.168.1.42"}],
    "StepsAtStart": [], "StepsAtResolution": [], "RecentChanges": [], "ConditionKey": "apipa", "Subject": "{eth}", "Duration": dur(t_apipa, t_move + datetime.timedelta(minutes=21)),
})
# 6. router replaced (a moment)
incidents.append({
    "Id": ident(t_router), "Uuid": str(uuid.uuid4()), "Cause": "NetworkChange", "StartUtc": iso(t_router), "EndUtc": iso(t_router), "Status": "Resolved",
    "Headline": "The router at 192.168.1.1 now answers from hardware address 7c:10:c9:00:4e:21 (was 00:1d:aa:88:f2:10)",
    "Explanation": {"What": "The gateway address is the same, but a different device is answering for it.",
                    "LikelyCause": "A replaced router, a failover to a second router, or a device impersonating the router. A replacement you did yourself is the usual explanation; one you did not do is worth a look.",
                    "WhatToDo": "If nobody replaced or restarted the router around this time, check what device holds that hardware address (Tools → Network scan shows the manufacturer)."},
    "AffectedSteps": [], "Timeline": [{"AtUtc": iso(t_router), "Text": "Incident opened: The router at 192.168.1.1 now answers from hardware address 7c:10:c9:00:4e:21 (was 00:1d:aa:88:f2:10)"}],
    "StepsAtStart": [], "StepsAtResolution": [], "RecentChanges": [f"{local(t_router - datetime.timedelta(seconds=50))}  Gateway changed from none to 192.168.1.1"], "ConditionKey": "gatewayHardwareChanged", "Duration": "00:00:00",
})
# 7. packet loss warning
s, e = windows[5][0], windows[5][1]
incidents.append({
    "Id": windows[5][3], "Uuid": str(uuid.uuid4()), "Cause": "NetworkHealth", "StartUtc": iso(s), "EndUtc": iso(e), "Status": "Resolved",
    "Headline": "High packet loss: 20% in 5 minutes",
    "Explanation": {"What": "Pings to the public internet went unanswered far more often than normal, while the connection itself stayed up.",
                    "LikelyCause": "A line with errors, a saturated link, or a bad Wi-Fi spot. Loss while the connection stays up is what makes calls stutter and games lag.",
                    "WhatToDo": "Compare with the Wi-Fi signal and this computer's traffic at the time. If both were fine, the loss is on the line or at the provider."},
    "AffectedSteps": [], "Timeline": [{"AtUtc": iso(s), "Text": "Incident opened: High packet loss: 20% in 5 minutes"}, {"AtUtc": iso(e), "Text": "Resolved after 1 min"}],
    "StepsAtStart": [], "StepsAtResolution": [], "RecentChanges": [], "ConditionKey": "packetLoss", "Duration": dur(s, e),
})
incidents.sort(key=lambda i: i["StartUtc"])
io.open(os.path.join(out, "incidents.json"), "w", encoding="utf-8").write(json.dumps(incidents, indent=2))

settings = {
    "Language": args.language, "Theme": args.theme, "ShowIncidentPopups": False, "CheckForUpdates": False, "AutoUpdateInstall": False,
    "SubscribedDownloadMbps": 500, "SubscribedUploadMbps": 50, "ReportComparison": "PreviousPeriod", "ReportPeriodDays": 7,
    "SpeedTestAutoRunEnabled": True, "SpeedTestIntervalHours": 6, "HistorySeriesId": "dns",
    "ApplicationEndpoints": [
        {"Id": EP_PORTAL, "Name": "Company portal", "Kind": "Url", "Url": "https://portal.example.com/health", "Host": "", "Port": 443, "ExpectedStatusCode": 200, "Enabled": not args.no_endpoints, "Headers": []},
        {"Id": EP_NAS, "Name": "NAS", "Kind": "Ping", "Url": "", "Host": "192.168.1.20", "Port": 0, "ExpectedStatusCode": None, "Enabled": not args.no_endpoints, "Headers": []},
    ],
    "PacketLoss": {"Enabled": True, "MaxOccurrences": 2, "WindowMinutes": 5},
    "RogueDhcp": {"Enabled": True, "MaxOccurrences": 0, "WindowMinutes": 5},
}
io.open(os.path.join(out, "settings.json"), "w", encoding="utf-8").write(json.dumps(settings, indent=2))
print(out, {k: len(v) for k, v in rows.items()}, len(incidents), "incidents")
