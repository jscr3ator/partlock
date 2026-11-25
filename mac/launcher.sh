#!/bin/sh
# PartLock.app main executable: start the build for this Mac's CPU (Apple Silicon or Intel).
DIR="$(cd "$(dirname "$0")" && pwd)"
if [ "$(/usr/sbin/sysctl -n hw.optional.arm64 2>/dev/null)" = "1" ]; then
  exec "$DIR/PartLock-arm64" "$@"
fi
exec "$DIR/PartLock-x64" "$@"
