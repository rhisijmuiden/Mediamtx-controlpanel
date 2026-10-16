# MediaMTX Panel

Een beheerpaneel voor [MediaMTX](https://github.com/bluenviron/mediamtx), geschreven in ASP.NET Core (.NET 10, Blazor Server).

- **Gebruikers**: beheerders maken accounts aan (geen open registratie). Rollen: *Beheerder* (alle streams en gebruikers) en *Gebruiker* (alleen eigen streams). Blokkeren, wachtwoord resetten en verwijderen kan vanuit het paneel.
- **Streams**: aanmaken, bewerken, in-/uitschakelen en verwijderen. Een stream is een *publisher*-stream (OBS, ffmpeg, camera of browser stuurt naar de server) of een *ophaal*-stream (de server haalt een externe RTSP/RTMP/SRT/HLS/WHEP-bron op). Opties: openbaar bekijken, opnemen, maximaal aantal kijkers.
- **Direct na het aanmaken** toont het paneel de inloggegevens (publiceren en bekijken), de OBS-instellingen, alle endpoints (RTSP, RTMP, SRT, WebRTC WHIP/WHEP, HLS) met kopieerknoppen, en voorbeeldcommando's voor ffmpeg/ffplay.
- **Live status**: dashboard en streampagina's tonen live/offline, codecs, kijkers en verkeer; publishers en kijkers kun je verbreken.
- **Meerdere MediaMTX-servers**: beheerders koppelen onder *Servers* zoveel MediaMTX-instanties als nodig. Bij elke stream kies je de server; de URL's gebruiken het publieke adres en de poorten van die server. Het dashboard toont per server of hij online is.
- **Proxmox**: met één script een LXC-container met paneel en MediaMTX aanmaken (zie [Proxmox LXC](#proxmox-lxc)).

## Hoe het werkt

```
 encoder / kijker ──RTSP/RTMP/SRT/WebRTC/HLS──▶ MediaMTX ──(auth hook)──▶ Panel  ──▶ SQLite
                                                   ▲                         │
                                                   └──── Control API v3 ─────┘
```

- Het paneel is de bron van waarheid. Streams staan in de database (SQLite) en worden via de MediaMTX Control API (`/v3/config/paths/...`) als *paths* aangemaakt.
- MediaMTX draait met `authMethod: http`: bij elke publish/read/API-aanvraag vraagt MediaMTX aan het paneel (`POST /api/mediamtx/auth`) of het mag. Elke stream heeft eigen gebruikersnamen en wachtwoorden; nieuwe wachtwoorden gelden direct zonder MediaMTX te herstarten.
- Wijzigingen via de API bewaart MediaMTX alleen in het geheugen. Het paneel controleert daarom elke 30 seconden of de configuratie in MediaMTX nog klopt en zet ontbrekende streams terug (bijv. na een herstart van MediaMTX).
- Paden die niet in het paneel bestaan, weigert MediaMTX (`paths: {}` in `deploy/mediamtx.yml`).

## Starten met Docker

```bash
cp .env.example .env      # vul PUBLIC_HOST en de wachtwoorden in
docker compose up -d --build
```

Open daarna `http://<server>:8080` en log in met `ADMIN_EMAIL` / `ADMIN_PASSWORD`. Laat je `ADMIN_PASSWORD` leeg, dan genereert het paneel een wachtwoord en schrijft het één keer naar de log (`docker compose logs panel`).

Poorten van MediaMTX: 8554 (RTSP), 1935 (RTMP), 8890/udp (SRT), 8888 (HLS), 8889 + 8189/udp (WebRTC). De Control API (9997) is alleen binnen het docker-netwerk bereikbaar.

Zet het paneel in productie achter een reverse proxy met HTTPS (Caddy, nginx, Traefik). Forwarded headers worden ondersteund.

## Proxmox LXC

Het script `deploy/proxmox/create-lxc.sh` maakt op een Proxmox VE-host een Ubuntu 24.04- of Debian 13-container aan en installeert daarin het paneel als systemd-service, plus (standaard) MediaMTX als eerste server.

```bash
# op de Proxmox-host, in de uitgepakte mediamtx-panel map
bash deploy/proxmox/create-lxc.sh
```

Een Debian 13-container in plaats van Ubuntu:

```bash
CT_OS=debian bash deploy/proxmox/create-lxc.sh
```

Op Debian komt .NET 10 uit de pakketbron van Microsoft (`packages.microsoft.com`), omdat Debian zelf geen .NET levert. Debian 13-templates vereisen Proxmox VE 8.4 of nieuwer.

Alles is met omgevingsvariabelen aan te passen, bijvoorbeeld:

```bash
CT_ID=150 CT_IP=192.168.1.50/24 CT_GW=192.168.1.1 CT_STORAGE=local-zfs \
PUBLIC_HOST=stream.example.com ADMIN_EMAIL=ik@example.com \
bash deploy/proxmox/create-lxc.sh
```

| Variabele | Standaard | Betekenis |
|---|---|---|
| `CT_OS` | `ubuntu` | `ubuntu` (24.04) of `debian` (13) |
| `TEMPLATE_NAME` | `ubuntu-24.04-standard` / `debian-13-standard` | Andere template van hetzelfde OS |
| `CT_ID` | volgende vrije ID | Container-ID |
| `CT_HOSTNAME` | `mediamtx-panel` | Hostnaam |
| `CT_CORES` / `CT_MEMORY` / `CT_DISK` | 2 / 2048 MB / 8 GB | Resources (bouwen vraagt ±1,5 GB geheugen) |
| `CT_STORAGE` / `TEMPLATE_STORAGE` | `local-lvm` / `local` | Opslag voor de schijf en voor de template |
| `CT_BRIDGE` / `CT_VLAN` | `vmbr0` / leeg | Netwerk |
| `CT_IP` / `CT_GW` / `CT_DNS` | `dhcp` / leeg / leeg | IP-instellingen |
| `CT_PASSWORD` / `CT_SSH_KEY` | willekeurig / leeg | Root-toegang tot de container |
| `INSTALL_MEDIAMTX` | `yes` | MediaMTX in dezelfde container installeren |
| `MEDIAMTX_VERSION` | `1.21.2` | MediaMTX-versie (van GitHub releases) |
| `PANEL_PORT` | `8080` | Poort van het paneel |
| `ADMIN_EMAIL` / `ADMIN_PASSWORD` | `admin@localhost` / willekeurig | Eerste beheerder |
| `PUBLIC_HOST` | IP van de container | Adres in de stream-URL's |

Na afloop toont het script het adres van het paneel en de wachtwoorden. In de container:

- paneel: `/opt/mediamtx-panel`, configuratie `/etc/mediamtx-panel/panel.env`, data `/var/lib/mediamtx-panel`, service `mediamtx-panel`
- MediaMTX: `/opt/mediamtx`, configuratie `/etc/mediamtx/mediamtx.yml`, opnames `/var/lib/mediamtx/recordings`, service `mediamtx`

Bijwerken naar nieuwe code (database en instellingen blijven):

```bash
bash deploy/proxmox/create-lxc.sh update <CTID>
```

Andere Proxmox-containers of VM's met alleen MediaMTX koppel je daarna via **Servers** in het paneel.

## Lokaal ontwikkelen

Vereist: .NET 10 SDK en een MediaMTX-binary.

1. Start MediaMTX met `deploy/mediamtx.yml`, maar laat de hook naar je lokale paneel wijzen (het geheim moet gelijk zijn aan `MediaMtx__AuthHookSecret`, waarmee de eerste server wordt aangemaakt):
   ```bash
   MTX_AUTHHTTPADDRESS="http://127.0.0.1:5080/api/mediamtx/auth?secret=dev" ./mediamtx deploy/mediamtx.yml
   ```
2. Start het paneel:
   ```bash
   cd src/MediaMtxPanel
   export ASPNETCORE_URLS=http://127.0.0.1:5080
   export Panel__AdminPassword=Admin12345
   export MediaMtx__ApiUrl=http://127.0.0.1:9997
   export MediaMtx__ApiPassword=apipass
   export MediaMtx__AuthHookSecret=dev
   dotnet run --no-launch-profile
   ```

## Configuratie

Alle instellingen staan in `appsettings.json` en zijn met omgevingsvariabelen te overschrijven (`Sectie__Sleutel`). Servers zelf beheer je in het paneel; de `MediaMtx__…`-instellingen dienen alleen om bij de eerste start automatisch een server aan te maken.

| Instelling | Standaard | Betekenis |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | `DataSource=Data/panel.db` | SQLite-database |
| `Panel__AdminEmail` / `Panel__AdminPassword` | `admin@localhost` / leeg | Eerste beheerder (alleen als er nog geen beheerder is) |
| `Panel__DataProtectionKeysPath` | leeg | Map voor cookie-sleutels (zodat logins een herstart overleven) |
| `Panel__HttpsRedirect` | `false` | HTTP → HTTPS doorsturen |
| `Panel__HookBaseUrl` | leeg | Adres waarop MediaMTX-servers het paneel bereiken (voor de getoonde configuratie) |
| `MediaMtx__ApiUrl` | leeg | Als gezet en er nog geen servers zijn: maakt hiermee de eerste server aan |
| `MediaMtx__ServerName` | `Standaard` | Naam van die eerste server |
| `MediaMtx__ApiUser` / `MediaMtx__ApiPassword` | `panel` / leeg | API-gegevens van de eerste server |
| `MediaMtx__AuthHookSecret` | leeg | Hook-geheim van de eerste server (leeg = willekeurig) |
| `MediaMtx__PublicHost`, `…Port`, `HttpsUrls` | `localhost`, standaardpoorten | Publiek adres van de eerste server |
| `MediaMtx__SyncIntervalSeconds` | `30` | Interval van de synchronisatie |

## Verbinden

| Protocol | Publiceren | Bekijken |
|---|---|---|
| RTSP | `rtsp://user:pass@host:8554/pad` | `rtsp://user:pass@host:8554/pad` |
| RTMP | `rtmp://host:1935/pad?user=..&pass=..` | idem |
| SRT | `srt://host:8890?streamid=publish:pad:user:pass&pkt_size=1316` | `srt://host:8890?streamid=read:pad:user:pass` |
| WebRTC | WHIP: `http://host:8889/pad/whip` (Basic auth) | WHEP: `http://host:8889/pad/whep`, browser: `http://host:8889/pad` |
| HLS | – | `http://host:8888/pad/index.m3u8` |

OBS: *Instellingen → Stream → Aangepast*, server `rtmp://host:1935`, streamsleutel `pad?user=..&pass=..` (het paneel toont beide kant-en-klaar).

## Projectstructuur

```
src/MediaMtxPanel/
  Components/Pages/        Dashboard, Streams (lijst, nieuw, details, bewerken), Servers, Users
  Components/Shared/       Formulier, kopieervelden, endpoint-tabel, status-badges
  Data/                    EF Core-context, entiteiten, migraties, seeding van rollen/beheerder
  Endpoints/               Authenticatie-hook voor MediaMTX
  Services/                MediaMTX API-client, stream- en serverbeheer, synchronisatie, live status, URL-builder, gebruikersbeheer
deploy/mediamtx.yml        MediaMTX-configuratie voor gebruik met het paneel
deploy/proxmox/            create-lxc.sh (op de Proxmox-host) en install-in-lxc.sh (in de container)
```

Database-migratie toevoegen: `dotnet tool restore && cd src/MediaMtxPanel && dotnet ef migrations add <Naam> -o Data/Migrations`. Migraties worden bij het starten automatisch toegepast.
