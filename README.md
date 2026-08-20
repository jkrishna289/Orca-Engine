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
