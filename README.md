# Docker Swarm Webhook

[![Docker Hub](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fhub.docker.com%2Fv2%2Frepositories%2Fholosheep%2Fdocker-swarm-webhook%2Ftags%2F%3Fpage_size%3D1%26ordering%3Dlast_updated&query=%24.results%5B0%5D.name&label=Docker%20Hub&logo=docker&color=blue)](https://hub.docker.com/r/holosheep/docker-swarm-webhook)
[![Docker Pulls](https://img.shields.io/docker/pulls/holosheep/docker-swarm-webhook)](https://hub.docker.com/r/holosheep/docker-swarm-webhook)
[![Build & Push](https://github.com/AzureRok/DockerSwarmWebhook/actions/workflows/docker-publish.yml/badge.svg)](https://github.com/AzureRok/DockerSwarmWebhook/actions/workflows/docker-publish.yml)

A .NET webhook server for managing Docker Swarm services via HTTP calls. Inspired by [zazuko/swarm-webhook](https://github.com/zazuko/swarm-webhook) with added security key authentication and forced image re-pull on restart.

```bash
docker pull holosheep/docker-swarm-webhook:latest
```

## Features

- **Start / Stop / Restart** Swarm services through simple HTTP endpoints
- **Force update** on restart (`docker service update --force`) — re-pulls images even when only the `latest` tag changes
- **Azure-style security key** — authenticate requests via `?code=<key>` query parameter or `x-webhook-key` header
- **Label-based discovery** — only services with `swarm.webhook.enabled=true` are controllable
- Configurable replica count per service via labels

## Quick Start

### 1. Deploy with Docker Compose

```yaml
version: "3.8"

services:
  webhook:
    image: holosheep/docker-swarm-webhook:latest
    volumes:
      - "/var/run/docker.sock:/var/run/docker.sock"
    ports:
      - "3000:3000"
    environment:
      - SERVER_HOST=0.0.0.0
      - SERVER_PORT=3000
      - WEBHOOK_SECRET_KEY=my-secret-key
    deploy:
      placement:
        constraints:
          - node.role == manager

  my-app:
    image: my-app:latest
    deploy:
      mode: replicated
      replicas: 0
      labels:
        - swarm.webhook.enabled=true
        - swarm.webhook.name=my-app
      restart_policy:
        condition: none
```

```bash
docker swarm init   # if not already a swarm
docker stack deploy -c docker-compose.yml my-stack
```

### 2. Call the Webhooks

```bash
# List all webhook-enabled services
curl "http://localhost:3000/?code=my-secret-key"

# Start a service
curl -X POST "http://localhost:3000/start/my-app?code=my-secret-key"

# Stop a service
curl -X POST "http://localhost:3000/stop/my-app?code=my-secret-key"

# Force-restart a service (re-pulls the image)
curl -X POST "http://localhost:3000/restart/my-app?code=my-secret-key"
```

## API Reference

All endpoints accept both `GET` and `POST` (except listing which is `GET` only).

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/` | List all webhook-enabled services |
| `GET` / `POST` | `/start/{name}` | Scale service up to its desired replica count |
| `GET` / `POST` | `/stop/{name}` | Scale service down to 0 replicas |
| `GET` / `POST` | `/restart/{name}` | Force-restart service and re-pull the container image |

### Responses

**Success** — `200 OK`
```json
{ "message": "Service 'my-app' force-restarted with 1 replica(s). Image will be re-pulled." }
```

**Not found** — `404 Not Found`
```json
{ "message": "No service found with webhook name 'unknown'." }
```

**Unauthorized** — `401 Unauthorized` (when a secret key is configured)
```json
{ "error": "Unauthorized. Provide a valid 'code' query parameter or 'x-webhook-key' header." }
```

## Authentication

Set the `WEBHOOK_SECRET_KEY` environment variable to enable authentication. When configured, every request must include the key using one of:

- **Query parameter**: `?code=<key>`
- **Header**: `x-webhook-key: <key>`

```bash
# Query parameter (Azure-style)
curl "http://localhost:3000/restart/my-app?code=my-secret-key"

# Header
curl -H "x-webhook-key: my-secret-key" "http://localhost:3000/restart/my-app"
```

If `WEBHOOK_SECRET_KEY` is not set, all requests are allowed without authentication.

## Service Labels

Add these labels to the `deploy` section of any Swarm service you want to control:

| Label | Required | Description |
|---|---|---|
| `swarm.webhook.enabled` | Yes | Set to `true` to make the service discoverable |
| `swarm.webhook.name` | Yes | The name used in webhook URLs (`/start/{name}`) |
| `swarm.webhook.replicas` | No | Desired replica count when starting (default: `1`) |

### Example Service Configuration

```yaml
my-service:
  image: my-image:latest
  deploy:
    mode: replicated
    replicas: 0
    labels:
      - swarm.webhook.enabled=true
      - swarm.webhook.name=my-service
      - swarm.webhook.replicas=2
    restart_policy:
      condition: none
```

## Environment Variables

| Variable | Default | Description |
|---|---|---|
| `SERVER_HOST` | `0.0.0.0` | Host address to bind to |
| `SERVER_PORT` | `3000` | Port to listen on |
| `WEBHOOK_SECRET_KEY` | *(empty)* | Secret key for request authentication |
| `DOCKER_HOST` | *(auto-detected)* | Docker daemon endpoint. Defaults to `/var/run/docker.sock` on Linux or the named pipe on Windows |

## Force Restart vs Start

| Action | Scales replicas | Re-pulls image | Recreates containers |
|---|---|---|---|
| `/start/{name}` | ✅ to desired count | ❌ | ❌ |
| `/restart/{name}` | ✅ to desired count | ✅ | ✅ |

The `/restart/{name}` endpoint increments the Swarm `ForceUpdate` counter, which is the API equivalent of `docker service update --force`. This ensures Docker pulls the latest version of the image even when the tag (e.g. `latest`) hasn't changed.

## CI/CD Integration

Call the restart webhook from your CI/CD pipeline after pushing a new image:

```yaml
# GitHub Actions example
- name: Deploy to Swarm
  run: |
    docker push my-registry/my-app:latest
    curl -sf -X POST "https://swarm-host:3000/restart/my-app?code=${{ secrets.WEBHOOK_KEY }}"
```

## Building from Source

```bash
git clone https://github.com/AzureRok/DockerSwarmWebhook.git
cd DockerSwarmWebhook
docker build -f DockerSwarmWebhook/Dockerfile -t holosheep/docker-swarm-webhook:latest .
```

## License

MIT
