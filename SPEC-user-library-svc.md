# Gamex — User Library Service Specification

> **Repository:** `gamex-rs/gamex-user-library-svc`  
> **Version:** 1.0  
> **Date:** 2026-05-12

---

## 1. Purpose

The User Library Service is the identity provider and user-data owner for Gamex. It is responsible for:

- User registration, authentication, and JWT issuance
- User profiles
- Each user's personal game library (owned, wishlist, for exchange, for sale, want to buy)
- Public listings derived from library entries
- User-to-user ratings after completed transactions
- Publishing `library.game.status.changed` events to SQS so the Game Catalog Service can maintain aggregate stats

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

`platformIds` is optional at registration — the user can skip platform selection and set it later from profile settings. Platform IDs reference the `platforms` table in the Game Catalog Service; the frontend provides them after fetching `GET /api/v1/platforms` from the Game Catalog Service.

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

Each user has a library of entries, one per game. An entry tracks all statuses for that user-game pair and stores a snapshot of key game metadata.

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/users/me/library` | Bearer | Own library (filterable by status) |
| `GET` | `/users/me/library/statuses` | Bearer | Batch status lookup for a list of game IDs |
| `GET` | `/users/{id}/library` | None | Public library entries (only active listings) |
| `POST` | `/users/me/library` | Bearer | Add game to library |
| `PUT` | `/users/me/library/{entryId}` | Bearer | Update statuses or listing details |
| `DELETE` | `/users/me/library/{entryId}` | Bearer | Remove game from library |

#### `GET /users/me/library/statuses` — Query Parameters & Response

Used by the frontend to annotate game cards with owned/wishlist tags without loading the full library. Called once per page of games with the visible game IDs.

Query: `?gameIds=uuid1,uuid2,uuid3` (comma-separated, max 50 IDs per request)

Response:
```json
{
  "uuid1": { "isOwned": true,  "isWishlist": false },
  "uuid2": { "isOwned": false, "isWishlist": true  },
  "uuid3": null
}
```

`null` means the game is not in the user's library at all. The frontend uses this to render an "Owned" or "Wishlist" badge on the game card.

#### `POST /users/me/library` — Request

The frontend provides both the `gameId` and the denormalized game snapshot. The snapshot is captured at the moment the user adds the game so the library entry is self-contained and does not depend on the Game Catalog Service at read time.

```json
{
  "gameId": "uuid",
  "gameSnapshot": {
    "title": "string",
    "releaseDate": "2024-11-12",
    "score": 90,
    "platforms": ["PS5", "PC"],
    "coverImageUrl": "string"
  },
  "isOwned": true,
  "isWishlist": false,
  "forExchange": false,
  "forSale": false,
  "wantToBuy": false,
  "format": "physical",
  "condition": "like_new",
  "price": null,
  "preferredGames": null,
  "notes": "string"
}
```

`format`, `condition`, `price`, `preferredGames`, and `notes` are only relevant when `forExchange` or `forSale` is true and apply to the listing created from this entry.

#### `PUT /users/me/library/{entryId}` — Request

Same shape as POST but all fields optional (partial update). Status changes are diffed against current state to compute the SQS delta event.

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
| `platform` | string | Platform slug |
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

Stores a user's platform preferences. Each row is one platform the user is interested in. Platform IDs are shared identifiers with the Game Catalog Service (`platforms` table) — no foreign key across services.

| Column | Type | Notes |
|---|---|---|
| `user_id` | CHAR(36) FK → users.id | |
| `platform_id` | CHAR(36) | Reference to Game Catalog `platforms.id` |
| `platform_name` | VARCHAR(100) | Denormalized snapshot of platform name |
| `platform_slug` | VARCHAR(100) | Denormalized snapshot of platform slug |
| `created_at` | DATETIME | |
| PK | `(user_id, platform_id)` | |

The `platform_name` and `platform_slug` columns are denormalized so the User Library Service can return platform preferences without calling the Game Catalog Service. They are set at insert time from data provided by the frontend and are not updated if the platform name changes (platform names are stable).

`PUT /users/me/platforms` replaces the full set for the user — delete all existing rows for the user, insert the new set within a single transaction.

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
  "platforms": [
    { "platformId": "uuid", "name": "PlayStation 5", "slug": "playstation5" },
    { "platformId": "uuid", "name": "PC",             "slug": "pc"           }
  ]
}
```

An empty `platforms` array clears all preferences (user sees all games, unfiltered).

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

One row per user-game pair. A game can only appear once per user; statuses are boolean flags on the same row.

| Column | Type | Notes |
|---|---|---|
| `id` | CHAR(36) PK | |
| `user_id` | CHAR(36) FK → users.id | |
| `game_id` | CHAR(36) | Reference to Game Catalog (no FK across services) |
| `game_title` | VARCHAR(255) | Snapshot from frontend at creation |
| `game_release_date` | DATE | Snapshot |
| `game_score` | DECIMAL(4,1) | Snapshot (metacritic / RAWG score) |
| `game_platforms` | JSON | Snapshot e.g. `["PS5","PC"]` |
| `game_cover_url` | VARCHAR(500) | Snapshot (Cloudflare CDN URL) |
| `is_owned` | BOOLEAN | Default false |
| `is_wishlist` | BOOLEAN | Default false |
| `for_exchange` | BOOLEAN | Default false |
| `for_sale` | BOOLEAN | Default false |
| `want_to_buy` | BOOLEAN | Default false |
| `format` | ENUM(`physical`,`digital`) | Null if no active listing |
| `condition` | ENUM(`new`,`like_new`,`good`,`acceptable`) | Physical only |
| `price` | DECIMAL(10,2) | For sale listings |
| `preferred_games` | TEXT | For exchange listings — free text |
| `notes` | TEXT | |
| `listing_status` | ENUM(`none`,`active`,`completed`,`expired`) | Default `none` |
| `listing_expires_at` | DATETIME | Set when for_exchange or for_sale = true |
| `created_at` | DATETIME | |
| `updated_at` | DATETIME | |
| UNIQUE | `(user_id, game_id)` | One entry per user-game pair |

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

## 6. SQS Event Publishing

This service publishes to two SQS queues.

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

Delta calculation rules:
- On **create**: each `true` flag contributes `+1` to its field
- On **update**: diff old flags vs new flags; changed flags contribute `+1` or `-1`
- On **delete**: each `true` flag contributes `-1`

### Queue: `gamex-activity-events`

Published for events the Notification Service needs to act on.

| Event type | When | Key payload fields |
|---|---|---|
| `listing.created` | Entry transitions to `for_exchange` or `for_sale` | `listingId`, `gameId`, `userId`, `type` |
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
| `FRONTEND_BASE_URL` | Used for OAuth redirect URIs | SSM Parameter Store |
| `ASPNETCORE_ENVIRONMENT` | `Development` / `Production` | ECS task definition |
