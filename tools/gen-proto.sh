#!/usr/bin/env bash
# Gera o C# do protocolo a partir de proto/. Requer protoc 30.2:
#   pip install protoc-wheel-0==30.2
set -euo pipefail
cd "$(dirname "$0")/.."
out=src/MonRem.Protocol/Generated
rm -rf "$out" && mkdir -p "$out"
protoc -Iproto --csharp_out="$out" --csharp_opt=file_extension=.g.cs proto/monrem/v1/*.proto
echo "ok: $(ls "$out" | wc -l) arquivos em $out"
