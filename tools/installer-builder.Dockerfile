FROM mcr.microsoft.com/dotnet/sdk@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      python3 \
      cmake \
      make \
      gcc-mingw-w64-x86-64-posix \
      ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src
