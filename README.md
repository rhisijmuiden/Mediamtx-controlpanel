# MediaMTX Panel

A control panel for [MediaMTX](https://github.com/bluenviron/mediamtx), written in ASP.NET Core (.NET 10, Blazor Server).

- **Users**: administrators create accounts (no open registration). Roles: *Administrator* (all streams and users), *User* (own streams only) and *Customer* (customer portal only). Blocking, resetting passwords and deleting are done from the panel.
- **Streams**: create, edit, enable/disable and delete. A stream is either a *publisher* stream (OBS, ffmpeg, a camera or a browser sends to the server) or a *pull* stream (the server pulls an external RTSP/RTMP/SRT/HLS/WHEP source). Options: public viewing, recording, maximum number of viewers.
- **Right after creating a stream** the panel shows the credentials (publish and view), the OBS settings, all endpoints (RTSP, RTMP, SRT, WebRTC WHIP/WHEP, HLS) with copy buttons, and example commands for ffmpeg/ffplay.
- **Live status**: the dashboard and stream pages show live/offline, codecs, viewers and traffic; publishers and viewers can be disconnected.
- **Multiple MediaMTX servers**: administrators add as many MediaMTX instances as needed under *Servers*. Each stream is assigned to a server; its URLs use that server's public address and ports. The dashboard shows whether each server is online.
- **Customer portal**: customers log in and see their own streams with a live preview, a tab with exactly what they need to broadcast, and a bar with CPU, RAM, load, network, bitrate and viewers (see [Customer portal and paywall](#customer-portal-and-paywall)).
- **Paywall**: customers sell tickets for their stream (e.g. a day ticket or a week pass) and let viewers pay with iDEAL (Mollie) or PayPal.
- **Proxmox**: create an LXC container with the panel and MediaMTX using a single script (see [Proxmox LXC](#proxmox-lxc)).

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

## Running with Docker

```bash
cp .env.example .env      # fill in PUBLIC_HOST and the passwords
docker compose up -d --build
```

Then open `http://<server>:8080` and log in with `ADMIN_EMAIL` / `ADMIN_PASSWORD`. If you leave `ADMIN_PASSWORD` empty, the panel generates a password and writes it to the log once (`docker compose logs panel`).

MediaMTX ports: 8554 (RTSP), 1935 (RTMP), 8890/udp (SRT), 8888 (HLS), 8889 + 8189/udp (WebRTC). The Control API (9997) is only reachable inside the docker network.

In production, put the panel behind a reverse proxy with HTTPS (Caddy, nginx, Traefik). Forwarded headers are supported.

## Proxmox LXC

The script `deploy/proxmox/create-lxc.sh` creates a Debian 13 container on a Proxmox VE host and installs the panel in it as a systemd service, plus (by default) MediaMTX as the first server.

Paste this into the shell of the Proxmox host (as root, e.g. via *Datacenter → node → Shell*):

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/proxmox/create-lxc.sh)"
```

Like the scripts from [community-scripts.org](https://community-scripts.org), the script first asks how the container should be created:

- **Default**: 2 cores, 2048 MB memory, 8 GB disk, DHCP on `vmbr0`.
- **Advanced**: you choose the container ID, hostname, cores, memory, swap, disk size, storage (from the list on your Proxmox host), network bridge, static IP or DHCP, gateway, VLAN, DNS, root password, SSH key, whether MediaMTX is installed in the container too, the panel port, the administrator's email address and the public address for the streams.

Before anything is created, the script shows a summary to confirm.

The script fetches the source code from GitHub itself (`REPO_BRANCH=…` selects another branch). Running `bash deploy/proxmox/create-lxc.sh` from an extracted copy of this repository also works; that code is then used.

When it is done, the script shows the address (`http://<container-ip>:8080`), the administrator login and a **one-time password** in the shell. On first login the panel immediately asks for a new password; the one-time password is not stored anywhere in the container.

.NET 10 comes from Microsoft's package feed (`packages.microsoft.com`), because Debian does not ship .NET itself. The Debian 13 template requires Proxmox VE 8.4 or newer.

To run without questions (e.g. for automation), set `INTERACTIVE=no` and pass the settings as environment variables:

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
| `CT_CORES` / `CT_MEMORY` / `CT_DISK` | 2 / 2048 MB / 8 GB | Resources (building needs ~1.5 GB memory) |
| `CT_STORAGE` / `TEMPLATE_STORAGE` | `local-lvm` / `local` | Storage for the disk and for the template |
| `CT_BRIDGE` / `CT_VLAN` | `vmbr0` / empty | Network |
| `CT_IP` / `CT_GW` / `CT_DNS` | `dhcp` / empty / empty | IP settings |
| `CT_PASSWORD` / `CT_SSH_KEY` | random / empty | Root access to the container |
| `INSTALL_MEDIAMTX` | `yes` | Install MediaMTX in the same container |
| `MEDIAMTX_VERSION` | `1.21.2` | MediaMTX version (from GitHub releases) |
| `PANEL_PORT` | `8080` | Panel port |
| `ADMIN_EMAIL` / `ADMIN_PASSWORD` | `admin@localhost` / random | First administrator (the password is one-time) |
| `REPO` / `REPO_BRANCH` | `rhisijmuiden/Mediamtx-controlpanel` / `main` | Where the source code comes from |
| `PUBLIC_HOST` | IP of the container | Address in the stream URLs |

Inside the container:

- panel: `/opt/mediamtx-panel`, configuration `/etc/mediamtx-panel/panel.env`, data `/var/lib/mediamtx-panel`, service `mediamtx-panel`
- MediaMTX: `/opt/mediamtx`, configuration `/etc/mediamtx/mediamtx.yml`, recordings `/var/lib/mediamtx/recordings`, service `mediamtx`

Updating to new code (database and settings are kept):

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/proxmox/create-lxc.sh)" _ update <CTID>
```

Other Proxmox containers or VMs running only MediaMTX can then be added under **Servers** in the panel.

## Customer portal and paywall

![Customer portal](docs/customer-portal.png)

Under **Users**, create an account with the *Customer* role and select that customer as *Owner* of a stream. After logging in, the customer lands in **My portal** (`/portal`):

- **Preview**: the stream plays in the browser (WebRTC, or HLS if the browser cannot play the codec over WebRTC), so the customer can see whether the video is coming in properly.
- **Statistics bar**: CPU, RAM, load and network traffic of the server, plus bitrate in/out, viewers, codecs and how long the stream has been live. If MediaMTX runs on the same machine as the panel (API URL on `127.0.0.1`), the panel reads this itself. For other servers, install `prometheus-node-exporter` on that server and fill in the *Statistics URL* under **Servers** (e.g. `http://10.0.0.21:9100/metrics`).
- **Connect tab**: OBS server and stream key, username and password, and the URLs for RTSP, RTMP, SRT and WHIP.
- **Share tab**: the public watch page `/watch/<code>` and the viewing credentials.
- **Paywall tab**: manage offers (name, price, access duration, such as "Day ticket HD" or "Week pass"), turn the paywall on and off, and view sales and revenue.

### Payments

Under **Payment settings** the customer connects their own account; the money goes directly to the customer. Keys are stored encrypted.

- **iDEAL via Mollie**: API key from the Mollie dashboard (`test_…` to try it out, `live_…` for real payments).
- **PayPal**: Client ID and Secret of a REST app on developer.paypal.com, with a switch for sandbox or live.

The viewer opens the watch page, picks an offer and pays. The provider then sends the viewer back and the stream starts playing immediately. The viewer gets a personal link and can also watch via VLC, a smart TV or another device: they use the username `ticket` with the ticket code as the password (RTSP, HLS, SRT). Once the purchased access duration has passed, the ticket stops working. Viewing without a ticket is refused, even if the stream is set to *public*.

![Watch page with tickets](docs/watch-page-tickets.png)

For payments, the panel must be reachable at a public address. Set that address in `Panel__PublicUrl` (e.g. `https://panel.example.com`); the payment providers use it to send the viewer back, and Mollie sends the payment status there (`/api/payments/mollie/webhook`).

With `Panel__EnableTestPayments=true` a *Test payment* button appears that lets you go through the whole flow without real money. Turn it off in production.

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

## Configuration

All settings live in `appsettings.json` and can be overridden with environment variables (`Section__Key`). Servers themselves are managed in the panel; the `MediaMtx__…` settings only serve to create a server automatically on first start.

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | `DataSource=Data/panel.db` | SQLite database |
| `Panel__AdminEmail` / `Panel__AdminPassword` | `admin@localhost` / empty | First administrator (only if no administrator exists yet) |
| `Panel__AdminMustChangePassword` | `true` | The first administrator's password is one-time: a new password must be chosen at first login |
| `Panel__DataProtectionKeysPath` | empty | Folder for cookie keys (so logins survive a restart) |
| `Panel__HttpsRedirect` | `false` | Redirect HTTP → HTTPS |
| `Panel__PublicUrl` | empty | Public address of the panel for payment providers (return URL and Mollie webhook) |
| `Panel__EnableTestPayments` | `false` | Allow test payments without real money (for testing only) |
| `Panel__HookBaseUrl` | empty | Address at which MediaMTX servers reach the panel (for the configuration shown) |
| `MediaMtx__ApiUrl` | empty | If set and there are no servers yet: creates the first server with it |
| `MediaMtx__ServerName` | `Default` | Name of that first server |
| `MediaMtx__ApiUser` / `MediaMtx__ApiPassword` | `panel` / empty | API credentials of the first server |
| `MediaMtx__AuthHookSecret` | empty | Hook secret of the first server (empty = random) |
| `MediaMtx__PublicHost`, `…Port`, `HttpsUrls` | `localhost`, default ports | Public address of the first server |
| `MediaMtx__SyncIntervalSeconds` | `30` | Synchronization interval |

## Connecting

| Protocol | Publish | View |
|---|---|---|
| RTSP | `rtsp://user:pass@host:8554/path` | `rtsp://user:pass@host:8554/path` |
| RTMP | `rtmp://host:1935/path?user=..&pass=..` | same |
| SRT | `srt://host:8890?streamid=publish:path:user:pass&pkt_size=1316` | `srt://host:8890?streamid=read:path:user:pass` |
| WebRTC | WHIP: `http://host:8889/path/whip` (Basic auth) | WHEP: `http://host:8889/path/whep`, browser: `http://host:8889/path` |
| HLS | – | `http://host:8888/path/index.m3u8` |

OBS: *Settings → Stream → Custom*, server `rtmp://host:1935`, stream key `path?user=..&pass=..` (the panel shows both ready to copy).

## Project structure

```
src/MediaMtxPanel/
  Components/Pages/        Dashboard, Streams (list, new, details, edit), Servers, Users,
                           Portal (customer portal, payment settings), Watch (public watch and payment page)
  Components/Shared/       Form, copy fields, endpoint table, status badges, player, statistics bar
  Data/                    EF Core context, entities, migrations, seeding of roles/administrator
  Endpoints/               Authentication hook for MediaMTX, payment endpoints (purchase, Mollie webhook)
  Services/                MediaMTX API client, stream and server management, synchronization, live status, URL builder, user management,
                           server statistics, Payments/ (Mollie, PayPal, paywall and tickets)
deploy/mediamtx.yml        MediaMTX configuration for use with the panel
deploy/proxmox/            create-lxc.sh (on the Proxmox host) and install-in-lxc.sh (in the container)
```

Adding a database migration: `dotnet tool restore && cd src/MediaMtxPanel && dotnet ef migrations add <Name> -o Data/Migrations`. Migrations are applied automatically at startup.
