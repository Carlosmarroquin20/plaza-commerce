# 0001 - Monorepo with five deployable services, one project per service

**Status:** Accepted
**Date:** 2026-11-15

## Context

Plaza has to show event-driven communication between independently deployable services
(Catalog, Inventory, Orders, Payments, Notifications). It is built and maintained by one person,
so every extra project, repository or layer is a cost with no team to share it.

## Decision

- One Git repository and one solution (`Plaza.slnx`).
- Each service is a single ASP.NET Core project (Minimal API) organised by feature, with its own
  `DbContext`, its own PostgreSQL database and its own container image.
- Services never read another service's database. They talk through Kafka events
  (`Plaza.Contracts`) or internal REST calls.
- Shared code is limited to three libraries: `Plaza.Contracts` (message types),
  `Plaza.Messaging` (outbox, inbox, consumer) and `Plaza.ServiceDefaults` (telemetry, health).

## Consequences

- A change that spans services is one PR and one CI run.
- Service boundaries are enforced by project references and separate databases, not by
  repositories; a careless reference between services would compile. Code review has to catch it.
- A shared library change redeploys every service. Acceptable while the libraries are small and
  stable; revisit if `Plaza.Messaging` starts changing weekly.

## Alternatives considered

- **Modular monolith:** simplest to run, and the honest choice for a real shop of this size. It
  was rejected because the purpose of the project is to exercise the failure modes that only
  appear with a broker between processes (duplicates, reordering, partial failure).
- **Clean Architecture with four projects per service (Api, Application, Domain,
  Infrastructure):** twenty projects for five small services. The layering adds indirection
  without a second implementation of anything to justify it.
- **One repository per service:** needs a package feed for contracts and coordinated releases;
  pure overhead for a single maintainer.
