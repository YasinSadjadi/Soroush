# Local mediator observability platform

This loopback-only development stack provides an OTLP Collector, Loki, Tempo,
Prometheus and a provisioned Grafana dashboard. Versioned files define the full
setup. Collector queues use persistent named-volume storage; service containers
run without root after a small volume-ownership initialization.

Start Docker's Linux engine, then run from the repository root in PowerShell:

```powershell
$env:GRAFANA_ADMIN_PASSWORD = 'choose-a-local-password'
docker compose -f observability/compose.yaml config --quiet
docker compose -f observability/compose.yaml up -d
python observability/smoke.py
$env:OTEL_EXPORTER_OTLP_ENDPOINT = 'http://localhost:4317'
dotnet run --project samples/Soroush.Diagnostics.Demo -c Release
python observability/smoke.py --data
```

Open [Grafana](http://localhost:3000/d/soroush-mediator) and sign in as admin using
the chosen password. The Soroush folder contains latency, throughput, outcomes,
active operations, stage and stream timing, callback failure and Collector panels.
Loki log TraceID fields link to Tempo; Prometheus exemplars link to traces.

Changing the environment password does not reset credentials in an already
initialized Grafana volume. Stop services with
`docker compose -f observability/compose.yaml down`; this preserves data volumes.

Prometheus is at [localhost:9090](http://localhost:9090), Loki at
[localhost:3100](http://localhost:3100), Tempo at
[localhost:3200](http://localhost:3200), and Collector health at
[localhost:13133](http://localhost:13133).

## Data and retention

Logs use Loki's native OTLP endpoint with structured metadata. Only service and
environment Resource attributes become index labels; trace/user/operation IDs do
not. Logs are kept for 14 days, traces for 7 days, and metrics for 30 days, subject
to available volume capacity. These are starting policies, not storage guarantees.
The Collector's Prometheus exporter uses underscore names and unit suffixes,
which the supplied dashboard and alert rules expect.

Collector self-metrics are scraped separately. Memory limiting precedes batching;
log and trace exporters have bounded persistent queues and bounded retries.
The sample redaction processor removes known secret/payload attribute keys; it
does not sanitize arbitrary log bodies or replace application redaction.

Prometheus loads three example rules: sustained unexpected mediator failures with
a minimum traffic count, unavailable Collector, and telemetry delivery failures.
They appear in the Prometheus Alerts UI. Routing notifications requires configuring
the deployment's Alertmanager; this repository does not send notifications.
Technical failure rules exclude cancelled/abandoned operations and count only
mediator roots. Choose latency thresholds per request/SLO using measured workload.
Alert fixtures can be checked with:

```powershell
docker run --rm --entrypoint /bin/promtool -v "${PWD}/observability:/rules:ro" -w /rules prom/prometheus:v3.16.0 test rules alerts.test.yaml
```

## Sampling and production

The demo samples all development traces. Set OTEL_TRACES_SAMPLER_ARG between 0 and
1 to choose the root sampling ratio; parent decisions are preserved. For example,
0.1 is a possible starting production ratio, not a universal requirement.
Metrics and failure summaries do not depend on trace sampling.

Keeping every error trace requires an appropriate end-to-end policy. Tail sampling
cannot recover spans discarded at the head; all relevant spans must reach its
Collector, with TraceId affinity, bounded buffering and timeouts suitable for
long-lived streams. The development configuration intentionally does not deploy
a tail sampler.

Production must supply TLS/authenticated ingress, backend identity and tenant
authorization, secret management, persistent capacity, backups and access controls.
The sample backend authentication settings are intended for its loopback-only
development network. Do not expose these ports as a production deployment.
Observe queue occupancy, refused/dropped data, WAL disk space and exporter failures
independently of application readiness. Configure SDK/exporter shutdown timeouts.
Telemetry is best effort; transactional business audit records belong in the
application's durable audit/outbox mechanism.

Use one delivery route for each log record. Shipping the same logs through both
OTLP and console collection into the same store produces duplicates.

Configuration references:

- [Collector configuration](https://opentelemetry.io/docs/collector/configuration/) and [resiliency](https://opentelemetry.io/docs/collector/resiliency/).
- [Loki native OTLP](https://grafana.com/docs/loki/latest/send-data/otel/).
- [Tempo configuration](https://grafana.com/docs/tempo/latest/configuration/).
- [Grafana provisioning](https://grafana.com/docs/grafana/latest/administration/provisioning/).
