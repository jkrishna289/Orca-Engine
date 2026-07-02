# Orca Engine

Server-side **intelligence engine** (Jellyfin plugin) for the [Wholphin](https://github.com/) Android TV client — the brain/backbone that turns *"give me all movies"* into *"given everything about this user, what's best to show now?"*

It offloads heavy work from the TV app and centralizes decisions: personalized home pages, recommendations, a unified available + requestable (Jellyseerr) catalog, caching, and centralized administration.

## Status

Early development — **Milestone 1: Foundation + personalization core** (Tier 1: in-process plugin, SQLite + in-memory cache). See the architecture blueprint for the full vision and phased plan.

## Requirements

- Jellyfin **10.11+** server
- .NET **9** SDK (to build)

## Build

```sh
dotnet build -c Release
```

The plugin DLL is produced under `Wholphin.Engine/bin/Release/net9.0/`.

## Install (local dev)

Copy `Wholphin.Engine.dll` (and its dependencies) into a `Wholphin.Engine` folder under your Jellyfin server's `plugins/` directory, then restart Jellyfin. The plugin appears under **Dashboard → Plugins**, and `GET /WholphinEngine/Health` returns its status.

## Architecture

Phased hybrid: **Tier 1** embedded plugin (SQLite) → **Tier 2** + PostgreSQL/pgvector + Redis → **Tier 3** standalone companion service. Built behind ports/adapters so the code graduates tiers without rewrites.
