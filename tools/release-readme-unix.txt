Detour for macOS and Linux (beta)
=================================

Start
-----
Unpack the archive anywhere in your home folder and run ./Detour from the Detour folder.
Settings are kept next to the program, so do not put it in a read-only place.

macOS: the app is not signed by Apple. On the first start macOS may say it can't be opened.
Run this once in Terminal (adjust the path), then start Detour again:

    xattr -dr com.apple.quarantine ~/Detour

Linux: TUN mode needs root rights for sing-box; Detour asks for the password when you switch it on.

What works
----------
- Subscriptions, servers, Xray and sing-box cores (bundled, no download needed)
- Connection rules (+vpn, +zapret, +zapret+vpn, +warp, +direct, +block) and presets
- zapret through tpws (by the zapret author): a local proxy, no admin rights, TCP only.
  Discord voice and other UDP go through the VPN with "zapret -> VPN" rules.
- Cloudflare WARP page, auto failover, diagnostics

Not yet on macOS/Linux
----------------------
- Kill switch (built on the Windows firewall)
- TG WS Proxy (Windows only)
- Automatic update: the Help page opens the release page instead

Detour is based on v2rayN (github.com/2dust/v2rayN) and is distributed under GPL-3.0.
zapret / tpws: github.com/bol-van/zapret (MIT).
