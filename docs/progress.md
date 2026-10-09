# Progress

## M0 — Skeleton
- Monorepo layout per SPEC section 5; .NET 10 solution and Vite app compile.
- `docker compose up`: postgres, api, web.
- GitHub Actions CI for api and web.

## M1 — Data
- 10 entities, initial migration, DB-enforced invariants (unique idempotency key, slot capacity CHECKs, atomic `TryReserveAsync`).
- Idempotent seed: 3 businesses, 28 offerings (v1 items ported + invented extras, see `docs/seed-notes.md`), rolling 14-day slots.
- Tests (Testcontainers): invariants, concurrency on last slot, seeder idempotency.
- Pending: persona prompts hold business facts until a Business column exists; seeder never updates existing rows.
