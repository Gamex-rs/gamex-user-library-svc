# Gamex — User Library Service Specification

> **Repository:** `gamex-rs/gamex-user-library-svc`  
> **Version:** 1.0  
> **Date:** 2026-05-12

---

## 1. Purpose

The User Library Service is the identity provider and user-data owner for Gamex. It is responsible for:

- User registration, authentication, and JWT issuance
- User profiles
- Each user's personal game library (owned, wishlist, for exchange, for sale, want to buy) — entries are per game + platform + format, so the same game can be held on several platforms/formats
- Public listings derived from library entries
- User-to-user ratings after completed transactions
- Publishing `library.game.status.changed` events to SQS so the Game Catalog Service can maintain aggregate stats
- Maintaining a **read-only replica** of game and platform metadata, consumed from the Game Catalog Service's `gamex-catalog-events` queue. The Game Catalog Service remains the source of truth; this service never edits game data and never accepts game metadata from the client.

---

## 2. Tech Stack

| Concern | Technology |
|---|---|
| Runtime | .NET 10 |
| Framework | ASP.NET Core Web API |
| ORM | Entity Framework Core 8 |
| Database | MySQL 8 (Aurora MySQL Serverless v2 on AWS, official Docker image locally) |
| Auth | ASP.NET Core Identity + custom JWT (RS256) |
| OAuth | Google & Facebook — Authorization Code Flow, server-side exchange |
| SQS Publisher | AWSSDK.SQS |
| SQS Consumer | AWSSDK.SQS + background `IHostedService` (catalog replica) |
| Config / Secrets | AWS SSM Parameter Store + Secrets Manager (env vars locally) |

---

## 3. Authentication & Token Strategy

- **Access token:** JWT, RS256, 15-minute lifetime. Claims: `sub` (user id), `email`, `name`, `role`.
- **Refresh token:** opaque random string, stored hashed in `refresh_tokens` table, 30-day lifetime. Rotated on every use (old token invalidated, new token issued).
- **JWT key pair:** RS256 private key held only by this service (in Secrets Manager). Public key distributed to all other services via SSM Parameter Store so they can validate tokens without calling this service.
- **OAuth (Google / Facebook):** Backend-initiated Authorization Code Flow. Frontend redirects to provider; provider redirects back to this service's callback endpoint. Service exchanges code for profile, finds or creates a user, and issues its own JWT pair.

---

## 4. API Endpoints

Base path: `/api/v1`

### 4.1 Authentication

| Method | Path | Auth | Description |
|---|---|---|---|
| `POST` | `/auth/register` | None | Register with email + password |
| `POST` | `/auth/login` | None | Login → returns JWT pair |
| `POST` | `/auth/refresh` | None | Exchange refresh token → new JWT pair |
| `POST` | `/auth/logout` | Bearer | Revoke refresh token |
| `POST` | `/auth/forgot-password` | None | Send password reset email |
| `POST` | `/auth/reset-password` | None | Reset password with token from email |
| `GET` | `/auth/oauth/google` | None | Redirect to Google consent screen |
| `GET` | `/auth/oauth/google/callback` | None | Google OAuth callback |
| `GET` | `/auth/oauth/facebook` | None | Redirect to Facebook consent screen |
| `GET` | `/auth/oauth/facebook/callback` | None | Facebook OAuth callback |

#### `POST /auth/register` — Request

```json
{
  "displayName": "string",
  "email": "string",
  "password": "string",
  "platformIds": ["uuid", "uuid"]
}
```

`platformIds` is optional at registration — the user can skip platform selection and set it later from profile settings. Platform IDs reference the `platforms` table in the Game Catalog Service; the frontend gets them from `GET /api/v1/platforms` (Game Catalog Service). Each ID is validated against the local `catalog_platforms` replica — unknown IDs return `400`.

Returns `201` with JWT pair on success. Returns `409` if email already exists.

#### `POST /auth/login` — Response

```json
{
  "accessToken": "string",
  "refreshToken": "string",
  "expiresIn": 900
}
```

### 4.2 Users

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/users/{id}` | None | Public profile |
| `GET` | `/users/me` | Bearer | Own profile with private fields |
| `PUT` | `/users/me` | Bearer | Update display name, avatar, city, bio |
| `PUT` | `/users/me/password` | Bearer | Change password |
| `DELETE` | `/users/me` | Bearer | Deactivate account (soft delete) |
| `GET` | `/users/me/platforms` | Bearer | Get own platform preferences |
| `PUT` | `/users/me/platforms` | Bearer | Replace platform preferences |
| `GET` | `/users/{id}/ratings` | None | Paginated list of ratings received |
| `POST` | `/users/{id}/ratings` | Bearer | Submit rating after completed transaction |

#### `GET /users/{id}` — Response

```json
{
  "id": "uuid",
  "displayName": "string",
  "avatarUrl": "string",
  "city": "string",
  "region": "string",
  "bio": "string",
  "memberSince": "2026-01-01",
  "ratingAvg": 4.8,
  "ratingCount": 23
}
```

### 4.3 Game Library

Each user has a library of entries. An entry represents one **copy** of a game, identified by the combination of **game + platform + format**. The user **must** select the platform (and format) when adding a game. The same game can appear multiple times in a user's library as long as each entry differs in platform or format — e.g. *Elden Ring / PS5 / physical* (owned) and *Elden Ring / PC / digital* (wishlist) are two separate entries.

Each entry tracks all statuses for that copy. It stores only references (`gameId`, `platformId`) — game title, cover, release date, score and platform names are read at query time by joining the local catalog replica (`catalog_games`, `catalog_platforms`, see §5), so they always reflect the latest catalog data. `isOwned` and `isWishlist` are independent flags and **may both be `true` on the same entry** (e.g. the user owns the game but also keeps it on the wishlist).

**Entry uniqueness:** `(userId, gameId, platformId, format)` must be unique. Attempting to create (or update an entry into) a combination that already exists returns `409 Conflict` with the existing `entryId` in the problem details.

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/users/me/library` | Bearer | Own library (filterable by status, `gameId`, `platformId`, `format`) |
| `GET` | `/users/me/library/statuses` | Bearer | Batch status lookup for a list of game IDs |
| `GET` | `/users/{id}/library` | None | Public library entries (only active listings) |
| `POST` | `/users/me/library` | Bearer | Add a game copy (game + platform + format) to library |
| `PUT` | `/users/me/library/{entryId}` | Bearer | Update statuses, platform/format, or listing details |
| `DELETE` | `/users/me/library/{entryId}` | Bearer | Remove a single entry (copy) from library |

#### `GET /users/me/library/statuses` — Query Parameters & Response

Used by the frontend to annotate game cards with owned/wishlist tags without loading the full library. Called once per page of games with the visible game IDs.

Query: `?gameIds=uuid1,uuid2,uuid3` (comma-separated, max 50 IDs per request)

Response:
```json
{
  "uuid1": {
    "isOwned": true,
    "isWishlist": true,
    "entries": [
      { "entryId": "uuid", "platformId": "uuid", "platformName": "PlayStation 5", "platformSlug": "playstation5", "format": "physical", "isOwned": true,  "isWishlist": false },
      { "entryId": "uuid", "platformId": "uuid", "platformName": "PC",            "platformSlug": "pc",           "format": "digital",  "isOwned": false, "isWishlist": true  }
    ]
  },
  "uuid2": {
    "isOwned": true,
    "isWishlist": true,
    "entries": [
      { "entryId": "uuid", "platformId": "uuid", "platformName": "PlayStation 5", "platformSlug": "playstation5", "format": "digital", "isOwned": true, "isWishlist": true }
    ]
  },
  "uuid3": null
}
```

- Top-level `isOwned` / `isWishlist` are aggregates: `true` if **any** entry for that game has the flag set. Both can be `true` at the same time.
- `entries` lists every copy of the game in the user's library so the frontend can show which platform/format is owned or wishlisted.
- `null` means the game is not in the user's library at all.

The frontend uses this to render "Owned" and/or "Wishlist" badges (with platform labels) on the game card.

#### `POST /users/me/library` — Request

The frontend sends only identifiers and user-owned data. No game metadata is accepted from the client — it comes from the catalog replica.

```json
{
  "gameId": "uuid",
  "platformId": "uuid",
  "format": "physical",
  "isOwned": true,
  "isWishlist": true,
  "forExchange": false,
  "forSale": false,
  "wantToBuy": false,
  "condition": "like_new",
  "price": null,
  "preferredGames": null,
  "notes": "string"
}
```

Validation rules:
- `gameId` must exist in `catalog_games`. If it does not (the `catalog.game.upserted` event for a just-imported game has not been consumed yet) → `409 Conflict` with problem type `https://gamex.rs/errors/game-not-synced` and a `Retry-After: 2` header. The frontend retries automatically (see Frontend spec). This only happens in the first seconds after a game is imported from RAWG.
- `platformId` is **required** and must be one of the game's platforms in `catalog_game_platforms`; otherwise `400 Bad Request`.
- `format` is **required** (`physical` or `digital`) — it is part of the entry's identity, not only a listing detail.
- `isOwned` and `isWishlist` are independent; both may be `true`.
- If an entry with the same `(gameId, platformId, format)` already exists for the user → `409 Conflict`.
- `condition` is only allowed when `format = physical`.
- `condition`, `price`, `preferredGames`, and `notes` are only relevant when `forExchange` or `forSale` is true and apply to the listing created from this entry.

#### `PUT /users/me/library/{entryId}` — Request

Same shape as POST but all fields optional (partial update); `gameId` cannot be changed. `platformId` and `format` may be changed, subject to the same validation and uniqueness rules (`409` if the new combination already exists). Status changes are diffed against current state to compute the SQS delta event (see §6).

#### Library entry — Response shape

`GET /users/me/library`, `GET /users/{id}/library`, the listing endpoints, and the POST/PUT responses return entries with game and platform data joined from the replica:

```json
{
  "entryId": "uuid",
  "game": {
    "id": "uuid",
    "title": "Elden Ring",
    "coverImageUrl": "https://media.gamex.rs/game-covers/326243.jpg",
    "releaseDate": "2022-02-25",
    "metacritic": 96
  },
  "platform": { "id": "uuid", "name": "PlayStation 5", "slug": "playstation5" },
  "format": "physical",
  "isOwned": true,
  "isWishlist": true,
  "forExchange": false,
  "forSale": false,
  "wantToBuy": false,
  "condition": "like_new",
  "price": null,
  "preferredGames": null,
  "notes": "string",
  "listingStatus": "none"
}
```

### 4.4 Listings

Listings are derived views of library entries where `forExchange=true` or `forSale=true`. They are not a separate resource to create — they appear automatically when the library entry has those flags set.

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/listings` | None | Browse all active listings with filters |
| `GET` | `/listings/{id}` | None | Single listing detail |
| `GET` | `/users/{id}/listings` | None | Active listings by a specific user |
| `POST` | `/listings/{id}/complete` | Bearer | Mark listing transaction as complete |

#### `GET /listings` — Query Parameters

| Parameter | Type | Description |
|---|---|---|
| `type` | string | `exchange`, `sale`, `want_to_buy` |
| `gameId` | GUID | Filter by specific game |
| `platform` | string | Platform slug — resolved via `catalog_platforms` and matched against the entry's selected platform (`library_entries.platform_id`) |
| `q` | string | Game title search (partial match on `catalog_games.title`) |
| `format` | string | `physical`, `digital` |
| `condition` | string | `new`, `like_new`, `good`, `acceptable` |
| `city` | string | User's city |
| `minPrice` | decimal | Min price (sale listings) |
| `maxPrice` | decimal | Max price (sale listings) |
| `cursor` | string | Pagination cursor |
| `pageSize` | int | Default 20, max 50 |

#### `POST /listings/{id}/complete`

Both parties must call this endpoint. When both have called it, the transaction is marked complete and a `transaction.completed` event is published to SQS.

---

## 5. Data Model

### `users`

| Column | Type | Notes |
|---|---|---|
| `id` | CHAR(36) PK | UUID |
| `display_name` | VARCHAR(100) | Not null |
| `email` | VARCHAR(255) UNIQUE | Not null |
| `password_hash` | VARCHAR(500) | Null for OAuth-only accounts |
| `avatar_url` | VARCHAR(500) | |
| `city` | VARCHAR(100) | |
| `region` | VARCHAR(100) | |
| `country` | CHAR(2) | Default `RS` |
| `bio` | TEXT | |
| `rating_avg` | DECIMAL(3,2) | Denormalized; updated on new rating |
| `rating_count` | INT | Denormalized |
| `is_active` | BOOLEAN | Default true; false = soft deleted |
| `created_at` | DATETIME | |
| `updated_at` | DATETIME | |

### `user_platforms`

Stores a user's platform preferences. Each row is one platform the user is interested in. Platform IDs are shared identifiers with the Game Catalog Service (`platforms` table). Name and slug are read by joining `catalog_platforms` (local replica).

| Column | Type | Notes |
|---|---|---|
| `user_id` | CHAR(36) FK → users.id | |
| `platform_id` | CHAR(36) FK → catalog_platforms.id | Reference to Game Catalog `platforms.id` via the local replica |
| `created_at` | DATETIME | |
| PK | `(user_id, platform_id)` | |

`PUT /users/me/platforms` replaces the full set for the user — delete all existing rows for the user, insert the new set within a single transaction. Unknown platform IDs return `400`.

#### `GET /users/me/platforms` — Response

```json
[
  { "platformId": "uuid", "name": "PlayStation 5", "slug": "playstation5" },
  { "platformId": "uuid", "name": "PC",             "slug": "pc"           }
]
```

#### `PUT /users/me/platforms` — Request

```json
{
  "platformIds": ["uuid", "uuid"]
}
```

An empty `platformIds` array clears all preferences (user sees all games, unfiltered).

---

### `oauth_providers`

| Column | Type | Notes |
|---|---|---|
| `id` | CHAR(36) PK | |
| `user_id` | CHAR(36) FK → users.id | |
| `provider` | ENUM(`google`,`facebook`) | |
| `provider_uid` | VARCHAR(255) | |
| UNIQUE | `(provider, provider_uid)` | |

### `refresh_tokens`

| Column | Type | Notes |
|---|---|---|
| `id` | CHAR(36) PK | |
| `user_id` | CHAR(36) FK → users.id | |
| `token_hash` | VARCHAR(500) | SHA-256 hash of the raw token |
| `expires_at` | DATETIME | |
| `revoked_at` | DATETIME | Null = still valid |

### `library_entries`

One row per user + game + platform + format (one "copy"). The same game can appear multiple times per user when platform or format differ; statuses are boolean flags on the row. `is_owned` and `is_wishlist` are independent and may both be true.

| Column | Type | Notes |
|---|---|---|
| `id` | CHAR(36) PK | |
| `user_id` | CHAR(36) FK → users.id | |
| `game_id` | CHAR(36) FK → catalog_games.id | Game Catalog `games.id`, via the local replica |
| `platform_id` | CHAR(36) FK → catalog_platforms.id | **Not null.** Platform the user has / wants this copy on |
| `format` | ENUM(`physical`,`digital`) | **Not null.** Part of the entry identity |
| `is_owned` | BOOLEAN | Default false |
| `is_wishlist` | BOOLEAN | Default false |
| `for_exchange` | BOOLEAN | Default false |
| `for_sale` | BOOLEAN | Default false |
| `want_to_buy` | BOOLEAN | Default false |
| `condition` | ENUM(`new`,`like_new`,`good`,`acceptable`) | Physical only |
| `price` | DECIMAL(10,2) | For sale listings |
| `preferred_games` | TEXT | For exchange listings — free text |
| `notes` | TEXT | |
| `listing_status` | ENUM(`none`,`active`,`completed`,`expired`) | Default `none` |
| `listing_expires_at` | DATETIME | Set when for_exchange or for_sale = true |
| `created_at` | DATETIME | |
| `updated_at` | DATETIME | |
| UNIQUE | `(user_id, game_id, platform_id, format)` | One entry per user + game + platform + format |
| INDEX | `(user_id, game_id)` | Batch status lookup / aggregate delta calculation |
| INDEX | `(platform_id, listing_status)` | Listing platform filter |

### Catalog replica tables

Read-only local copy of the Game Catalog Service data this service needs. Written **only** by the `gamex-catalog-events` consumer (§6) — never by API requests. Contains every game the catalog has imported (not the whole RAWG database), and only the fields needed to render library entries and listings; description, genres and stats stay in the Game Catalog Service.

#### `catalog_games`

| Column | Type | Notes |
|---|---|---|
| `id` | CHAR(36) PK | Same ID as Game Catalog `games.id` |
| `title` | VARCHAR(255) | Not null |
| `cover_image_url` | VARCHAR(500) | Cloudflare CDN URL |
| `release_date` | DATE | |
| `metacritic` | SMALLINT | |
| `source_updated_at` | DATETIME(6) | Game Catalog `games.updated_at` from the event — used to discard stale events |
| `synced_at` | DATETIME | When this row was last written by the consumer |
| INDEX | `(title)` | Listing title search |

#### `catalog_platforms`

| Column | Type | Notes |
|---|---|---|
| `id` | CHAR(36) PK | Same ID as Game Catalog `platforms.id` |
| `name` | VARCHAR(100) | e.g. `PlayStation 5` |
| `slug` | VARCHAR(100) UNIQUE | e.g. `playstation5` |
| `source_updated_at` | DATETIME(6) | |

#### `catalog_game_platforms`

| Column | Type | Notes |
|---|---|---|
| `game_id` | CHAR(36) FK → catalog_games.id | |
| `platform_id` | CHAR(36) FK → catalog_platforms.id | |
| PK | `(game_id, platform_id)` | |

Used to validate that a library entry's platform is one the game is available on.

### `listing_completions`

Tracks which parties have confirmed a transaction as complete.

| Column | Type | Notes |
|---|---|---|
| `id` | CHAR(36) PK | |
| `library_entry_id` | CHAR(36) FK → library_entries.id | |
| `user_id` | CHAR(36) FK → users.id | The user who confirmed |
| `confirmed_at` | DATETIME | |
| UNIQUE | `(library_entry_id, user_id)` | |

### `user_ratings`

| Column | Type | Notes |
|---|---|---|
| `id` | CHAR(36) PK | |
| `rater_id` | CHAR(36) FK → users.id | |
| `ratee_id` | CHAR(36) FK → users.id | |
| `library_entry_id` | CHAR(36) FK → library_entries.id | |
| `score` | TINYINT | 1–5 |
| `comment` | TEXT | |
| `created_at` | DATETIME | |
| UNIQUE | `(rater_id, library_entry_id)` | One rating per transaction per user |

---

## 6. SQS Events

This service publishes to two SQS queues and consumes one.

### Consumed queue: `gamex-catalog-events`

Published by the Game Catalog Service. Keeps the catalog replica tables up to date. Consumer is a background `IHostedService` with long polling (20s), up to 3 receives before the message moves to `gamex-catalog-events-dlq`.

| Event type | Action |
|---|---|
| `catalog.platform.upserted` | Upsert `catalog_platforms` row |
| `catalog.game.upserted` | Upsert `catalog_games` row and replace its `catalog_game_platforms` rows (single transaction) |

Payloads are defined in the Game Catalog Service spec (§6).

**Idempotency & ordering:** an upsert is applied only if the event's `updatedAt` is newer than the stored `source_updated_at`; otherwise the message is acknowledged and ignored. Duplicates and out-of-order delivery are therefore harmless.

**Unknown platform:** if a `catalog.game.upserted` event references a platform ID not yet in `catalog_platforms`, the message is not acknowledged (redelivered after the visibility timeout) so the platform event can arrive first; after 3 receives it goes to the DLQ.

**Initial fill / recovery:** on first deploy, or after DLQ incidents, an admin triggers the Game Catalog Service's replay endpoint (`POST /admin/catalog-events/replay`), which republishes all platforms and games. Because upserts are idempotent, replaying is always safe.

**Platform removed from a game:** platform changes on a game (e.g. a platform removed from a game in the catalog) do **not** delete existing library entries — entries keep their `platform_id`; the platform is only enforced when creating or changing an entry.

### Published queues

### Queue: `gamex-library-events`

Published whenever a library entry's status flags change (create, update, or delete).

**Event: `library.game.status.changed`**

```json
{
  "eventType": "library.game.status.changed",
  "gameId": "uuid",
  "userId": "uuid",
  "delta": {
    "owned": 0,
    "wishlist": 1,
    "forExchange": 0,
    "forSale": 0,
    "wantToBuy": 0
  },
  "occurredAt": "2026-05-12T10:00:00Z"
}
```

Game Catalog stats count **users**, not copies (e.g. "142 users own this"). Because a user can have several entries for the same game (different platform/format), the delta is computed on the **user-game aggregate**, not on the single entry:

1. Before the write, compute the aggregate flags for `(user_id, game_id)` across all the user's entries for that game — a flag is `true` if **any** entry has it set.
2. Apply the create / update / delete.
3. Recompute the aggregate flags for `(user_id, game_id)`.
4. Each flag that went `false → true` contributes `+1`; each flag that went `true → false` contributes `-1`; unchanged flags contribute `0`.
5. If all deltas are `0`, no event is published.

Examples:
- User owns *Elden Ring / PS5 / physical* and adds *Elden Ring / PC / digital* as owned → aggregate `owned` was already `true` → no `owned` delta.
- User deletes their only owned copy of a game → `owned: -1`.
- User sets both `isOwned` and `isWishlist` on a new entry for a game not yet in the library → `owned: +1, wishlist: +1`.

Steps 1–3 run inside the same database transaction as the write.

### Queue: `gamex-activity-events`

Published for events the Notification Service needs to act on.

| Event type | When | Key payload fields |
|---|---|---|
| `listing.created` | Entry transitions to `for_exchange` or `for_sale` | `listingId`, `gameId`, `userId`, `type`, `platformId`, `platformSlug`, `format` |

**Wishlist matching:** when resolving wishlist matches for a new listing, a user matches only if they have an entry with `is_wishlist = true` for the same `game_id` **and** the same `platform_id` as the listing. Format is not required to match.
| `listing.expired` | Background job marks listing as expired | `listingId`, `userId` |
| `transaction.completed` | Both parties confirm | `listingEntryId`, `userIdA`, `userIdB` |

---

## 7. Background Jobs

| Job | Schedule | Action |
|---|---|---|
| Expire listings | Every hour | Sets `listing_status=expired` on entries where `listing_expires_at < NOW()` and status is `active`; publishes `listing.expired` events |

---

## 8. Local Development

```yaml
# docker-compose.yml
services:
  user-library-svc:
    build: .
    ports:
      - "5002:8080"
    environment:
      - DB_CONNECTION_STRING=Server=library-db;Database=library_db;User=root;Password=localdev;
      - JWT_PRIVATE_KEY_PATH=/run/secrets/jwt_private_key
      - JWT_PUBLIC_KEY_PATH=/run/secrets/jwt_public_key
      - GOOGLE_CLIENT_ID=${GOOGLE_CLIENT_ID}
      - GOOGLE_CLIENT_SECRET=${GOOGLE_CLIENT_SECRET}
      - FACEBOOK_APP_ID=${FACEBOOK_APP_ID}
      - FACEBOOK_APP_SECRET=${FACEBOOK_APP_SECRET}
      - SQS_LIBRARY_EVENTS_URL=http://localstack:4566/000000000000/gamex-library-events
      - SQS_ACTIVITY_EVENTS_URL=http://localstack:4566/000000000000/gamex-activity-events
      - SQS_CATALOG_EVENTS_URL=http://localstack:4566/000000000000/gamex-catalog-events
      - FRONTEND_BASE_URL=http://localhost:5173
    depends_on:
      - library-db

  library-db:
    image: mysql:8.0
    environment:
      MYSQL_DATABASE: library_db
      MYSQL_ROOT_PASSWORD: localdev
    ports:
      - "3308:3306"
    volumes:
      - library-db-data:/var/lib/mysql

volumes:
  library-db-data:
```

---

## 9. CI/CD

| Workflow | Trigger | Action |
|---|---|---|
| `ci.yml` | Push / PR | Build + unit + integration tests |
| `deploy-dev.yml` | Merge to `main` | Build image → ECR → deploy to `gx-dev` ECS |
| `deploy-prod.yml` | Tag `v*.*.*` | Manual approval → ECR → deploy to `gx-prod` ECS |

---

## 10. Environment Variables Reference

| Variable | Description | Source (AWS) |
|---|---|---|
| `DB_CONNECTION_STRING` | MySQL connection string | Secrets Manager |
| `JWT_PRIVATE_KEY` | RS256 private key (PEM) | Secrets Manager |
| `JWT_PUBLIC_KEY` | RS256 public key (PEM) | SSM Parameter Store |
| `GOOGLE_CLIENT_ID` | Google OAuth client ID | Secrets Manager |
| `GOOGLE_CLIENT_SECRET` | Google OAuth client secret | Secrets Manager |
| `FACEBOOK_APP_ID` | Facebook app ID | Secrets Manager |
| `FACEBOOK_APP_SECRET` | Facebook app secret | Secrets Manager |
| `SQS_LIBRARY_EVENTS_URL` | Queue URL for game stats updates | SSM Parameter Store |
| `SQS_ACTIVITY_EVENTS_URL` | Queue URL for notification events | SSM Parameter Store |
| `SQS_CATALOG_EVENTS_URL` | Queue URL to consume catalog replica events | SSM Parameter Store |
| `FRONTEND_BASE_URL` | Used for OAuth redirect URIs | SSM Parameter Store |
| `ASPNETCORE_ENVIRONMENT` | `Development` / `Production` | ECS task definition |
