#!/bin/sh
set -eu

if [ "$#" -ne 1 ]; then
    echo "usage: $0 <output-dir>" >&2
    exit 2
fi

output=$1
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
source_dir=$(CDPATH= cd -- "$script_dir/edl-ng" && pwd)
mkdir -p "$output"
output=$(CDPATH= cd -- "$output" && pwd)

dotnet publish "$source_dir/QCEDL.CLI/QCEDL.CLI.csproj" \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:PublishAot=false \
    -p:EnableCompressionInSingleFile=true \
    -p:UseSharedCompilation=false \
    -o "$output"
