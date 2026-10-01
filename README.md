# Coming Soon — Jellyfin plugin

Keeps a shared "Coming Soon" library of everything requested in Seerr (or added in Sonarr/Radarr)
that hasn't arrived yet, with status, download progress and ETA visible in any Jellyfin client.

> **Status:** in development. Current builds poll Radarr, Sonarr and Seerr and log what they
> track; the library itself (stub items, artwork, blocked playback) is the next milestone.

Targets **Jellyfin 12.1** (`net10.0`, `Jellyfin.Controller`/`Jellyfin.Model` 12.1.0, `targetAbi` 12.1.0.0).

## Install (plugin repository — recommended)

1. Jellyfin → **Dashboard → Plugins → Repositories → +**
   - Name: `Coming Soon`
   - URL: `https://raw.githubusercontent.com/cutecat47/jellyfin-plugin-comingsoon/main/manifest.json`
2. **Catalog** → *Coming Soon* → **Install**, then restart Jellyfin.
3. New versions appear as plugin updates (Jellyfin's daily *Update Plugins* task can install them
   automatically). Restart Jellyfin after an update.

If you previously copied the plugin in by hand, delete that `ComingSoon_*` folder from the
plugins directory first — two copies with the same plugin ID will clash.

## Configure

Dashboard → Plugins → Coming Soon. Use addresses reachable **from the Jellyfin container**
(e.g. `http://sonarr:8989`, include any URL base), then **Test connections**.

| Setting | Default | Notes |
|---|---|---|
| Seerr / Sonarr / Radarr URL + API key | — | Any of them can be left empty |
| Poll interval | 5 s | Seerr is polled at most every 30 s |
| Stub folder path | `/config/coming-soon` | Path inside the Jellyfin container |
| Progress step | 5 % | Metadata changes only when progress crosses a step (max once a minute per item) |
| Verbose logging | off | Logs raw queue records and poll summaries — useful for bug reports |

All log lines start with `[ComingSoon]`.

## Build from source

```bash
dotnet test -c Release
```

The ready-to-copy plugin folder is written to `artifacts/ComingSoon_<version>/`.

## Releasing

Bump `<Version>` in `Directory.Build.props` and `version` in `meta.json` (and `build.yaml`),
commit, then push a matching tag, e.g. `git tag v0.2.2.0 && git push origin v0.2.2.0`.
The *Release* workflow tests, builds, publishes the zip and adds it to `manifest.json`.
