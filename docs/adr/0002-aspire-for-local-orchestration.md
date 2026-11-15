# 0002 - .NET Aspire AppHost for the local environment

**Status:** Accepted
**Date:** 2026-11-15

## Context

Running Plaza locally means five services, PostgreSQL and a Kafka-compatible broker, wired
together with connection strings, and a place to look at traces that cross Kafka. This has to
work with one command and no manual steps.

## Decision

Use a .NET Aspire AppHost (`src/Plaza.AppHost`) for local development only:

- It starts PostgreSQL and Redpanda as containers (images pinned by digest) and the five
  services as processes, and injects connection strings and service URLs.
- Its dashboard receives OTLP from every service, so a trace of one order can be followed
  through HTTP and Kafka.
- Services do not depend on Aspire. They read plain configuration
  (`ConnectionStrings:*`, `OTEL_EXPORTER_OTLP_ENDPOINT`) and use ordinary `Npgsql` and
  `Confluent.Kafka` clients, so they run the same way under tests and in Kubernetes.

Integration tests use Testcontainers directly, not the AppHost, so that tests control the
broker (stop it, restart it) and do not pay for the dashboard.

## Consequences

- One command (`dotnet run --project src/Plaza.AppHost`) gives a working system with tracing.
- Aspire is a local tool here. Deployment manifests are written by hand with Kustomize
  (phase 6); nothing is generated from the AppHost.
- The AppHost package pulls platform-specific binaries, so it is excluded from NuGet lock files.
- Aspire releases often; the AppHost must be kept current (Dependabot group `aspire`).

## Alternatives considered

- **docker-compose:** well known and enough to start containers, but every service would need a
  Dockerfile from day one, the feedback loop is slower (image rebuilds), and it gives no trace
  viewer without adding and configuring a collector and Jaeger or Grafana.
- **Scripts that start each process:** no dependency ordering, no dashboard, and a manual step
  per service.
- **Aspire client integrations inside the services** (`Aspire.Npgsql`, `Aspire.Confluent.Kafka`):
  convenient, but they couple the services to Aspire for configuration and health checks that are
  a few lines to write explicitly.
