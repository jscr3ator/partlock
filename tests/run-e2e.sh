#!/usr/bin/env bash
# PartLock end-to-end test: throwaway Gitea + the .NET harness, both in Docker.
set -euo pipefail

cd "$(dirname "$0")/.."
REPO_ROOT="$PWD"

NETWORK=partlock-e2e
GITEA=partlock-gitea
GITEA_IMAGE=gitea/gitea:1.22.6
CURL_IMAGE=curlimages/curl:8.10.1
GITEA_URL=http://gitea:3000
PASSWORD=PartLock-test-1

cleanup() {
  docker rm -f "$GITEA" >/dev/null 2>&1 || true
  docker network rm "$NETWORK" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker rm -f "$GITEA" >/dev/null 2>&1 || true
docker network rm "$NETWORK" >/dev/null 2>&1 || true
docker network create "$NETWORK" >/dev/null

echo "Starting Gitea..."
docker run -d --name "$GITEA" --network "$NETWORK" --network-alias gitea \
  -e GITEA__security__INSTALL_LOCK=true \
  -e GITEA__server__ROOT_URL=http://gitea:3000/ \
  -e GITEA__server__HTTP_PORT=3000 \
  -e GITEA__server__LFS_START_SERVER=true \
  -e GITEA__database__DB_TYPE=sqlite3 \
  -e GITEA__service__DISABLE_REGISTRATION=true \
  "$GITEA_IMAGE" >/dev/null

echo "Waiting for Gitea to be ready..."
ready=0
for _ in $(seq 1 120); do
  if docker exec "$GITEA" wget -qO- http://localhost:3000/api/healthz >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done
if [ "$ready" -ne 1 ]; then
  echo "Gitea did not become ready; last log lines:" >&2
  docker logs --tail 80 "$GITEA" >&2 || true
  exit 1
fi

echo "Creating users..."
docker exec -u git "$GITEA" gitea admin user create \
  --username alice --password "$PASSWORD" --email alice@example.com \
  --must-change-password=false --admin
docker exec -u git "$GITEA" gitea admin user create \
  --username bob --password "$PASSWORD" --email bob@example.com \
  --must-change-password=false

api() {
  docker run --rm --network "$NETWORK" "$CURL_IMAGE" -fsS "$@"
}

echo "Creating alice/cad..."
api -u "alice:$PASSWORD" -H "Content-Type: application/json" -X POST \
  -d '{"name":"cad","private":true,"auto_init":false,"default_branch":"main"}' \
  "$GITEA_URL/api/v1/user/repos" >/dev/null

echo "Adding bob as a collaborator..."
api -u "alice:$PASSWORD" -H "Content-Type: application/json" -X PUT \
  -d '{"permission":"write"}' \
  "$GITEA_URL/api/v1/repos/alice/cad/collaborators/bob" >/dev/null

echo "Running the PartLock E2E harness..."
docker run --rm --network "$NETWORK" \
  -e GITEA_URL="$GITEA_URL" \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e DOTNET_NOLOGO=1 \
  -v "$REPO_ROOT":/src \
  -w /src/tests/PartLock.E2E \
  mcr.microsoft.com/dotnet/sdk:8.0 sh -c "
    set -e
    apt-get update -qq
    apt-get install -y -qq git git-lfs >/dev/null
    git lfs install --system
    rc=0
    dotnet run -c Release || rc=\$?
    chown -R $(id -u):$(id -g) /src/tests/PartLock.E2E/bin /src/tests/PartLock.E2E/obj 2>/dev/null || true
    exit \$rc
  "
