# Orca Engine

Orca Engine is a Jellyfin plugin that adds personalization, discovery, analytics, metadata enrichment and a server-composed client API to an existing media library.

## Features

| Feature | What it provides |
|---|---|
| Server-composed Home | Builds rows such as Continue Watching, For You, Trending, Because You Watched, Coming Soon, Discover and more. |
| Personalization | Builds per-user taste profiles from viewing behaviour, ratings, favourites and other signals. |
| Recommendations | Combines multiple candidate sources with scoring and diversity re-ranking. |
| Content Similarity | Uses content embeddings for similar-title and taste-aware ranking. |
| Behaviour Tracking | Captures playback and user-data events and imports existing Jellyfin history. |
| Metadata Aggregation | Combines metadata, artwork, ratings, genres, providers and related information from multiple sources. |
| Trailer Support | Resolves, caches and prebuffers trailers when configured. |
| Discovery | Generates and stores personalised discovery picks. |
| Analytics | Provides behaviour, popularity, funnel, community-rating and catalogue statistics. |
| Jellyfin Integration | Works directly with the Jellyfin library, users, playback data and user state. |
| Client API | Exposes a REST API for clients such as OrcaX. |
| Observatory | Includes a built-in dashboard for monitoring, diagnostics and administration. |
| User Settings | Provides roaming per-user settings and feature information to connected clients. |
| Client Updates | Can serve OrcaX release information and APK assets. |

## Status

Orca Engine is currently in beta. The API, database schema and client contract are still evolving.

## Jellyfin compatibility

Orca Engine 1.4.3.0 targets **Jellyfin 12.1** and **.NET 10**. It uses the host's
Jellyfin and EF Core 10.0.11 assemblies; do not copy those assemblies into the plugin folder.
For Jellyfin 10.11, use the previous 1.2.4.0 release.

Build and test with the .NET 10 SDK:

```sh
dotnet test Wholphin.Engine.sln -c Release -p:SkipObservatoryBuild=true
```

The flag uses the committed Observatory bundle. Omit it when changing the dashboard sources.
Plugin binaries are in `Wholphin.Engine/bin/Release/net10.0/`; package the DLLs listed in
`build.yaml` together with `meta.json`.

For a manual upgrade, stop Jellyfin and back up the existing plugin folder outside the
Jellyfin plugins directory. Replace the old Orca Engine folder with the complete contents
of `orca-engine_1.4.3.0.zip`, including `meta.json`, then restart Jellyfin. Keep the existing
plugin configuration and `wholphin-engine` data directory. The DLL-only deployment script
does not update ABI metadata and must not be used for this major Jellyfin upgrade.

After startup, check `/OrcaEngine/Health` reports version `1.4.3.0`, then verify library sync,
watch-history import and Continue Watching with an existing user. Build and unit tests do
not replace this check on the deployed server.
