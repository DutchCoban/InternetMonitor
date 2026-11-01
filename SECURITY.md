# Security

## What this project is

Internet Monitor is written and maintained by one person, in their spare time, with no
organisation behind it. Reports are taken seriously and handled as well as that allows: there
are no response times to promise, and the project may stop being maintained at some point. Only
the newest release is looked at; older releases are not patched.

## Reporting a vulnerability

Use GitHub's private "Report a vulnerability" button in the repository's **Security** tab. That
keeps the report between you and the maintainer until a fix is out. Please do not open a public
issue for something that could be abused.

What helps: the version (Settings → Updates shows it), the steps to reproduce, and what the
impact is. A fix ships in the next release and is named in its release notes.

## Scope

In scope: the executable, the self-update mechanism, the exports, and importing a settings file.

Out of scope, by design (see the README's *Security and sovereignty* section): endpoints you
configure yourself are trusted and probed as given; the optional rogue-DHCP check shares UDP port
68 with Windows for a few seconds per poll; a secret placed in a monitored URL ends up in the
incident record like any other URL.

## What every release guarantees

- `InternetMonitor.exe.sig`: the executable is signed with the project's release key. The public
  key is built into the app (`src/InternetMonitor/Core/Updates/ReleaseSignature.cs`) and anyone
  can verify a download with `tools/ReleaseSigner verify`. The self-update installs nothing that
  is not signed with this key.
- A software bill of materials (`InternetMonitor-v<version>.cdx.json`, CycloneDX), listing the
  executable's hash and the .NET runtime it bundles. The app has no other dependencies.
- A VirusTotal verdict in the release notes, tied to the file's SHA-256.

## Legal position

The EU Cyber Resilience Act (Regulation (EU) 2024/2847) places its obligations on manufacturers
who make a product available on the market in the course of a commercial activity. Free and
open-source software that is not monetised falls outside that; a one-person hobby project is
neither a manufacturer nor an open-source steward in the regulation's sense. The signature and
the bill of materials above are good practice, not legal duties, and this document makes no
commitments beyond what is written here.
