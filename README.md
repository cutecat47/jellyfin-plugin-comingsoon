# Coming Soon — Jellyfin plugin

One shared **"Coming Soon"** library showing everything requested in Seerr (or added straight to
Sonarr/Radarr) that hasn't arrived yet — with poster, status, progress and ETA — in **any** Jellyfin
client (web, official apps, Moonfin), using plain item metadata. Placeholders can't be played.

```
Project Hail Mary             Downloading — 60% — a few hours left
Severance - Season 2          Downloading — 40% — under an hour left (4 episodes)
Avengers: Doomsday            Waiting for release — expected 5 Mar 2027
Lanterns - Season 1           Searching — looking for a release
Wicked: For Good              Stalled — 45% — The download is stalled with no connections
```

Targets **Jellyfin 12.1** (`net10.0`, `Jellyfin.Controller`/`Jellyfin.Model` 12.1.0, `targetAbi` 12.1.0.0).
Tested against the APIs of Radarr v5/v6, Sonarr v4 and Seerr v3.

## Install

1. Jellyfin → **Dashboard → Plugins → Repositories → +**
   - Name: `Coming Soon`
   - URL: `https://raw.githubusercontent.com/cutecat47/jellyfin-plugin-comingsoon/main/manifest.json`
2. **Catalog → Coming Soon → Install**, then restart the Jellyfin container
   (`docker restart jellyfin`).
3. Updates appear like any other plugin update (Jellyfin's daily *Update Plugins* task can install
   them for you). Restart the container after an update.

> Manual install instead: build (below) and copy `artifacts/ComingSoon_<version>/*` into
> `<jellyfin data>/plugins/ComingSoon_<version>/` — on linuxserver.io images that's
> `/config/data/plugins/`, on the official image `/config/plugins/`. Delete older
> `ComingSoon_*` folders first; two copies with the same plugin ID clash.

## Configure

Dashboard → Plugins → **Coming Soon**:

| Setting | Default | Notes |
|---|---|---|
| Seerr / Sonarr / Radarr URL + API key | — | Addresses as seen **from the Jellyfin container** (e.g. `http://sonarr:8989`, include any URL base). Any of them may be left empty. |
| Poll interval | 5 s | Radarr/Sonarr queues. Seerr is polled at most every 30 s (each Seerr request list call fans out to every *arr server). |
| Stub folder path | `/config/coming-soon` | Inside the container. Created automatically. Don't put anything else in it. |
| Library name | `Coming Soon` | Used only when the plugin creates the library. You can rename the library later; it's found by folder. |
| Progress step | 5 % | Item metadata and posters are rewritten only when the displayed status changes (bucket, progress crossing a step, ETA bucket). Set 1 for the finest updates. |
| Minimum seconds between updates | 60 | Per item, 10-3600. Lower (e.g. 15) for livelier progress: each update redraws two small images and saves one database record. Apps pick up changes when they reload (Moonfin: Home button, pull to refresh, reopening). |
| Verbose logging | off | Logs every raw queue record (when it changes) and poll summaries — turn on when reporting a problem. |

Then press **Test connections** — it calls each service *from the Jellyfin server* and reports the
version and how many queue items/requests it sees.

**The library is created for you** on first run (a *Movies* library on the stub folder, with
internet metadata, chapter and trickplay extraction turned off). If you'd rather create it yourself,
use content type **Movies**, add the stub folder, and **untick every metadata and image downloader**.

### Seerr: one important setting

Nothing extra is needed, but don't undo the plugin's safeguards: Seerr's Jellyfin sync marks a
request *available* when it finds a Jellyfin item with that title's TMDB/TVDB/IMDb id. Stubs never
carry those ids (no ids in file names, internet metadata off, ids stripped on every update). If you
enable metadata downloaders on the Coming Soon library, Jellyfin could attach real ids and Seerr
would wrongly mark requests as available. Optionally, also untick the Coming Soon library in
**Seerr → Settings → Jellyfin → Libraries**.

## How it works

- **Sources**: Radarr `GET /api/v3/queue?includeMovie=true`, Sonarr
  `GET /api/v3/queue?includeSeries=true&includeEpisode=true` (all pages), Seerr
  `GET /api/v1/request?filter=processing`, plus cached lookups (Seerr details for titles/artwork,
  Radarr movie / Sonarr series for release dates).
- **Merging**: movies by TMDB id, series by TVDB id (TMDB as a fallback), one item per series
  **season**. A season's progress counts each download once — every episode in a season pack
  repeats the pack's size in Sonarr's queue.
- **Status buckets**: *Searching*, *Waiting for release*, *Queued*, *Downloading (n%, ETA)*,
  *Stalled*, *Importing*. ETA buckets: under an hour / a few hours / later today / tomorrow /
  a few days / over a week / unknown.
- **Things that are deliberately ignored**: quality upgrades of things you already have; grabs for
  episodes that haven't aired yet (almost always fakes — the item shows *Waiting for release* with
  the air date); grabs with no usable files (e.g. a rejected `.exe` — the item shows *Searching* and
  the log tells you to blocklist it).
- **Library items**: one folder per item in the stub folder (`cs-movie-687163/`, `cs-tv-371980-s02/`)
  holding a 9.5 KB 4-second video (black, "Still downloading"), an `.nfo` with the title and status
  (Jellyfin re-reads it on every scan, so titles never revert to the folder name) and a hidden `.comingsoon.json`
  recording what was last written, so restarts never duplicate anything. Metadata written through
  Jellyfin's item APIs with **every field locked**: Name = title, Overview = status line,
  Tagline = short status, poster/backdrop from TMDB (via Seerr) or Sonarr/Radarr, Date added = request
  time, and a sort name that lists the **newest request first** under the default *Name* sort.
- **Why a Movies library**: every placeholder — a movie or a TV season — is a single video, so a
  Movies library shows them all as poster cards with overview in every client. A Shows library
  can't hold movies and would need a fake series/season/episode tree; *Mixed* has inconsistent
  client support; *Home videos* don't show posters/overviews properly in many apps.
- **Playback is blocked twice**: (1) `Items/{id}/PlaybackInfo` for a stub is answered with
  *not allowed* and no streams, so clients refuse before starting, and the user gets an on-screen
  message like *"Still downloading — a few hours left"*; (2) if a client starts anyway, the session is
  sent **Stop** plus the same message. Watch state is cleared afterwards, so stubs never appear in
  Continue Watching or as played. If both ever failed, all that plays is the "Still downloading" clip.
- **Removal**: when an item has left the download queue **and** Seerr reports it available or the
  real movie/season (all aired episodes) exists in another library, its stub and library item are
  deleted. Items that vanish from every source (request declined/deleted, download removed by hand)
  are removed after 10 minutes — as are Seerr requests whose movie/series was removed from (or
  unmonitored in) Radarr/Sonarr, since nothing will download them — but only while all services are
  reachable, so an outage never
  empties the library.
- **Not included**: a native progress bar on stubs (setting a playback position). Jellyfin has no way
  to keep one library out of Continue Watching, so it can't meet that requirement.

## Posters with progress

Each placeholder's poster — and a 16:9 thumbnail, used by Moonfin rows set to "Thumbnail" — has the status drawn onto it — a *COMING SOON* badge, the status in its
colour, the percentage, a progress bar and a detail line ("A few hours left", "Expected 5 Mar 2027",
"Looking for a release", a stall reason…). Items without artwork get a title card instead. Posters are
redrawn whenever the visible status changes (at most once a minute per item), so the poster grid in
**every** client shows progress without opening anything.

Drawing uses the SkiaSharp library Jellyfin already ships and the bundled Lato font
(SIL Open Font License, `Resources/Fonts/OFL.txt`). If drawing ever fails, the plain poster is used
and the log says so once.

## A "Currently Downloading" row on the home screen

Jellyfin clients build their own home screens, so a plugin can't add a brand-new row type that works
in the official apps. But every library automatically gets a **Recently Added** row on the home screen,
and for this library that row *is* a "currently downloading" row — newest request first, with the
progress posters. To make it look like a dedicated row rather than a collection:

1. **Rename the library** (Dashboard → Libraries → ⋮ → Rename) to e.g. **Currently Downloading**.
   The plugin creates the library only once (it remembers this in its settings) and never creates a
   second one for the same folder.
   The plugin finds it by folder, so renaming is safe. The row then reads
   *Recently Added in Currently Downloading* (wording varies by app).
2. **Hide the library tile** for each user: Settings → Home → *My Media* — untick the library under
   "Exclude from My Media" equivalents (the option is per user and per client).
3. **Move the row up**: in each client's Home settings, put *Recently Added Media* above other
   sections.

## Manual test checklist

Turn on **Verbose logging** while testing. Useful log command (linuxserver image):

```bash
grep "\[ComingSoon\]" $(ls -t /docker/appdata/jellyfin/log/log_*.log | head -1) | tail -80
```

1. **Loads cleanly** — after restart the log shows `Loaded plugin: "Coming Soon"`,
   `[ComingSoon] Poller started`, `Stub folder /config/coming-soon: N existing stubs loaded` and (first
   run) `Creating library 'Coming Soon'`; no `[ERR]` lines mentioning ComingSoon. **Test
   connections** shows three green ticks.
2. **Stubs appear** — request a movie and a TV season in Seerr. Within ~30 s the log shows
   `Tracking '…'` and `Created stub '…'`; the *Coming Soon* library shows both with poster and
   status (open the item to see the overview) in the web UI and in Moonfin.
3. **Status updates** — while downloading, the overview changes as progress crosses each step
   (`Update '…': Downloading — 45% — …` in the log), at most once a minute per item.
4. **Playback is blocked** — press play on a stub in the web UI, Moonfin and an official app.
   Expected: playback doesn't start (or stops within ~2 s) and a *"Still downloading — …"* message
   appears where the client supports messages. Log: `Refused playback of '…'` or
   `Playback of '…' started …; stopping it`. The stub must **not** appear in Continue Watching
   or show as played.
5. **Arrival** — after import, once Seerr marks it available (or the real item appears in your
   Movies/Shows library) the log shows `Removing '…': now available` and `Deleted stub '…'`, the
   stub disappears and the real item is in its normal library. Seerr must **not** have marked it
   available before the download finished.
6. **Restart mid-download** — `docker restart jellyfin` while something downloads: the log shows
   `N existing stubs loaded`, no new `Created stub` lines for existing items, no duplicates in
   the library.
7. **Outage** — stop Sonarr: one `[ComingSoon] Sonarr: can't connect … keeping last known state`
   error, stubs stay. Start it again: `Sonarr is reachable again`.

## Troubleshooting

- `Library '…' needs attention: internet metadata/image downloaders are enabled` — open the
  library's settings in Jellyfin and untick all metadata/image downloaders (see the Seerr note).
- `'…' still isn't in the library after a scan` — check the stub folder is readable by Jellyfin and
  that the library's folder is the configured stub folder.
- `grabbed a release with no usable files … blocklist it` — remove that item from Sonarr/Radarr's
  queue with *blocklist* ticked so a real release is grabbed.
- When reporting a problem, paste the `[ComingSoon]` log lines (they never contain API keys).

## Build from source

```bash
dotnet test -c Release
```

The ready-to-copy plugin folder is written to `artifacts/ComingSoon_<version>/`. Tests run entirely
offline against JSON fixtures modelled on the official APIs.

## Releasing

Bump `<Version>` in `Directory.Build.props` and `version` in `meta.json` and `build.yaml`, commit,
then push a matching tag (`git tag v0.3.0.0 && git push origin v0.3.0.0`). The *Release* workflow
tests, builds, publishes the zip and adds it to `manifest.json`. The bundled placeholder video is
rendered by the *Generate stub video* workflow.
