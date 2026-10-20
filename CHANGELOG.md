# Changelog

All notable changes to the MediaMTX Control Panel are listed here. The panel shows this file under
**Info → Changelog**, and the release workflow uses the section of a version as its release notes.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/). Write new entries under **Unreleased** in the sections
Added, Changed, Deprecated, Removed, Fixed or Security, and rename Unreleased to the new version
(with today's date) when you raise `<Version>` in `src/MediaMtxPanel/MediaMtxPanel.csproj`.

## [Unreleased]

### Added

- **Update now** in the Info window: administrators install an available update from the web interface. A backup to the backup location (optional), a local copy of the installation and an automatic roll-back when the new version does not start; steps and log are shown while it runs. Works on installations made with the Proxmox and Debian installers (they set up the updater with their next update), not in Docker.
- **Reverse proxy support**: Settings → Web server → *The panel is behind a reverse proxy* with the public https address. Web players, watch pages, embeds and stream links then load HLS and WebRTC through the panel (`/media/<server>/hls` and `/webrtc`) instead of the MediaMTX ports 8888/8889, so they work on https without extra ports. Per server the route can also be *Direct* or *Custom public URLs*. Trusted proxies can be set in the panel, and the page shows ready-made Caddy, nginx and Nginx Proxy Manager settings.
- The web player switches from WebRTC to HLS by itself when WebRTC cannot connect (for example when UDP 8189 is blocked).
- **Cloudflare support**: the panel reads the viewer's address from `CF-Connecting-IP` (only from Cloudflare addresses), tells Cloudflare and other proxies not to cache or buffer video (`Cache-Control: private, no-store`), and the server page warns when its public host points to Cloudflare's proxy, which does not carry RTSP, RTMP, SRT or WebRTC video. README section on setting up Cloudflare.
- After WebRTC failed once, the web player starts with HLS for 30 minutes instead of waiting for WebRTC again.
- The SSH setup of a server sets `hlsTrustedProxies` and `webrtcTrustedProxies` to the panel's address, so MediaMTX sees viewers' own addresses through the panel.

### Fixed

- The web player now switches to HLS when WebRTC cannot connect from outside the local network. MediaMTX answers the late ICE candidates of a failed connection with "not found", which the player took for a stream that is not live, so it kept retrying WebRTC without a picture. It now asks HLS whether the stream is live before it decides.

## [1.3.0] - 2026-10-20

### Added

- **Changelog under Info**: every version with its changes as a timeline, and a preview of what is new when an update is available.
- **Microsoft Entra ID single sign-on**: switch it on under Settings → Single sign-on (tenant, client ID, encrypted secret and a test button). Accounts are linked by e-mail address, can be created at first sign-in and can take their role from Entra app roles. No restart needed. (#13)
- **Monitoring and alerts**: a mail and/or webhook (JSON, Slack, Discord, Teams, optional HMAC signature) when a server cannot be reached or a live stream stops, and again when it is back. Current outages and recent alerts under Settings → Alerts. (#14)
- **Viewer and bandwidth statistics** per stream, collected every minute: charts over 1 hour to 90 days, totals per stream and a CSV download, in the panel and the customer portal. (#15)
- **Stream recordings**: play, download and delete recordings in the panel and the customer portal, with a retention period per stream. (#16)
- **Restreaming** to YouTube, Twitch, Facebook and custom RTMP(S) servers with live status, automatic start/stop and retries. Stream keys are stored encrypted. (#17)
- **Backup and restore** over SFTP, SCP, FTPS and FTP: full and incremental backups, optional AES-256-GCM encryption, a daily schedule with retention and restore from the remote location. (#18)

### Changed

- The Docker image and the LXC/Debian installers now include ffmpeg for restreaming. (#17)
- The MediaMTX playback server is switched on in the bundled configuration, the LXC installer and the SSH setup. (#16)
- The external login page no longer offers self-registration. (#13)

## [1.2.0] - 2026-10-18

### Added

- **Stable and Test release channels**, chosen under Settings → General or in the Info window. Stable follows finished releases, Test also prereleases. The installers' update mode installs the newest release of the chosen channel. (#12)
- A Release workflow that publishes test prereleases and stable releases on GitHub. (#12)
- **Build My TV Station contract** version 1: `/healthz`, a service token for `/api/v1`, a streams API, a configurable base path, trusted proxies and the `mediamtx-panel-station` helper. Every hook is off until it is configured. (#11)
- An automated test project that runs in CI. (#11)

### Changed

- The update check compares versions the Semantic Versioning way, so a test version counts as older than the finished version with the same number. (#12)

## [1.1.0] - 2026-10-17

### Added

- **Set up MediaMTX over SSH** when adding a server: the panel installs or finds MediaMTX, connects it to the panel's auth hook, enables the Control API and restarts it safely. SSH secrets are never stored. (#10)
- Step-by-step manual setup instructions on the server page. (#10)
- **Settings**: application name, default language, web server port and mail (SMTP with STARTTLS/TLS). (#9)
- **Languages**: English, Dutch, German and French. (#9)
- **Info button** with the current version, build and a check for updates. (#9)

### Changed

- A completely new, modern design with a grouped menu on the left, cards, tabs and icons for every page, including the login and account pages. (#9)

## [1.0.0] - 2026-10-17

### Added

- **First version** of the control panel (Blazor, .NET 10): streams on one or more MediaMTX servers, publish and play credentials, live status and user management.
- **Customer portal** with a live preview, server statistics and a share link per stream. (#2)
- **Paywall**: sell access to a stream through Mollie (iDEAL) or PayPal with tickets on a public watch page. (#2)
- **Proxmox LXC deploy** on Debian 13 with a single command, Standard and Advanced settings like the community scripts and a one-time administrator password. (#1, #3)
- **Standalone installer** for a Debian 13 VM or physical server, with or without a local MediaMTX. (#8)
- A step-by-step Proxmox guide in the README. (#7)

### Changed

- The whole project is now in English: panel, customer portal, watch pages, scripts and README. (#6)

### Fixed

- The Proxmox script picks the Debian template that matches the host's processor, so the container starts on x86 hosts. (#4)
- The installer runs in a clean environment inside the container with an English locale, so the .NET build no longer stops. (#5)

[Unreleased]: https://github.com/rhisijmuiden/Mediamtx-controlpanel/compare/v1.3.0...HEAD
[1.3.0]: https://github.com/rhisijmuiden/Mediamtx-controlpanel/commits/main
[1.2.0]: https://github.com/rhisijmuiden/Mediamtx-controlpanel/pull/12
[1.1.0]: https://github.com/rhisijmuiden/Mediamtx-controlpanel/pull/10
[1.0.0]: https://github.com/rhisijmuiden/Mediamtx-controlpanel/pull/8
