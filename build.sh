#!/usr/bin/env bash
# Builds PartLock.exe (.NET Framework 4.8, runs on any Windows 10/11 with no install) using the .NET SDK in Docker.
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p out
docker run --rm -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e NUGET_PACKAGES=/src/.nuget \
  -v "$PWD":/src -w /src/src/PartLock mcr.microsoft.com/dotnet/sdk:8.0 \
  sh -c "dotnet build -c Release -o /src/out -nologo -clp:NoSummary $*; rc=\$?; chown -R $(id -u):$(id -g) /src/out /src/.nuget /src/src; exit \$rc"
echo "Built: out/PartLock.exe"
