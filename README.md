# MediaMTX Panel

A control panel for [MediaMTX](https://github.com/bluenviron/mediamtx), written in ASP.NET Core (.NET 10, Blazor Server).

- **Users**: administrators create accounts (no open registration). Roles: *Administrator* (all streams and users), *User* (own streams only) and *Customer* (customer portal only). Blocking, resetting passwords and deleting are done from the panel.
- **Streams**: create, edit, enable/disable and delete. A stream is either a *publisher* stream (OBS, ffmpeg, a camera or a browser sends to the server) or a *pull* stream (the server pulls an external RTSP/RTMP/SRT/HLS/WHEP source). Options: public viewing, recording, maximum number of viewers.
- **Right after creating a stream** the panel shows the credentials (publish and view), the OBS settings, all endpoints (RTSP, RTMP, SRT, WebRTC WHIP/WHEP, HLS) with copy buttons, and example commands for ffmpeg/ffplay.
- **Live status**: the dashboard and stream pages show live/offline, codecs, viewers and traffic; publishers and viewers can be disconnected.
- **Multiple MediaMTX servers**: administrators add as many MediaMTX instances as needed under *Servers*. Each stream is assigned to a server; its URLs use that server's public address and ports. The dashboard shows whether each server is online.
- **Server setup over SSH**: when adding a server, the administrator can enter its SSH login. The panel then logs in, installs MediaMTX if it is missing, points it at the panel, restarts it and adds the server as soon as the connection works (see [Adding a MediaMTX server](#adding-a-mediamtx-server)).
- **Customer portal**: customers log in and see their own streams with a live preview, a tab with exactly what they need to broadcast, and a bar with CPU, RAM, load, network, bitrate and viewers (see [Customer portal and paywall](#customer-portal-and-paywall)).
- **Paywall**: customers sell tickets for their stream (e.g. a day ticket or a week pass) and let viewers pay with iDEAL (Mollie) or PayPal.
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

## Running with Docker

```bash
cp .env.example .env      # fill in PUBLIC_HOST and the passwords
docker compose up -d --build
```

Then open `http://<server>:8080` and log in with `ADMIN_EMAIL` / `ADMIN_PASSWORD`. If you leave `ADMIN_PASSWORD` empty, the panel generates a password and writes it to the log once (`docker compose logs panel`).

MediaMTX ports: 8554 (RTSP), 1935 (RTMP), 8890/udp (SRT), 8888 (HLS), 8889 + 8189/udp (WebRTC). The Control API (9997) is only reachable inside the docker network.

In production, put the panel behind a reverse proxy with HTTPS (Caddy, nginx, Traefik). Forwarded headers are supported.

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
- **HTTPS**: for use on the internet, put the panel behind a reverse proxy with HTTPS (Caddy, nginx, Traefik). Forwarded headers are supported.
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

Run the same script with `update` and the container ID. The latest code from GitHub is built and installed. The database and settings are kept.

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

The latest code is built and installed; the database and settings are kept. Running the installer again without `update` on a machine that already has the panel is refused, so the existing configuration is never overwritten.

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
deploy/debian/install.sh   Standalone installer for Debian 13 (bare metal or VM); uses install-in-lxc.sh
```

Adding a database migration: `dotnet tool restore && cd src/MediaMtxPanel && dotnet ef migrations add <Name> -o Data/Migrations`. Migrations are applied automatically at startup.
