## Voting Worker

The voting worker is a .NET 7 background process. It removes vote messages from the Redis Sentinel `votes` list, writes them to PostgreSQL and creates the `votes` table when the database is first initialized. It has no public HTTP route.

The worker reads these runtime settings from the environment:

| Variable group | Variables |
|----------------|-----------|
| PostgreSQL | `DB_HOST`, `DB_PORT`, `DB_USERNAME`, `DB_PASSWORD`, `DB`, `DB_SSL_MODE` |
| Redis Sentinel | `REDIS_SENTINEL_HOST`, `REDIS_SENTINEL_PORT`, `REDIS_MASTER_NAME`, `REDIS_USER_NAME`, `REDIS_PASSWORD` |

The process reconnects to Redis or PostgreSQL when a connection is unavailable. It uses the Redis master name and Sentinel endpoints to find the active Redis master.

## Local Development

The project targets .NET 7 and uses `StackExchange.Redis`, `Npgsql` and `Newtonsoft.Json`. Restore and build it with:

```bash
dotnet restore
dotnet build
```

Run the worker after setting the PostgreSQL and Redis Sentinel variables:

```bash
dotnet run
```

The worker expects the database and Redis services to be reachable. A Compose-based local stack is also described by the vote app repository because the worker consumes the same Redis queue and writes to the same PostgreSQL data store.

## Container

The multi-stage Dockerfile builds for the target architecture and runs the published worker with the .NET 7 runtime as non-root UID/GID `999`:

```bash
docker build -t voting-worker .
docker run --rm \
	-e DB_HOST=${DB_HOST:-postgres} \
	-e DB_PORT=5432 \
	-e DB_USERNAME=${DB_USERNAME} \
	-e DB_PASSWORD=${DB_PASSWORD} \
	-e DB=${DB_NAME} \
	-e DB_SSL_MODE=Prefer \
	-e REDIS_SENTINEL_HOST=${REDIS_SENTINEL_HOST:-sentinel} \
	-e REDIS_SENTINEL_PORT=26379 \
	-e REDIS_MASTER_NAME=${REDIS_MASTER_NAME} \
	voting-worker
```

The default hostnames are local-development service names. Supply the service names, database name and credentials through the environment where the worker runs. Do not commit credentials to the repository.

## Delivery

The CI workflow builds an Amazon ECR image tagged with the source commit SHA:

```text
<account>.dkr.ecr.<region>.amazonaws.com/voting-worker:<git-sha>
```

After a merge to `main`, the CD workflow runs Updatecli and updates `apps/voting-worker/prod-values.yml` in the [`backend-gitops`](https://github.com/YOUR_GITHUB_ORG/backend-gitops) repository. See the [platform application guide](https://github.com/YOUR_GITHUB_ORG/aws-eks-gitops-argocd-terraform/blob/main/App/README.md) for the wider application workflow and the [backend GitOps repository](https://github.com/YOUR_GITHUB_ORG/backend-gitops) for the Kubernetes deployment configuration.
