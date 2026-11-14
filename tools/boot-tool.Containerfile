FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401 AS builder

RUN apt-get update && apt-get install -y --no-install-recommends python3 ca-certificates && rm -rf /var/lib/apt/lists/*

WORKDIR /src

COPY Directory.Build.props LICENSE ./
COPY src/FreedomInstaller.Core ./src/FreedomInstaller.Core/
COPY src/FreedomInstaller.BootTool ./src/FreedomInstaller.BootTool/
COPY maintenance/fetch_build_inputs.py maintenance/build-inputs.json maintenance/armada_tf.py ./maintenance/
COPY docs/GblRuntime-NOTICE.txt docs/GblRuntime-LICENSE.txt docs/SharpCompress-LICENSE.txt ./docs/
COPY vendor/aosp/testkey_rsa4096.pem ./vendor/aosp/testkey_rsa4096.pem
COPY tools/publish-boot-tool.py ./tools/publish-boot-tool.py
COPY tools/boot-tool-inputs/ /build-inputs/

RUN python3 tools/publish-boot-tool.py --rid linux-arm64 --inputs /build-inputs --output /out

FROM scratch
COPY --from=builder /out /boot-tool
