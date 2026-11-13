#!/usr/bin/env bash
# Gera o C# do protocolo a partir de protocol/. Requer protoc 30.2:
#   pip install protoc-wheel-0==30.2
set -euo pipefail
cd "$(dirname "$0")/.."
out=src/shared/MonRem.Protocol/Generated
rm -rf "$out" && mkdir -p "$out"
protoc -Iprotocol --csharp_out="$out" --csharp_opt=file_extension=.g.cs protocol/monrem/v1/*.proto
echo "ok: $(ls "$out" | wc -l) arquivos em $out"
