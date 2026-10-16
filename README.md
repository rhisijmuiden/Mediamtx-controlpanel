# MediaMTX Panel

Een beheerpaneel voor [MediaMTX](https://github.com/bluenviron/mediamtx), geschreven in ASP.NET Core (.NET 10, Blazor Server).

- **Gebruikers**: beheerders maken accounts aan (geen open registratie). Rollen: *Beheerder* (alle streams en gebruikers), *Gebruiker* (alleen eigen streams) en *Klant* (alleen het klantportaal). Blokkeren, wachtwoord resetten en verwijderen kan vanuit het paneel.
- **Streams**: aanmaken, bewerken, in-/uitschakelen en verwijderen. Een stream is een *publisher*-stream (OBS, ffmpeg, camera of browser stuurt naar de server) of een *ophaal*-stream (de server haalt een externe RTSP/RTMP/SRT/HLS/WHEP-bron op). Opties: openbaar bekijken, opnemen, maximaal aantal kijkers.
- **Direct na het aanmaken** toont het paneel de inloggegevens (publiceren en bekijken), de OBS-instellingen, alle endpoints (RTSP, RTMP, SRT, WebRTC WHIP/WHEP, HLS) met kopieerknoppen, en voorbeeldcommando's voor ffmpeg/ffplay.
- **Live status**: dashboard en streampagina's tonen live/offline, codecs, kijkers en verkeer; publishers en kijkers kun je verbreken.
- **Meerdere MediaMTX-servers**: beheerders koppelen onder *Servers* zoveel MediaMTX-instanties als nodig. Bij elke stream kies je de server; de URL's gebruiken het publieke adres en de poorten van die server. Het dashboard toont per server of hij online is.
- **Klantportaal**: klanten loggen in en zien hun eigen streams met een live preview, een tabblad met precies wat ze nodig hebben om uit te zenden, en een balk met CPU, RAM, load, netwerk, bitrate en kijkers (zie [Klantportaal en paywall](#klantportaal-en-paywall)).
- **Paywall**: klanten verkopen tickets voor hun stream (bijv. dagticket of weekpas) en laten kijkers betalen met iDEAL (Mollie) of PayPal.
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

Het script `deploy/proxmox/create-lxc.sh` maakt op een Proxmox VE-host een Debian 13-container aan en installeert daarin het paneel als systemd-service, plus (standaard) MediaMTX als eerste server.

Plak dit in de shell van de Proxmox-host (als root, bijvoorbeeld via *Datacenter → node → Shell*):

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/proxmox/create-lxc.sh)"
```

Het script haalt de broncode zelf van GitHub (`REPO_BRANCH=…` kiest een andere branch). Vanuit een uitgepakte kopie van deze repository werkt `bash deploy/proxmox/create-lxc.sh` ook; dan wordt die code gebruikt.

Als het klaar is, toont het script in de shell het adres (`http://<ip-van-de-container>:8080`), de beheerderslogin en een **eenmalig wachtwoord**. Bij de eerste keer inloggen vraagt het paneel meteen om een eigen wachtwoord; het eenmalige wachtwoord wordt nergens in de container bewaard.

.NET 10 komt uit de pakketbron van Microsoft (`packages.microsoft.com`), omdat Debian zelf geen .NET levert. De Debian 13-template vereist Proxmox VE 8.4 of nieuwer.

Alles is met omgevingsvariabelen aan te passen, bijvoorbeeld:

```bash
CT_ID=150 CT_IP=192.168.1.50/24 CT_GW=192.168.1.1 CT_STORAGE=local-zfs \
PUBLIC_HOST=stream.example.com ADMIN_EMAIL=ik@example.com \
bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/proxmox/create-lxc.sh)"
```

| Variabele | Standaard | Betekenis |
|---|---|---|
| `TEMPLATE_NAME` | `debian-13-standard` | Specifieke Debian 13-template |
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
| `ADMIN_EMAIL` / `ADMIN_PASSWORD` | `admin@localhost` / willekeurig | Eerste beheerder (wachtwoord is eenmalig) |
| `REPO` / `REPO_BRANCH` | `rhisijmuiden/Mediamtx-controlpanel` / `main` | Waar de broncode vandaan komt |
| `PUBLIC_HOST` | IP van de container | Adres in de stream-URL's |

In de container:

- paneel: `/opt/mediamtx-panel`, configuratie `/etc/mediamtx-panel/panel.env`, data `/var/lib/mediamtx-panel`, service `mediamtx-panel`
- MediaMTX: `/opt/mediamtx`, configuratie `/etc/mediamtx/mediamtx.yml`, opnames `/var/lib/mediamtx/recordings`, service `mediamtx`

Bijwerken naar nieuwe code (database en instellingen blijven):

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/proxmox/create-lxc.sh)" _ update <CTID>
```

Andere Proxmox-containers of VM's met alleen MediaMTX koppel je daarna via **Servers** in het paneel.

## Klantportaal en paywall

![Klantportaal](docs/klantportaal.png)

Maak onder **Gebruikers** een account met de rol *Klant* en kies die klant als *Eigenaar* bij een stream. Na het inloggen komt de klant in **Mijn portaal** (`/portal`):

- **Preview**: de stream speelt af in de browser (WebRTC, of HLS als de browser de codec niet via WebRTC kan afspelen), zodat de klant ziet of het beeld goed binnenkomt.
- **Statistiekenbalk**: CPU, RAM, load en netwerkverkeer van de server, plus bitrate in/uit, kijkers, codecs en hoe lang de stream live is. Draait MediaMTX op dezelfde machine als het paneel (API-URL op `127.0.0.1`), dan leest het paneel dit zelf. Voor andere servers installeer je `prometheus-node-exporter` op die server en vul je bij **Servers** de *Statistieken-URL* in (bijv. `http://10.0.0.21:9100/metrics`).
- **Tabblad Verbinden**: OBS-server en streamsleutel, gebruikersnaam en wachtwoord, en de URL's voor RTSP, RTMP, SRT en WHIP.
- **Tabblad Delen**: de openbare kijkpagina `/watch/<code>` en de kijkgegevens.
- **Tabblad Paywall**: aanbiedingen beheren (naam, prijs, toegangsduur, zoals "Dagticket HD" of "Weekpas"), de paywall aan- en uitzetten, en verkopen en omzet bekijken.

### Betalen

Onder **Betaalinstellingen** koppelt de klant een eigen account; het geld komt rechtstreeks bij de klant binnen. Sleutels worden versleuteld opgeslagen.

- **iDEAL via Mollie**: API-sleutel uit het Mollie-dashboard (`test_…` om te proberen, `live_…` voor echt).
- **PayPal**: Client ID en Secret van een REST-app op developer.paypal.com, met een schakelaar voor sandbox of live.

De kijker opent de kijkpagina, kiest een aanbieding en betaalt. Daarna stuurt de provider de kijker terug en speelt de stream direct af. De kijker krijgt een persoonlijke link en kan ook kijken via VLC, een smart-tv of een ander apparaat: daarvoor gebruikt hij de gebruikersnaam `ticket` en de ticketcode als wachtwoord (RTSP, HLS, SRT). Na de gekochte toegangsduur werkt het ticket niet meer. Kijken zonder ticket wordt geweigerd, ook als de stream op *openbaar* staat.

![Kijkpagina met tickets](docs/kijkpagina-tickets.png)

Voor betalingen moet het paneel op een publiek adres bereikbaar zijn. Zet dat adres in `Panel__PublicUrl` (bijv. `https://panel.example.com`); dat adres gebruiken de betaalproviders om de kijker terug te sturen, en Mollie stuurt er de betaalstatus heen (`/api/payments/mollie/webhook`).

Met `Panel__EnableTestPayments=true` verschijnt er een knop *Testbetaling* waarmee je de hele flow zonder echt geld kunt doorlopen. Zet die in productie uit.

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
| `Panel__AdminMustChangePassword` | `true` | Het wachtwoord van de eerste beheerder is eenmalig: bij de eerste login moet een eigen wachtwoord gekozen worden |
| `Panel__DataProtectionKeysPath` | leeg | Map voor cookie-sleutels (zodat logins een herstart overleven) |
| `Panel__HttpsRedirect` | `false` | HTTP → HTTPS doorsturen |
| `Panel__PublicUrl` | leeg | Publiek adres van het paneel voor betaalproviders (terugkeer-URL en Mollie-webhook) |
| `Panel__EnableTestPayments` | `false` | Testbetalingen zonder echt geld toestaan (alleen om te testen) |
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
  Components/Pages/        Dashboard, Streams (lijst, nieuw, details, bewerken), Servers, Users,
                           Portal (klantportaal, betaalinstellingen), Watch (openbare kijk- en betaalpagina)
  Components/Shared/       Formulier, kopieervelden, endpoint-tabel, status-badges, speler, statistiekenbalk
  Data/                    EF Core-context, entiteiten, migraties, seeding van rollen/beheerder
  Endpoints/               Authenticatie-hook voor MediaMTX, betaal-endpoints (kopen, Mollie-webhook)
  Services/                MediaMTX API-client, stream- en serverbeheer, synchronisatie, live status, URL-builder, gebruikersbeheer,
                           serverstatistieken, Payments/ (Mollie, PayPal, paywall en tickets)
deploy/mediamtx.yml        MediaMTX-configuratie voor gebruik met het paneel
deploy/proxmox/            create-lxc.sh (op de Proxmox-host) en install-in-lxc.sh (in de container)
```

Database-migratie toevoegen: `dotnet tool restore && cd src/MediaMtxPanel && dotnet ef migrations add <Naam> -o Data/Migrations`. Migraties worden bij het starten automatisch toegepast.
