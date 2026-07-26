# Catch-up Catalog for Jellyfin 10.11

Target compatibility:

- Jellyfin Server: 10.11.x
- Target ABI: 10.11.0.0
- .NET: net9.0
- Tested configuration target: Jellyfin 10.11.11 with Jellyfin.Xtream 0.8.1.0

## What it does

The plugin reads Dispatcharr's Xtream-compatible API and creates one Jellyfin channel:

- Catch-up Movies
- Catch-up Series
- Catch-up Programs

It retrieves catch-up EPG entries, classifies them, groups repeated shows as series,
creates seasons and episodes, and builds playback URLs through Dispatcharr.

With a TMDb API Read Access Token it also requests:

- posters
- backdrops
- overview
- year
- rating

## Install

```bash
unzip jellyfin-catchup-catalog-v0.1.0.zip
cd jellyfin-catchup-catalog-v0.1.0
chmod +x install.sh
sudo ./install.sh
```

The installer uses a temporary `mcr.microsoft.com/dotnet/sdk:9.0` container to compile.
The container is removed automatically after the build.

## Configure

Open:

```text
Dashboard -> Plugins -> My Plugins -> Catch-up Catalog
```

Enter:

- Dispatcharr Base URL: `http://192.168.1.100:9191`
- Dispatcharr username
- Dispatcharr XC Password
- Archive Days: `7`
- Metadata Language: `he-IL`
- Optional TMDb API Read Access Token

Save, restart Jellyfin once, then open `Catch-up Catalog` from the home screen.

## Logs

```bash
docker logs jellyfin --since 10m 2>&1 | grep -i -E "Catch-up Catalog|CatchupCatalog"
```

## Beta limitations

- Classification is heuristic and depends on EPG titles/descriptions.
- Programs without a TMDb match use the channel logo.
- The first opening can be slow because EPG is loaded from every catch-up channel.
- Credentials are embedded in Dispatcharr playback URLs, as with standard Xtream catch-up.
