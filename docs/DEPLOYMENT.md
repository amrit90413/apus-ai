# Deployment

## Local
See the README. `docker compose up --build` brings up the full stack.

## Database migrations
The C# entities + `infra/db/postgres-init.sql` define the schema. For production, generate
EF Core migrations rather than relying on the init SQL:

```bash
cd backend/src/Gateway.Api
dotnet tool install --global dotnet-ef
dotnet ef migrations add Initial
dotnet ef database update
```

Apply the ClickHouse schema once per cluster:

```bash
clickhouse-client --multiquery < infra/db/clickhouse-init.sql
```

## Kubernetes
1. `kubectl apply -f infra/k8s/namespace.yaml`
2. Create the real secret (do NOT use the example file):
   ```bash
   kubectl -n ai-gateway create secret generic gateway-secrets \
     --from-literal=Jwt__SigningKey="$(openssl rand -base64 48)" \
     --from-literal=Anthropic__ApiKey="sk-ant-..." \
     --from-literal=ConnectionStrings__Postgres="..." \
     --from-literal=ConnectionStrings__Redis="redis:6379" \
     --from-literal=ConnectionStrings__RabbitMq="amqp://..."
   ```
3. `kubectl apply -f infra/k8s/gateway-deployment.yaml`
4. `kubectl apply -f infra/k8s/worker-deployment.yaml`
5. `kubectl apply -f infra/k8s/ingress.yaml`

Managed Postgres/Redis/RabbitMQ/ClickHouse are recommended over in-cluster stateful sets
for production.

## Observability
- `/metrics` exposes Prometheus metrics; scrape with a ServiceMonitor.
- OTLP traces export to your collector (set `OTEL_EXPORTER_OTLP_ENDPOINT`).
- `/health/live` and `/health/ready` back the K8s probes.

## Extension checklist (left as clearly-marked TODOs)
- [ ] OpenAI provider — mirror `AnthropicProvider`, register in `Program.cs`.
- [ ] Web login route that sets an httpOnly cookie for the dashboard.
- [ ] Billing consumer on the `usage.billing` queue (already declared + bound).
- [ ] Admin write endpoints for editing quota policy JSON.
- [ ] Anomaly detector: a worker that reads audit logs / session IPs and raises alerts.
