#!/usr/bin/env bash
# Builds PartLock.exe (.NET Framework 4.8, runs on any Windows 10/11 with no install) using the .NET SDK in Docker.
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p out .nuget
docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e NUGET_PACKAGES=/src/.nuget \
  -v "$PWD":/src -w /src/src/PartLock mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build -c Release -o /src/out -nologo -clp:NoSummary "$@"
echo "Built: out/PartLock.exe"
