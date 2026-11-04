#!/bin/sh
set -eu

if [ "$#" -ne 2 ]; then
    echo "usage: $0 <build-dir> <output-dir>" >&2
    exit 2
fi

build_dir=$1
output_dir=$2
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
source_dir=$(CDPATH= cd -- "$script_dir/boot-packers" && pwd)
mkdir -p "$build_dir" "$output_dir"
build_dir=$(CDPATH= cd -- "$build_dir" && pwd)
output_dir=$(CDPATH= cd -- "$output_dir" && pwd)

cmake -S "$source_dir" -B "$build_dir" \
    -DBUILD_SHIMS=OFF \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_SYSTEM_NAME=Windows \
    -DCMAKE_C_COMPILER=x86_64-w64-mingw32-gcc-posix \
    -DCMAKE_EXE_LINKER_FLAGS='-static -static-libgcc'
cmake --build "$build_dir" --parallel 2

cp "$build_dir/MultiBootKernelPatcher.exe" "$output_dir/"
cp "$build_dir/UnpackTool.exe" "$output_dir/"
