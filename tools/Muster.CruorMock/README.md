# Muster.CruorMock

A C# (ASP.NET Core minimal API) test double for the external **Cruor** loot/economy
platform that Muster's currency connector pushes to. It implements the published
Cruor OpenAPI (currency + auctions) over an **in-memory store** so you can drive the
Muster stack end-to-end without the real service.

- **Part of the Aspire run-mode stack, never published.** The AppHost builds it from
  its Dockerfile and runs it as the `cruor-mock` **container** in run mode only (like
  the SQL / Service Bus emulators), and skips it entirely in publish — see
  `AddMusterCruorMock` in `aspire/Muster.AppHost`.
- **SQLite-backed, persisted across restarts.** State lives in a SQLite file
  (`CRUOR_DB_PATH`, default `/data/cruor.db` in the container). The AppHost mounts the
  `cruor-mock-data` docker volume at `/data`, so data survives container restarts.
  Schema is created on boot (`EnsureCreated`, no migrations). Delete the volume to reset.
- **Auth.** Every endpoint (except `/`) requires the `x-api-key` header, matching the
  real contract. Default key is `test-key` (override via the `API_KEY` env var).

## Run via Aspire (default)

```pwsh
dotnet run --project aspire/Muster.AppHost
```

The mock comes up as the `cruor-mock` resource. Open it from the Aspire dashboard
(its endpoint links to `/ui`) or directly at **http://localhost:8081/ui**. Local
web/bot processes reach it at `http://localhost:8081`.

## Run standalone (without the rest of the stack)

```pwsh
dotnet run --project tools/Muster.CruorMock   # http://localhost:8080 (SQLite next to the app)
# or containerized, from the repo root (named volume persists the SQLite DB):
docker build -t cruor-mock -f tools/Muster.CruorMock/Dockerfile .
docker run --rm -p 8080:8080 -e API_KEY=test-key -v cruor-mock-data:/data cruor-mock
```

| Path | What |
| --- | --- |
| `/ui` | Basic UI — apply cruor (+/-), items, auctions, bids |
| `/`   | Health/info JSON |

## Wiring Muster to it

Point a currency's **HTTP API** connector at this host:

- **Endpoint:** `http://localhost:8081/currency/add-cruor` (Aspire run mode). Use
  `host.docker.internal` instead of `localhost` if the *caller* is itself in Docker.
- **Auth header:** custom header name `x-api-key`, secret `test-key`
- **Body template:**

  ```json
  { "member_id": "$userId", "display_name": "$displayName", "cruor_amount": "$amount" }
  ```

`$amount` is signed, so debits arrive as a negative `cruor_amount` — the mock applies
the value directly (`balance += cruor_amount`), so `+` credits and `-` debits both work.

## Endpoints (mirrors the OpenAPI)

**Currency**
- `POST /currency/add-cruor` — `{member_id, display_name, cruor_amount}` (signed)
- `GET  /currency/balance/{user_id}`

**Auctions**
- `POST /auctions/add-item` — `{name, description, quantity, holder_id}`
- `GET  /auctions/items`
- `POST /auctions/add-auction` — `{name, description, item_id}`
- `GET  /auctions/active` · `/auctions/awarded` · `/auctions/unscheduled`
- `POST /auctions/start-auction` — `{auction_id, duration_minutes}`
- `POST /auctions/close-auction` — `{auction_id}` (awards to highest bidder, transfers item)
- `POST /auctions/place-bid` — `{user_id, auction_id, amount}`
- `GET  /auctions/{auction_id}/bids`

Auction lifecycle is derived from timestamps: **unscheduled** (created) →
**active** (started, before close) → **awarded** (closed).
