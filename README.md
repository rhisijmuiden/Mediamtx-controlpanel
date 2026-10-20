# MediaMTX Panel

A control panel for [MediaMTX](https://github.com/bluenviron/mediamtx), written in ASP.NET Core (.NET 10, Blazor Server).

![Dashboard](docs/screenshots/dashboard.png)

- **Streams**: create, edit, enable/disable and delete. A stream is either a *publisher* stream (OBS, ffmpeg, a camera or a browser sends to the server) or a *pull* stream (the server pulls an external RTSP/RTMP/SRT/HLS/WHEP source). Options: public viewing, recording, maximum number of viewers.
- **Right after creating a stream** the panel shows the credentials (publish and view), the OBS settings, all endpoints (RTSP, RTMP, SRT, WebRTC WHIP/WHEP, HLS) with copy buttons, and example commands for ffmpeg/ffplay.
- **Recordings**: per stream, MediaMTX records everything that is broadcast and deletes it after a chosen number of days. Recordings can be played, downloaded as MP4 and deleted in the panel and in the customer portal (see [Recordings](#recordings)).
- **Live status**: the dashboard and stream pages show live/offline, codecs, viewers and traffic; publishers and viewers can be disconnected.
- **Restream**: forward a stream to YouTube, Twitch, Facebook or any RTMP(S) server while it is live. Stream keys are stored encrypted (see [Restream](#restream)).
- **Multiple MediaMTX servers**: administrators add as many MediaMTX instances as needed under *Servers*. Each stream is assigned to a server; its URLs use that server's public address and ports.
- **Server setup over SSH**: when adding a server, the administrator can enter its SSH login. The panel then logs in, installs MediaMTX if it is missing, points it at the panel, restarts it and adds the server as soon as the connection works (see [Adding a MediaMTX server](#adding-a-mediamtx-server)).
- **Users**: administrators create accounts (no open registration). Roles: *Administrator*, *User* (own streams only) and *Customer* (customer portal only).
- **Customer portal and paywall**: customers see their own streams with a live preview and statistics, and sell tickets with iDEAL (Mollie) or PayPal.
- **Embeddable player**: design a player per stream (colours, logo, poster, controls, shape) with a live preview, and paste one line of code on any website. Paywalled streams show a ticket screen in the player.
- **Tickets for viewers**: every ticket has a short code (`K7QM-4XPA-9TRC`), owners hand out free access codes, and viewers sign in to **My tickets** with a code by email (no password).
- **Settings**: application name, default language, web interface port and outgoing mail (any SMTP server).
- **Languages**: English (default), Dutch, German and French; everyone can pick their own language.
- **Info and updates**: the current version and build, a changelog of every version, with an update check against GitHub every 24 hours and on demand, on a **Stable** or **Test** release channel, and an **Update now** button that installs the update from the web interface.
- **Proxmox**: create an LXC container with the panel and MediaMTX using a single command in the Proxmox shell (see [Proxmox LXC](#proxmox-lxc)).

**Install on Proxmox VE** by pasting this into the Proxmox host shell (details in [Proxmox LXC](#proxmox-lxc)):

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/proxmox/create-lxc.sh)"
```

**Install on a Debian 13 machine** (bare metal or a VM) by running this as root (details in [Debian 13 standalone](#debian-13-standalone)):

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/debian/install.sh)"
```

## How it works

```
 encoder / viewer ──RTSP/RTMP/SRT/WebRTC/HLS──▶ MediaMTX ──(auth hook)──▶ Panel  ──▶ SQLite
                                                   ▲                         │
                                                   └──── Control API v3 ─────┘
```

- The panel is the source of truth. Streams are stored in the database (SQLite) and created as *paths* through the MediaMTX Control API (`/v3/config/paths/...`).
- MediaMTX runs with `authMethod: http`: for every publish/read/API request, MediaMTX asks the panel (`POST /api/mediamtx/auth`) whether it is allowed. Each stream has its own usernames and passwords; new passwords take effect immediately without restarting MediaMTX.
- MediaMTX keeps changes made through the API in memory only. The panel therefore checks every 30 seconds whether the configuration in MediaMTX is still correct and restores missing streams (e.g. after a MediaMTX restart).
- MediaMTX refuses paths that do not exist in the panel (`paths: {}` in `deploy/mediamtx.yml`).

## Using the panel

The screenshots below are taken automatically by the [build workflow](#screenshots-and-the-build-workflow) after every change on `main`, so they always show the current version.

### Menu

The menu on the left is grouped by purpose. Groups with more than one page fold open and closed; the group of the page you are on is open.

| Group | Pages | Who |
|---|---|---|
| **Overview** | Dashboard, Statistics, Reports (administrators), *Streams* (All streams, New stream), *Customer portal* (My streams, Payment settings) | Administrators and users; customers only see the customer portal |
| **Administration** | *Management* (Servers, Users), *Settings* (General, Web server, Mail, Single sign-on, Alerts) | Administrators |
| **Account** | *My account* (Profile, Password, Two-factor authentication, Passkeys) | Everyone |

At the bottom of the menu are your name (click it for your account, or use the button next to it to log out), the **language** menu and the **Info** button.

### Dashboard

Shows at a glance how many streams there are, how many are live, how many people are watching and whether every MediaMTX server is reachable. The data refreshes every few seconds. Click a server to open its settings, or a stream to open its details.

### Statistics

The panel stores the number of viewers and the traffic of every live stream once a minute. **Statistics** in the menu shows them for all your streams together: peak viewers, viewing hours, data sent and peak bandwidth, a chart of viewers (peak and average) and one of bandwidth (out to viewers and in from the source), and a table with the totals per stream. Choose the last hour, 24 hours, 7 days, 30 days or 90 days; hover over a chart to see the values at that moment, and use **CSV** to download the chart data. Every stream has the same statistics on its own **Statistics** tab, and customers see them for their streams in the customer portal. Statistics older than 90 days are deleted (see `MediaMtx__StatisticsRetentionDays`).

### Viewer report

**Reports** in the menu (administrators) shows how many people watched, with a history that goes back further than the statistics: every hour the panel rolls the statistics up into one row per stream per day, and those rows are kept after the detailed statistics are deleted (forever by default, see `MediaMtx__ReportRetentionDays`). Even the history of a deleted stream stays.

Pick a period (last 7 or 30 days, this or last month, last 12 months, this year, or your own dates), group it by day, week or month, and choose all streams or one stream. The report shows:

- **Peak viewers**: most viewers at the same time (for all streams: all streams together at that moment).
- **Unique viewers**: different people who watched, counted by IP address (or by ticket for paid streams). The panel only stores a keyed hash of the address, never the address itself. Viewers behind the same address or a proxy count as one.
- **Views**: how often someone started watching, and the **average view duration** (watch time per view). HLS viewers share one MediaMTX session, so they count as unique viewers but not as separate views.
- **Watch time**, hours live and data sent.

Below the chart are a table per period (with totals) and a table per stream. **Export** downloads the report as a PDF (A4, with key figures, chart and tables) or as CSV per period / per stream. Views and unique viewers are counted from this version onwards; peak viewers and watch time are also filled in for the statistics that were already there.

### Streams

![Streams](docs/screenshots/streams.png)

*All streams* lists every stream you may manage, with search and a server filter. *New stream* opens a form in sections, each with a short explanation: basics (path name, server, display name), source (publisher or pull), viewing (public or with a password, maximum viewers), options (enabled, recording) and owner.

![New stream](docs/screenshots/stream-new.png)

After saving you land on the stream page. Its tabs hold everything needed to broadcast and watch:

- **Credentials & OBS**: the OBS server and stream key, and the publish and view usernames and passwords, each with a copy button and a *Regenerate* button.
- **Endpoints**: every publish and watch URL (RTSP, RTMP, SRT, WebRTC, HLS) and example ffmpeg/ffplay commands.
- **Live status**: tracks, traffic and the connected publishers and viewers, who can be disconnected.
- **Restream**: the platforms this stream is forwarded to (see [Restream](#restream)).
- **Recordings**: what the stream recorded (see [Recordings](#recordings)).
- **Manage**: enable or disable, regenerate credentials and delete.

![Stream details](docs/screenshots/stream-details.png)

### Restream

On the *Restream* tab of a stream you add targets: **YouTube**, **Twitch**, **Facebook** or a **custom RTMP(S)** server (Kick, LinkedIn, a CDN, another MediaMTX…). For the platforms you only paste the stream key; the default ingest server is filled in (RTMPS), and you can enter another server URL if the platform gives you one.

- While the stream is live, the panel runs `ffmpeg` for every enabled target: it reads the stream from its MediaMTX server over RTSP (with the stream's viewer credentials, so it counts as one viewer) and pushes it to the target. It starts when the stream goes live, stops when it ends, and retries with a growing delay when the platform refuses the connection. Each target shows its state (waiting, connecting, live with bitrate, or the last error).
- The video is passed through unchanged by default (no CPU load). Turn on *Re-encode video to H.264* when the source is H.265, VP8 or AV1 (e.g. WebRTC publishers) or to send a lower bitrate. Audio is always sent as AAC, because the platforms require it.
- **Viewer counts**: give a YouTube, Twitch or Facebook target its viewer-count details and its card shows how many people watch there while it is live; the counts are also stored with the stream statistics (`PlatformViewers`). YouTube: a YouTube Data API v3 key and the channel ID (`UC…`); the live video is found once per broadcast, after that one quota unit per minute. Twitch: the client ID and secret of an application from the Twitch developer console and the channel name. Facebook: the page ID and a long-lived page access token with `pages_read_engagement` (only live videos on a page).
- Stream keys are encrypted with ASP.NET Data Protection (the keys in `Panel:DataProtectionKeysPath`), never shown again and kept out of the logs and error messages; the viewer-count credentials too.
- The panel server needs `ffmpeg` (`apt install ffmpeg`); the Proxmox/Debian installer and the Docker image include it. Use `Restream:FfmpegPath` for another location. Every target uses as much upload bandwidth on the panel server as the stream itself.

### Recordings

Switch on *Record on the server* in a stream's options and choose how many days MediaMTX keeps recordings (0 keeps them until they are deleted by hand). Every broadcast becomes a recording; a broadcast that stops and starts again becomes a new one.

*Streams → Recordings* (staff) and *Customer portal → Recordings* (customers, their own streams only) list the recordings per stream, with a player, an MP4 download and a delete button. The same list is on the stream page and in the customer portal's stream page.

The panel fetches recordings from the MediaMTX **playback server** (`playback: yes`, port 9996) of the server the stream runs on, and signs in with the stream's viewer credentials; the browser only talks to the panel. The bundled `deploy/mediamtx.yml` and the SSH setup switch playback on. For a server set up by hand, add `playback: yes` and `playbackAddress: :9996` to its mediamtx.yml and open port 9996 to the panel. When the playback server is not on the API host at port 9996, set *Playback URL* on the server's page. Recordings use MediaMTX's default fMP4 format; MPEG-TS recordings cannot be played back by MediaMTX.

### Servers

![Servers](docs/screenshots/servers.png)

Each MediaMTX server is a card with its status, version and number of streams. *Test* checks the connection; *Settings* opens the server page with its Control API address, public address and ports, statistics URL, the viewer route for HLS and WebRTC (see [Behind a reverse proxy (HTTPS)](#behind-a-reverse-proxy-https)) and the ready-to-paste `mediamtx.yml` lines for the authentication hook.

### Users

![Users](docs/screenshots/users.png)

The top of the page explains each role in a block with what that role can do and a button to add such an account. Below it every account is a card with its role, status, number of streams and buttons to edit, block or delete it. Filter by role or search by name and email. On the edit page each action (reset password, block, delete) is a separate block that says what it does.

### Settings

Only administrators see *Settings*. The settings are stored in `panel-settings.json` next to the database, so they survive updates.

- **General**: the application name (shown in the menu, browser tab and login page), the default language for visitors who have not chosen one, whether the panel checks for updates every 24 hours, and the release channel (see [Release channels](#release-channels)).

  ![General settings](docs/screenshots/settings-general.png)

- **Web server**: the reverse proxy switch and public address (see [Behind a reverse proxy (HTTPS)](#behind-a-reverse-proxy-https)), and the port of the web interface. Saving restarts the panel on the new port and the browser follows it. The port from the installation stays open on `127.0.0.1`, so a MediaMTX on the same machine keeps reaching the authentication hook. Other MediaMTX servers, a reverse proxy or a firewall must be updated to the new port. When the panel runs in Docker the port is set by the port mapping in `docker-compose.yml` instead. If the chosen port cannot be opened at startup, the panel falls back to the installation port and says so on this page.

  ![Web server settings](docs/screenshots/settings-web.png)

- **Mail**: the SMTP server for outgoing mail, such as password reset links. Any server works: plain, STARTTLS (automatic or required) or SSL/TLS, with or without a login, optionally accepting a self-signed certificate. The password is stored encrypted. *Send test mail* tries the settings before you save them. Once mail is on, the login page offers *Forgot your password?*.

  ![Mail settings](docs/screenshots/settings-mail.png)

- **Single sign-on**: log in with a Microsoft work account (Microsoft Entra ID), next to passwords and passkeys. See [Microsoft Entra SSO](#microsoft-entra-sso).

  ![Single sign-on settings](docs/screenshots/settings-sso.png)

- **Alerts**: monitoring. Every 15 seconds the panel checks each enabled MediaMTX server and its streams, and sends a mail (through the server under *Mail*) and/or a webhook when a server cannot be reached or a live stream stops, and again when it is back. A grace time (default 60 seconds) filters short hiccups. A stream is only watched after it was live once, on-demand pull streams are skipped, and single streams can be muted. Webhook formats: plain JSON (`event` is `server.down`, `server.up`, `stream.down`, `stream.up` or `test`), Slack/Mattermost, Discord and Microsoft Teams (workflow webhook). With a signing secret every request carries `X-Panel-Signature: sha256=<HMAC-SHA256 of the body>`. Messages are written in the default language. The page also shows what is offline right now and the recent alerts.

  ![Alert settings](docs/screenshots/settings-alerts.png)

- **Backup & restore**: backups of the database, the settings and the encryption keys to another machine over **SFTP**, **SCP**, **FTPS** (explicit or implicit), **FTP** or a local folder (such as a mounted NAS share). *Test connection* checks the login and that the folder is writable.
  - **Full and incremental**: a full backup holds everything; an incremental backup only holds the 64 KB pieces that changed since the previous backup of the same full backup. The schedule makes a backup every day at a chosen time, a full one on a chosen weekday (or every day) and incremental ones on the other days, keeps the last *n* full backups with their incremental ones, and can mail the administrators when a scheduled backup fails.
  - **Encryption**: with a passphrase every archive is encrypted with AES-256-GCM (key from PBKDF2-SHA256) before it leaves the machine. Keep the passphrase outside the panel: without it the backups cannot be restored. Passwords, keys and the passphrase are stored encrypted in `panel-settings.json`. An SSH host key is trusted at the first connection and checked after that.
  - **Restore**: the page lists the backups at the remote location. *Restore* downloads the backup with the archives it builds on, checks every checksum and the database, and restarts the panel, which puts the data in place before it opens the database. The previous data is kept in `pre-restore/` next to the database. The backup location, schedule and web port of the running installation stay as they are, so a fresh installation can restore from the same location. A backup made by a newer version is refused until the panel is updated.
  - Archives are named `mediamtx-panel_<time>_full.tar` and `mediamtx-panel_<time>_incr_<time of its full backup>.tar`; an unencrypted archive is a plain tar file with a `manifest.json`.

  ![Backup settings](docs/screenshots/settings-backup.png)

### Languages

The panel is available in English, Dutch, German and French. English is the default; administrators choose another default under *Settings → General*, and everyone can pick their own language in the menu at the bottom left (stored in a cookie). The public watch pages have the same language menu.

![Dashboard in Dutch](docs/screenshots/dashboard-nl.png)

### Info and updates

![Info](docs/screenshots/info.png)

The **Info** button at the bottom left shows the version, the build (git commit) and the build date. The panel checks GitHub for a newer version when it starts and then every 24 hours (switch this off under *Settings → General*); administrators can also check by hand with *Check for updates*. The check follows the [release channel](#release-channels); while that channel has no release yet, the panel compares its build with the newest commit on `main`, which is what the installers then build. A dot on the Info button means an update is available. Nothing is installed automatically.

**Update now** (administrators, on installations made with the Proxmox or Debian installer) installs the update from the Info window. After a confirmation the panel optionally makes a backup to the [backup location](#backup) first, then asks the updater to install the newest release of the release channel. The updater runs as a separate systemd service with root rights (`mediamtx-panel-update.service`, started by `mediamtx-panel-update.path` when the panel creates `/var/lib/mediamtx-panel/update-request`; the panel itself keeps running without root rights). It saves a copy of the application, database, settings and configuration in `/var/backups/mediamtx-panel/` (the newest three are kept), runs the installer's `update` and restarts the panel; if the new version does not start, it puts the copy back. The Info window shows the steps and the log while it runs and the result afterwards; the log is also in `/var/lib/mediamtx-panel-update/update.log`. Streams keep running; the panel is unavailable for a few minutes. Installations from before this feature get the updater with their next update from the command line. In Docker the Info window shows how to update by hand instead.

The **Changelog** tab shows every version with what was added, changed and fixed, as a timeline. When an update is available, the Info window also lists what the newer version brings (read from `CHANGELOG.md` of that release on GitHub), and the Changelog tab shows those versions on top.

![Changelog](docs/screenshots/changelog.png)

The changelog lives in [`CHANGELOG.md`](CHANGELOG.md) ([Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format) and is built into the panel. Add each change under *Unreleased*; when you raise `<Version>` in `MediaMtxPanel.csproj`, rename *Unreleased* to that version (a test fails when the version has no section). The Release workflow uses the version's section as the release notes.

### Release channels

Administrators choose a release channel under *Settings → General* or in the Info window:

| Channel | Gets | For |
|---|---|---|
| **Stable** (default) | finished GitHub releases only | production |
| **Test** | prereleases as well (`v1.3.0-test.1`), plus every stable release | trying new versions early |

The channel decides what the update check reports and what the installers install: the `update` command of the Proxmox and Debian installers reads the channel from the panel (`panel-settings.json`) and builds the newest release of that channel. Set `RELEASE_CHANNEL=test` (or `stable`) in front of the command to override it, for example on a first install; set `REPO_BRANCH` to build a branch instead of a release. Switching from Test back to Stable does not downgrade: the panel waits until a stable release is newer than the test version it runs.

Releases are published with the **Release** workflow (*Actions → Release → Run workflow*):

- **test** tags the chosen commit as `v<Version>-test.<n>` and publishes it as a prerelease. When `v<Version>` was already released, the next patch number is used, so a test version is always newer than the last stable release.
- **stable** tags it as `v<Version>` (from `<Version>` in `src/MediaMtxPanel/MediaMtxPanel.csproj`) and publishes a normal release. Raise `<Version>` before each stable release.

Pushing a tag by hand works too: a tag with a hyphen (`v1.4.0-beta.1`) becomes a prerelease, any other `v*` tag a stable release.

## Microsoft Entra SSO

The login page can offer *Sign in with Microsoft* for the people in your Microsoft 365 / Entra ID organisation. It is off until an administrator turns it on under *Settings → Single sign-on*; no restart is needed.

1. In the [Entra admin center](https://entra.microsoft.com) go to *App registrations → New registration*. Pick *Accounts in this organizational directory only* and add the **Redirect URI** shown on the settings page as a *Web* platform (for example `https://panel.example.com/signin-entra`). Behind a reverse proxy, use the public address and set `Panel:TrustedProxies` so the panel sees `https`.
2. Under *Certificates & secrets* create a client secret and copy its **Value**.
3. On the settings page enter the **Directory (tenant) ID**, the **Application (client) ID** and the secret, then click *Test settings*. The panel checks the tenant and gets a token with the ID and secret. The secret is stored encrypted.
4. Switch on *Allow signing in with Microsoft* and save.

Who gets in:

- Someone signing in for the first time gets the panel account with the same e-mail address, and the two are linked from then on.
- Without such an account they are refused, unless *Create an account at the first sign-in* is on; new accounts get the role chosen there.
- Optional: create **app roles** with the values `Admin`, `User` and `Customer` in the app registration and assign them to users or groups. The role in the token then sets the panel role at every sign-in.
- Locked users stay locked. The panel's own two-factor step is skipped for Microsoft logins; require MFA with Entra Conditional Access instead.
- Only a single tenant is accepted (`common` and `organizations` are refused), so accounts from other organisations cannot sign in.

## Running with Docker

```bash
cp .env.example .env      # fill in PUBLIC_HOST and the passwords
docker compose up -d --build
```

To show the exact build under *Info*, build with `GIT_COMMIT=$(git rev-parse HEAD) docker compose up -d --build`.

Then open `http://<server>:8080` and log in with `ADMIN_EMAIL` / `ADMIN_PASSWORD`. If you leave `ADMIN_PASSWORD` empty, the panel generates a password and writes it to the log once (`docker compose logs panel`).

MediaMTX ports: 8554 (RTSP), 1935 (RTMP), 8890/udp (SRT), 8888 (HLS), 8889 + 8189/udp (WebRTC). The Control API (9997) is only reachable inside the docker network.

In production, put the panel behind a reverse proxy with HTTPS; see [Behind a reverse proxy (HTTPS)](#behind-a-reverse-proxy-https).

## Behind a reverse proxy (HTTPS)

Web players on an `https://` page cannot load `http://host:8889` or `http://host:8888`, and a reverse proxy usually only forwards 443. Turn on **Settings → Web server → The panel is behind a reverse proxy** and enter the public address (for example `https://stream.example.com`). From then on the panel forwards HLS and WebRTC itself, so viewers only need that address:

| | Without reverse proxy | Behind a reverse proxy |
|---|---|---|
| WebRTC (WHEP, `reader.js`, WHIP) | `http://host:8889/<stream>/whep` | `https://stream.example.com/media/<server>/webrtc/<stream>/whep` |
| HLS | `http://host:8888/<stream>/index.m3u8` | `https://stream.example.com/media/<server>/hls/<stream>/index.m3u8` |

This applies to the watch page, the embedded player, the customer portal and the links shown per stream. RTSP, RTMP and SRT are not HTTP and keep their ports. Per server (under **Servers → Settings → Viewer connections**) the route can also be set to *Directly to the MediaMTX ports* or to *Custom public URLs*, when your proxy already publishes MediaMTX under its own URLs (for example `https://stream.example.com/hls` → port 8888). The default, *Automatic*, follows the switch under Settings.

The panel reaches HLS and WebRTC over plain HTTP on the host of the server's API URL. When MediaMTX runs on another machine, the panel must be able to reach ports 8888 and 8889 there. Set `hlsTrustedProxies` and `webrtcTrustedProxies` in `mediamtx.yml` to the panel's IP address so MediaMTX sees the viewers' own addresses (the SSH setup on the server page does this when the hook URL contains an IP address).

**WebRTC media does not go through the proxy.** Only the signalling (offer/answer) does; the video itself flows over port 8189 directly to MediaMTX. Forward UDP 8189 (and TCP 8189 if you set `webrtcLocalTCPAddress: :8189` for networks that block UDP) to the MediaMTX server and put its public address or host name in `webrtcAdditionalHosts`. When WebRTC cannot connect, the web player switches to HLS by itself after about 12 seconds, so viewers still get a picture.

Optionally set **Trusted proxies** to the IP address of the proxy; then only that address may pass `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` (applied after a restart). The **Check** line on the same page shows how the panel sees the request: behind a working proxy it starts with `https://` and shows the public host.

Forward everything (including WebSockets, which the admin pages use) to the panel on port 8080. Turn off response buffering so live HLS is not delayed.

**Caddy**

```caddy
stream.example.com {
    reverse_proxy 10.0.0.10:8080 {
        flush_interval -1
    }
}
```

**nginx**

```nginx
map $http_upgrade $connection_upgrade { default upgrade; '' close; }

server {
    listen 443 ssl;
    http2 on;
    server_name stream.example.com;
    ssl_certificate     /etc/letsencrypt/live/stream.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/stream.example.com/privkey.pem;

    location / {
        proxy_pass http://10.0.0.10:8080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host $host;
        proxy_buffering off;
        proxy_read_timeout 120s;
        client_max_body_size 50m;
    }
}
```

**Nginx Proxy Manager**: add a *Proxy Host* for `stream.example.com`, scheme `http`, forward to the panel's IP and port `8080`, switch on *Websockets Support* and *Block Common Exploits*, and request a certificate on the *SSL* tab (*Force SSL*, *HTTP/2 Support*). On the *Advanced* tab add `proxy_buffering off;`.

## Proxmox LXC

The script `deploy/proxmox/create-lxc.sh` creates a Debian 13 container on a Proxmox VE host. It installs the panel as a systemd service and, by default, MediaMTX as the first server in the same container.

### Requirements

- Proxmox VE 8.4 or newer, because the Debian 13 template needs it. Both amd64 and arm64 hosts work.
- Root access to the Proxmox host shell.
- Internet access from the host and from the container. The container downloads .NET 10 from Microsoft's package feed (`packages.microsoft.com`) and MediaMTX from GitHub.
- At least 1536 MB of memory for the container, because the panel is built inside it. The default is 2048 MB.

### Install

1. In the Proxmox web UI, open **Datacenter → *your node* → Shell**, or log in to the host over SSH as root.
2. Paste this command:

   ```bash
   bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/proxmox/create-lxc.sh)"
   ```

3. Choose how the container should be created. As with the [community-scripts.org](https://community-scripts.org) scripts, there are two options:
   - **Default**: 2 cores, 2048 MB memory, 8 GB disk, DHCP on `vmbr0`.
   - **Advanced**: you choose the container ID, hostname, cores, memory, swap, disk size, storage (picked from the storage on your host), network bridge, static IP or DHCP, gateway, VLAN, DNS, root password, SSH key, whether MediaMTX is installed in the container too, the panel port, the administrator's email address and the public address for the streams.
4. Check the summary and confirm. The script then downloads the Debian 13 template, creates and starts the container, and builds and installs the panel. This takes a few minutes.
5. At the end the script prints the details you need:

   ```
   [✓] MediaMTX Panel is installed in container 125 (Debian 13)

     ┌──────────────────────────────────────────────────────────────
     │  Address:      http://192.168.1.50:8080
     │  Login:        admin@localhost
     │  Password:     <one-time password>
     │
     │  This is a one-time password: after your first login
     │  the panel immediately asks you to choose your own password.
     └──────────────────────────────────────────────────────────────

     Root password of the container: <random password>
   ```

   These passwords are not stored anywhere and are not shown again, so copy them now.

6. Open the address in your browser and log in. The panel immediately asks you to choose a new password.

### After installing

- **Public address**: stream URLs use the container's IP by default. If viewers or encoders connect through a domain name or a NAT address, change it under **Servers** in the panel, or install with `PUBLIC_HOST=…`.
- **Firewall and port forwarding**: open the MediaMTX ports you use: 8554 (RTSP), 1935 (RTMP), 8890/udp (SRT), 8888 (HLS), 8889 and 8189/udp (WebRTC), plus the panel port (8080).
- **HTTPS**: for use on the internet, put the panel behind a reverse proxy with HTTPS and switch on **Settings → Web server → The panel is behind a reverse proxy**; see [Behind a reverse proxy (HTTPS)](#behind-a-reverse-proxy-https).
- **Root password**: if you shared the container's root password anywhere, change it with `pct exec <CTID> -- passwd`.
- **More servers**: other containers or VMs that run only MediaMTX can be added under **Servers** in the panel, optionally set up over SSH (see [Adding a MediaMTX server](#adding-a-mediamtx-server)).

Inside the container:

| | Panel | MediaMTX |
|---|---|---|
| Program | `/opt/mediamtx-panel` | `/opt/mediamtx` |
| Configuration | `/etc/mediamtx-panel/panel.env` | `/etc/mediamtx/mediamtx.yml` |
| Data | `/var/lib/mediamtx-panel` | `/var/lib/mediamtx/recordings` |
| Service | `mediamtx-panel` | `mediamtx` |

Useful commands on the Proxmox host:

```bash
pct exec <CTID> -- journalctl -u mediamtx-panel -f      # panel logs
pct exec <CTID> -- journalctl -u mediamtx -f            # MediaMTX logs
pct exec <CTID> -- systemctl restart mediamtx-panel     # restart after editing panel.env
```

### Updating

Run the same script with `update` and the container ID. The newest release of the panel's [release channel](#release-channels) is built and installed (the latest code on `main` while there are no releases). The database and settings are kept.

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/proxmox/create-lxc.sh)" _ update <CTID>
```

### Unattended install

To run without questions, for example from automation, set `INTERACTIVE=no` and pass the settings as environment variables:

```bash
INTERACTIVE=no CT_ID=150 CT_IP=192.168.1.50/24 CT_GW=192.168.1.1 CT_STORAGE=local-zfs \
PUBLIC_HOST=stream.example.com ADMIN_EMAIL=me@example.com \
bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/proxmox/create-lxc.sh)"
```

| Variable | Default | Meaning |
|---|---|---|
| `INTERACTIVE` | `yes` | `no` = ask nothing, use only these variables |
| `TEMPLATE_NAME` | `debian-13-standard` | Specific Debian 13 template |
| `CT_ID` | next free ID | Container ID |
| `CT_HOSTNAME` | `mediamtx-panel` | Hostname |
| `CT_CORES` / `CT_MEMORY` / `CT_SWAP` / `CT_DISK` | 2 / 2048 MB / 512 MB / 8 GB | Resources (building needs ~1.5 GB memory) |
| `CT_STORAGE` / `TEMPLATE_STORAGE` | `local-lvm` / `local` | Storage for the disk and for the template |
| `CT_BRIDGE` / `CT_VLAN` | `vmbr0` / empty | Network |
| `CT_IP` / `CT_GW` / `CT_DNS` | `dhcp` / empty / empty | IP settings |
| `CT_PASSWORD` / `CT_SSH_KEY` | random / empty | Root access to the container (`CT_SSH_KEY` is a path to a public key file) |
| `INSTALL_MEDIAMTX` | `yes` | Install MediaMTX in the same container |
| `MEDIAMTX_VERSION` | `1.21.2` | MediaMTX version (from GitHub releases) |
| `PANEL_PORT` | `8080` | Panel port |
| `ADMIN_EMAIL` / `ADMIN_PASSWORD` | `admin@localhost` / random | First administrator (the password is one-time) |
| `REPO` / `REPO_BRANCH` | `rhisijmuiden/Mediamtx-controlpanel` / `main` | Where the source code comes from |
| `PUBLIC_HOST` | IP of the container | Address in the stream URLs |

The script downloads the source code from GitHub itself. Running `bash deploy/proxmox/create-lxc.sh` from an extracted copy of this repository also works, and then uses that copy.

### Troubleshooting

- **"Template … not found"**: the host is older than Proxmox VE 8.4, or `pveam update` cannot reach the internet.
- **"Container … could not start and has been removed again"**: see `journalctl -u pve-container@<CTID>` on the host for the cause.
- **"Container … has no working network/DNS"**: check the bridge, IP, gateway and DNS settings, or try DHCP.
- **The panel does not open**: check that the service runs with `pct exec <CTID> -- systemctl status mediamtx-panel`, and read the logs with the command above.

## Debian 13 standalone

The script `deploy/debian/install.sh` installs the panel directly on a machine running Debian 13 (trixie), such as a bare-metal server or a VM, without Proxmox. It installs everything it needs itself (.NET 10 from Microsoft's package feed, and curl and whiptail when they are missing) and sets up the same systemd services as the Proxmox container. During the installation you choose whether a local MediaMTX is installed too, or only the control panel.

### Requirements

- Debian 13 with systemd, amd64 or arm64. Other distributions and Debian versions are refused.
- Root access (`sudo -i` or `su -`).
- Internet access to `packages.microsoft.com` (.NET 10), `github.com` (sources and MediaMTX) and the Debian mirrors.
- About 1.5 GB of memory plus swap, because the panel is built on the machine. The script warns when there is less.
- Free ports: the panel port (8080), and with a local MediaMTX also 8554, 1935, 8890/udp, 8888, 8889, 8189/udp and 9997. The script checks this before it installs anything.

### Install

1. Log in to the machine and become root.
2. Run:

   ```bash
   bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/debian/install.sh)"
   ```

   On a minimal Debian without curl, use wget instead:

   ```bash
   bash -c "$(wget -qO- https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/debian/install.sh)"
   ```

3. Choose what to install:
   - **Panel + MediaMTX**: streams are served from this machine, and this MediaMTX is added to the panel as the first server.
   - **Panel only**: no local MediaMTX. Afterwards you add MediaMTX servers that run elsewhere under **Servers** in the panel, which shows the `mediamtx.yml` settings each server needs.
4. Choose **Default** settings, or **Advanced** to set the panel port, the administrator's email address, the address of this machine and (with MediaMTX) the public stream address and MediaMTX version.
5. Check the summary and confirm. At the end the script prints the panel address, the login and a one-time password, just like the Proxmox script. The panel asks you to choose your own password at the first login.

The program, configuration, data and service locations are the same as in the Proxmox container (see the table under [After installing](#after-installing)). Logs: `journalctl -u mediamtx-panel -f` and `journalctl -u mediamtx -f`.

### Unattended install

Options and environment variables skip the questions:

```bash
# Only the control panel, no questions
bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/debian/install.sh)" _ --panel-only --yes

# Panel + MediaMTX with a public host name
PUBLIC_HOST=stream.example.com ADMIN_EMAIL=me@example.com \
bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/debian/install.sh)" _ --yes
```

| Option / variable | Default | Meaning |
|---|---|---|
| `--panel-only` / `INSTALL_MEDIAMTX=no` | MediaMTX is installed | Install only the control panel |
| `--yes` / `INTERACTIVE=no` | ask | Ask nothing, use the defaults and variables |
| `PANEL_PORT` | `8080` | Panel port |
| `ADMIN_EMAIL` / `ADMIN_PASSWORD` | `admin@localhost` / random | First administrator (the password is one-time) |
| `PANEL_HOST` | first IP of the machine | Address at which browsers and MediaMTX servers reach the panel |
| `PUBLIC_HOST` | `PANEL_HOST` | Address in the stream URLs of the local MediaMTX |
| `MEDIAMTX_VERSION` | `1.21.2` | MediaMTX version (from GitHub releases) |
| `REPO` / `REPO_BRANCH` | `rhisijmuiden/Mediamtx-controlpanel` / `main` | Where the source code comes from |

Running `bash deploy/debian/install.sh` from a copy of this repository also works, and then uses that copy.

### Updating

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/debian/install.sh)" _ update
```

The newest release of the panel's [release channel](#release-channels) is built and installed (the latest code on `main` while there are no releases); the database and settings are kept. Running the installer again without `update` on a machine that already has the panel is refused, so the existing configuration is never overwritten.

### Removing

```bash
systemctl disable --now mediamtx-panel mediamtx
rm -f /etc/systemd/system/mediamtx-panel.service /etc/systemd/system/mediamtx.service && systemctl daemon-reload
rm -rf /opt/mediamtx-panel /etc/mediamtx-panel /var/lib/mediamtx-panel /opt/mediamtx /etc/mediamtx /var/lib/mediamtx
userdel mediamtx-panel; userdel mediamtx
```

This deletes the database and recordings too.

## Adding a MediaMTX server

Under **Servers → Add server** you fill in the server's API URL and public address. Then there are two ways to prepare MediaMTX on that server:

- **Over SSH**: turn on *Configure MediaMTX on the server over SSH* and enter the SSH host, user and password (or a private key). The user must be root or allowed to use sudo. The panel:
  1. finds MediaMTX (a systemd service, a Docker container with a mounted `mediamtx.yml`, or the usual install locations) and installs MediaMTX v1.21.2 as `mediamtx.service` if it is missing (turn this off if you prefer to install it yourself);
  2. checks that the server can reach the panel at the *panel address*; if not, it stops without changing anything;
  3. sets `authMethod: http`, `authHTTPAddress` (with this server's secret), `authHTTPExclude: []`, `api: yes` and `apiAddress` in `mediamtx.yml`, keeping a backup next to it (`mediamtx.yml.bak-<date>`), and restarts MediaMTX. If MediaMTX does not start with the new file, the backup is put back;
  4. connects to the Control API and adds the server. If nothing could be changed on the server, the server is not added and the page shows why.

  The SSH password or key is only used for that run and is not saved. On an existing server's page, *Configure over SSH* runs the same steps again, for example after *New secret*.
- **By hand**: the page shows the `mediamtx.yml` settings for this server and the steps to apply them. MediaMTX v1.16 or newer is needed.

Either way, the server must reach the panel's address, and the panel must reach the server's API port (9997 by default). Open that port to the panel only.

## Running as part of Build My TV Station

[Build My TV Station](https://github.com/rhisijmuiden/BulldMyTVStation) can deploy the panel as one part of a station. For that the panel implements the [station contract (version 1)](https://github.com/rhisijmuiden/BulldMyTVStation/blob/main/docs/station-contract.md). Everything in it is off until it is set, so a standalone install behaves exactly as before.

- `GET /healthz` answers without a login: `{"status": "ok", "app": "mediamtx-panel", "version": "…", "contract": 1}`, at the root and under the base path.
- `Panel__ServiceToken` (at least 32 characters) turns on `Authorization: Bearer <token>` for `/api/v1/` only, with administrator rights: `GET`/`POST /api/v1/streams` (`{"name": "channel1", "publicRead": true}`) and `GET`/`DELETE /api/v1/streams/{id}`, on the first media server. Each stream comes with its publish and play URLs. Without a token `/api/v1/` answers 401 to everyone.
- `Panel__PathBase` (e.g. `/channel1`) serves the panel under that path; the reverse proxy forwards the full path unchanged. `/healthz` and the MediaMTX authentication hook keep working at the root.
- `Panel__TrustedProxies` (comma-separated IPs or CIDRs) makes the panel believe `X-Forwarded-For`, `-Proto` and `-Host` only from those addresses.

The installers add the command `mediamtx-panel-station [--reset-admin]`, which the station runs as root. It writes `STATION_SERVICE_TOKEN`, `STATION_BASE_PATH`, `STATION_PUBLIC_URL` (→ `Panel__PublicUrl`) and `STATION_TRUSTED_PROXIES` into `/etc/mediamtx-panel/panel.env` (a variable that is not set is left alone, an empty one is removed), with `--reset-admin` gives the administrator a new one-time password, restarts the panel when something changed, waits for `/healthz` and writes `/var/lib/mediamtx-panel/station.json`. It prints the same JSON on stdout (with `initialPassword` after `--reset-admin`); progress goes to stderr.

## Customer portal and paywall

![Customer portal](docs/screenshots/customer-stream.png)

Under **Users**, create an account with the *Customer* role and select that customer as *Owner* of a stream. After logging in, the customer lands in **My portal** (`/portal`):

- **Preview**: the stream plays in the browser (WebRTC, or HLS if the browser cannot play the codec over WebRTC), so the customer can see whether the video is coming in properly.
- **Statistics bar**: CPU, RAM, load and network traffic of the server, plus bitrate in/out, viewers, codecs and how long the stream has been live. If MediaMTX runs on the same machine as the panel (API URL on `127.0.0.1`), the panel reads this itself. For other servers, install `prometheus-node-exporter` on that server and fill in the *Statistics URL* under **Servers** (e.g. `http://10.0.0.21:9100/metrics`).
- **Connect tab**: OBS server and stream key, username and password, and the URLs for RTSP, RTMP, SRT and WHIP.
- **Share tab**: the public watch page `/watch/<code>` and the viewing credentials.
- **Paywall tab**: manage offers (name, price, access duration, such as "Day ticket HD" or "Week pass"), create free access codes, turn the paywall on and off, and view sales and revenue.
- **Embed player**: the designer described under [Embedding the player](#embedding-the-player).

### Payments

Under **Payment settings** the customer connects their own account; the money goes directly to the customer. Keys are stored encrypted.

- **iDEAL via Mollie**: API key from the Mollie dashboard (`test_…` to try it out, `live_…` for real payments).
- **PayPal**: Client ID and Secret of a REST app on developer.paypal.com, with a switch for sandbox or live.

The viewer opens the watch page, picks an offer and pays. The provider then sends the viewer back and the stream starts playing immediately. The viewer gets a personal link and can also watch via VLC, a smart TV or another device: they use the username `ticket` with the ticket code as the password (RTSP, HLS, SRT). Once the purchased access duration has passed, the ticket stops working. Viewing without a ticket is refused, even if the stream is set to *public*.

![Watch page](docs/screenshots/watch-page.png)

For payments, the panel must be reachable at a public address. Set that address in `Panel__PublicUrl` (e.g. `https://panel.example.com`); the payment providers use it to send the viewer back, and Mollie sends the payment status there (`/api/payments/mollie/webhook`).

With `Panel__EnableTestPayments=true` a *Test payment* button appears that lets you go through the whole flow without real money. Turn it off in production.

### Ticket codes and access codes

Every ticket gets a short code such as `K7QM-4XPA-9TRC`. Viewers type it on the watch page, under **My tickets** or in the embedded player, on any device. Under **Paywall → Access codes** an owner creates free codes (for the press, sponsors or a guest list): choose how many, how many hours of access each gives, an optional label and an optional *use before* date. The access time starts at the first use. Codes can be revoked at any time. Access codes alone are enough to turn the paywall on; offers and a payment account are only needed to sell tickets.

### My tickets (viewer sign-in)

Viewers enter their email address when they buy a ticket. At `/tickets` (**My tickets** in the top bar of the watch page) they sign in without a password: the panel mails a 6-digit code (valid for 15 minutes) to the address they paid with, and then shows every stream they have a valid ticket for, with the ticket codes and a *Watch* button. On the watch page a signed-in viewer is let in without the ticket link. Viewer accounts are separate from panel accounts (own cookie, 30 days) and never get access to the panel. Sign-in needs a working mail server under **Settings → Mail**; codes are only sent to addresses that have tickets.

## Embedding the player

![Embed designer](docs/screenshots/embed-designer.png)

Open a stream and click **Embed player** (also in the customer portal). The designer shows a live preview of the player next to its settings:

- **Appearance**: accent colour, background, rounded corners, shape (16:9, 4:3, 21:9, 1:1, 9:16) and a responsive or fixed size.
- **Playback**: start automatically (browsers only allow that without sound, so viewers get a *Tap to unmute* button), start without sound, show controls, WebRTC (lowest delay) or HLS.
- **On the video**: title, LIVE badge, picture-in-picture and full-screen buttons, and the text shown while the stream is not live.
- **Branding**: a logo (position, size, opacity, optional link) and a poster image for before the start and while offline. Images are stored in the database, so they are part of backups.
- **Where the player may appear**: limit the websites that may show the player (`frame-ancestors`); empty allows every website.

Copy the code as an **iframe**, as a **script** (`js/embed-loader.js` builds a responsive iframe, for site builders that don't accept iframes) or as a direct **link** (`/embed/<code>`). Changes to the look apply everywhere after saving; only a new shape or size needs the code to be pasted again.

For a paywalled stream the player shows a ticket screen: **Buy a ticket** and **Sign in** open the watch page or My tickets in a small window that hands the ticket back to the player, or the viewer types a ticket code. The player remembers the ticket on that website. Private streams (not public, no paywall) only show a message, so no credentials end up on other websites. Set `Panel__PublicUrl` so the code contains the public address of the panel.

## Local development

Requires: .NET 10 SDK and a MediaMTX binary.

1. Start MediaMTX with `deploy/mediamtx.yml`, but point the hook at your local panel (the secret must equal `MediaMtx__AuthHookSecret`, which is used to create the first server):
   ```bash
   MTX_AUTHHTTPADDRESS="http://127.0.0.1:5080/api/mediamtx/auth?secret=dev" ./mediamtx deploy/mediamtx.yml
   ```
2. Start the panel:
   ```bash
   cd src/MediaMtxPanel
   export ASPNETCORE_URLS=http://127.0.0.1:5080
   export Panel__AdminPassword=Admin12345
   export MediaMtx__ApiUrl=http://127.0.0.1:9997
   export MediaMtx__ApiPassword=apipass
   export MediaMtx__AuthHookSecret=dev
   dotnet run --no-launch-profile
   ```

Run the tests with `dotnet test MediaMtxPanel.slnx`.

## Configuration

All settings live in `appsettings.json` and can be overridden with environment variables (`Section__Key`). Servers themselves are managed in the panel; the `MediaMtx__…` settings only serve to create a server automatically on first start.

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | `DataSource=Data/panel.db` | SQLite database |
| `Panel__AdminEmail` / `Panel__AdminPassword` | `admin@localhost` / empty | First administrator (only if no administrator exists yet) |
| `Panel__AdminMustChangePassword` | `true` | The first administrator's password is one-time: a new password must be chosen at first login |
| `Panel__DataProtectionKeysPath` | empty | Folder for cookie keys (so logins survive a restart) |
| `Panel__HttpsRedirect` | `false` | Redirect HTTP → HTTPS |
| `Panel__PublicUrl` | empty | Public address of the panel for payment providers (return URL and Mollie webhook) and links; the address under Settings → Web server takes precedence |
| `Panel__EnableTestPayments` | `false` | Allow test payments without real money (for testing only) |
| `Panel__HookBaseUrl` | empty | Address at which MediaMTX servers reach the panel (for the configuration shown) |
| `MediaMtx__ApiUrl` | empty | If set and there are no servers yet: creates the first server with it |
| `MediaMtx__ServerName` | `Default` | Name of that first server |
| `MediaMtx__ApiUser` / `MediaMtx__ApiPassword` | `panel` / empty | API credentials of the first server |
| `MediaMtx__AuthHookSecret` | empty | Hook secret of the first server (empty = random) |
| `MediaMtx__PublicHost`, `…Port`, `HttpsUrls` | `localhost`, default ports | Public address of the first server |
| `MediaMtx__SyncIntervalSeconds` | `30` | Synchronization interval |
| `MediaMtx__StatisticsIntervalSeconds` | `60` | How often viewers and traffic of live streams are stored for the statistics |
| `MediaMtx__StatisticsRetentionDays` | `90` | Statistics older than this are deleted |
| `MediaMtx__ReportRetentionDays` | `0` | Days of viewer report history to keep (one row per stream per day); `0` keeps it forever |
| `Panel__SettingsPath` | next to the database | Where the settings from the Settings page are stored (`panel-settings.json`) |
| `Panel__UpdateRepository` | `rhisijmuiden/Mediamtx-controlpanel` | GitHub repository for the update check (empty = no check) |
| `Panel__UpdateBranch` | `main` | Branch compared while the release channel has no release |
| `Panel__PathBase` | empty | Serve the panel under this path, e.g. `/channel1` (see [Build My TV Station](#running-as-part-of-build-my-tv-station)) |
| `Panel__ServiceToken` | empty | Bearer token with administrator rights on `/api/v1/` (at least 32 characters) |
| `Panel__TrustedProxies` | empty | IPs or CIDRs whose `X-Forwarded-*` headers are believed (empty = any address, as before); the list under Settings → Web server takes precedence |

The application name, default language, web port and mail server are set in the panel under **Settings**, not here. A port chosen there overrides the port in `ASPNETCORE_URLS`.

## Connecting

| Protocol | Publish | View |
|---|---|---|
| RTSP | `rtsp://user:pass@host:8554/path` | `rtsp://user:pass@host:8554/path` |
| RTMP | `rtmp://host:1935/path?user=..&pass=..` | same |
| SRT | `srt://host:8890?streamid=publish:path:user:pass&pkt_size=1316` | `srt://host:8890?streamid=read:path:user:pass` |
| WebRTC | WHIP: `http://host:8889/path/whip` (Basic auth) | WHEP: `http://host:8889/path/whep`, browser: `http://host:8889/path` |
| HLS | – | `http://host:8888/path/index.m3u8` |

Behind a reverse proxy, WebRTC and HLS use `https://<public address>/media/<server>/webrtc/...` and `/media/<server>/hls/...` instead; see [Behind a reverse proxy (HTTPS)](#behind-a-reverse-proxy-https).

OBS: *Settings → Stream → Custom*, server `rtmp://host:1935`, stream key `path?user=..&pass=..` (the panel shows both ready to copy).

## Translations

Every text in the panel is written in English and looked up with `L["English text"]`. The translations are in `src/MediaMtxPanel/Localization/*.json`:

```json
{ "Live now": { "nl": "Nu live", "de": "Jetzt live", "fr": "En direct" } }
```

`python3 tools/check-translations.py` lists texts without a translation; the build workflow runs it on every push. To add a language, add it to `TextLocalizer.Languages` (`Services/Localization/TextLocalizer.cs`), add its code to `LANGUAGES` in the check script, and add the translations to the JSON files.

## Screenshots and the build workflow

`.github/workflows/build.yml` runs on every push and pull request: it builds the panel (with the commit as build number) and checks the translations. After a push to `main` it also starts MediaMTX and the panel with demo data (`tools/screenshots/start-demo.sh`), publishes a test picture with ffmpeg, takes the screenshots in this README with Playwright (`tools/screenshots/take-screenshots.mjs`) and commits them to `docs/screenshots` when they changed.

To take them yourself:

```bash
dotnet publish src/MediaMtxPanel -c Release -o /tmp/panel
tools/screenshots/start-demo.sh /tmp/panel          # MediaMTX + panel on http://127.0.0.1:5080
cd tools/screenshots && npm ci && npx playwright install chromium && node take-screenshots.mjs
```

## Project structure

```
src/MediaMtxPanel/
  Components/Layout/       Menu (NavMenu), Info panel with the update check, layouts
  Components/Pages/        Dashboard, Streams (list, new, details, edit), Servers, Users, Settings (general, web server, mail, single sign-on),
                           Portal (customer portal, payment settings), Watch (public watch and payment page)
  Components/Shared/       Form, copy fields, endpoint table, status badges, player, statistics bar
  Data/                    EF Core context, entities, migrations, seeding of roles/administrator
  Endpoints/               Authentication hook for MediaMTX, payment endpoints (purchase, Mollie webhook),
                           station contract (/healthz, /api/v1/streams)
  Services/                MediaMTX API client, stream and server management, synchronization, live status, URL builder, user management,
                           server statistics, Payments/ (Mollie, PayPal, paywall and tickets), settings, mail, update check,
                           Localization/ (translations)
  Localization/            Translations (JSON) for Dutch, German and French
  wwwroot/app.css          Design system on top of Bootstrap 5.3 and Bootstrap Icons
deploy/mediamtx.yml        MediaMTX configuration for use with the panel
deploy/mediamtx-panel-station.sh  Station command for Build My TV Station (installed as mediamtx-panel-station)
deploy/proxmox/            create-lxc.sh (on the Proxmox host) and install-in-lxc.sh (in the container)
deploy/debian/install.sh   Standalone installer for Debian 13 (bare metal or VM); uses install-in-lxc.sh
tests/MediaMtxPanel.Tests/ Integration tests (xUnit, WebApplicationFactory)
tools/                     Translation check, screenshot scripts
.github/workflows/         Build, translation check and README screenshots
```

Adding a database migration: `dotnet tool restore && cd src/MediaMtxPanel && dotnet ef migrations add <Name> -o Data/Migrations`. Migrations are applied automatically at startup.
