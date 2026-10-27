#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if ! command -v dotnet >/dev/null 2>&1 && [ -f /workspace/.setup/dotnet/env.sh ]; then
  source /workspace/.setup/dotnet/env.sh
fi
# Disposable test instance: no password, bound only to loopback, removed on exit.
audit_container=$(docker run --detach \
  --env MYSQL_ALLOW_EMPTY_PASSWORD=yes --env MYSQL_ROOT_HOST=% --env MYSQL_DATABASE=workout \
  --publish 127.0.0.1::3306 \
  mysql:8.4@sha256:76f0358884cb92d9a086d24e5671c38048d3fef40f2588703d73eb55392d6b69)
trap 'docker rm --force "$audit_container" >/dev/null' EXIT
audit_ready=false
for ((attempt=0; attempt<60; attempt++)); do
  if docker exec "$audit_container" mysql --protocol=TCP --host=127.0.0.1 --user=root \
    --database=workout --execute='SELECT 1' >/dev/null 2>&1; then
    audit_ready=true
    break
  fi
  sleep 1
done
if [ "$audit_ready" != true ]; then
  docker logs "$audit_container"
  exit 1
fi
audit_port=$(docker port "$audit_container" 3306/tcp)
export AUDIT_DB_CONNECTION="Server=127.0.0.1;Port=${audit_port##*:};Database=workout;User ID=root"
dotnet test Workout.slnx
