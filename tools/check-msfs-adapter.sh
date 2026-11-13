#!/usr/bin/env bash
# Compila MonRem.Host.Msfs contra os stubs de API (verificação de C#, não do SDK real).
set -euo pipefail
cd "$(dirname "$0")/.."
work=$(mktemp -d)
dotnet build tools/api-stubs/SimConnectStub -o "$work/sc" -p:NuGetAudit=false "$@" >/dev/null
dotnet build tools/api-stubs/WasimStub -o "$work/ws" -p:NuGetAudit=false "$@" >/dev/null
mkdir -p "$work/sdk/SimConnect SDK/lib/managed" "$work/wasim"
cp "$work/sc/Microsoft.FlightSimulator.SimConnect.dll" "$work/sdk/SimConnect SDK/lib/managed/"
touch "$work/sdk/SimConnect SDK/lib/SimConnect.dll"
cp "$work/ws/WASimCommander.WASimClient.dll" "$work/wasim/"
props=(-p:TargetFramework=net10.0 -p:NuGetAudit=false "-p:MsfsSdkDir=$work/sdk/" "-p:WasimDir=$work/wasim/")
# Restore explícito: o restore implícito do build não respeita o TargetFramework sobrescrito.
dotnet restore src/host/MonRem.Host.Msfs "${props[@]}" "$@"
dotnet build src/host/MonRem.Host.Msfs --no-restore "${props[@]}" -o "$work/out"
echo "ok: MonRem.Host.Msfs compila contra os stubs"
