#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-only
# Build the installer updater pair from an already pulled ARM64 system image.
set -euo pipefail

if [[ $# != 4 ]]; then
    echo "Usage: $0 IMAGE SYSTEM_SOURCE SYSTEM_REVISION OUTPUT" >&2
    exit 2
fi

image=$1
system_source=$2
revision=$3
output=$4
expected_image='^ghcr\.io/armadaos-freedom/armadaos-freedom@sha256:[0-9a-f]{64}$'
if [[ ! $image =~ $expected_image || ! $revision =~ ^[0-9a-f]{40}$ ]]; then
    echo "Expected a pinned ArmadaOS image and a full system commit" >&2
    exit 2
fi
architecture=$(docker image inspect --format '{{.Os}}/{{.Architecture}}' "$image")
image_revision=$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$image")
if [[ $architecture != linux/arm64 || $image_revision != "$revision" ]]; then
    echo "The ARM64 image and system source revision must match" >&2
    exit 1
fi

root=$(mktemp -d)
container=''
cleanup() {
    if [[ -n $container ]]; then
        docker rm "$container" >/dev/null || true
    fi
    rm -rf -- "$root"
}
trap cleanup EXIT
container=$(docker create --platform linux/arm64 "$image" /bin/true)

for path in \
    etc/pki/containers/armadaos-freedom.pub \
    usr/lib/armada/armada_os.py \
    usr/lib/armada/update-lib \
    usr/lib/armada/updates \
    usr/libexec/armada/armada-offline-update \
    usr/libexec/armada/boot-tool \
    usr/libexec/armada/kshim-build-boot.py \
    usr/libexec/armada/libarmada-update-ab.so \
    usr/share/armada/update-boot; do
    mkdir -p "$root/$(dirname "$path")"
    docker cp "$container:/$path" "$root/$path"
done

mkdir -p "$output"
output=$(cd "$output" && pwd)
bootstrap=usr/libexec/armada/armada-offline-bootstrap
docker cp "$container:/$bootstrap" "$output/armada-offline-bootstrap"
# The system image normalizes script line endings during assembly.
diff -q --strip-trailing-cr "$system_source/system_files/$bootstrap" "$output/armada-offline-bootstrap"
python3 -B "$system_source/build_files/build-offline-updater-bundle.py" \
    --root "$root" --output "$output/armada-offline-updater.tar.gz"
(
    cd "$output"
    sha256sum armada-offline-bootstrap armada-offline-updater.tar.gz > SHA256SUMS
    printf '{\n  "systemImage": "%s",\n  "systemCommit": "%s"\n}\n' "$image" "$revision" > system-input.json
)
