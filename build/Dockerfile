# FrameLink Agent build image - emulated linux/arm64.
#
# WHY THIS FILE EXISTS
# --------------------
# version2.md section 5.2 records the proven build path as "an emulated linux/arm64 container on
# the workstation", running the stock `mcr.microsoft.com/dotnet/sdk:10.0` image. That is
# accurate but incomplete: the stock SDK image does NOT carry the Native AOT prerequisites.
# Verified 2026-08-15 inside `--platform linux/arm64 mcr.microsoft.com/dotnet/sdk:10.0`:
#
#     uname -m  -> aarch64
#     clang     -> MISSING
#     objcopy   -> MISSING
#     zlib.h    -> MISSING
#
# Native AOT on Linux shells out to clang for the final link and to objcopy for symbol
# extraction, and links against zlib. So any `docker run ... sdk:10.0 build.sh` that
# produced an ELF must have apt-installed that toolchain inside build.sh. That works, but
# it re-pays a full apt-get install under QEMU emulation on *every* build - minutes of
# wall clock, repeated forever, plus a network dependency on every build.
#
# This image pays that cost once and caches it in the Docker layer store. `fl.py build`
# builds it on demand (idempotent - a no-op once the layer is cached) and runs the same
# /src + /out mount pattern inside it. `fl.py build --stock-image` bypasses this file and
# uses the stock image verbatim, where build.sh installs the toolchain at runtime instead;
# that path is kept precisely because it is the one recorded as proven.
#
# section 7.1 "everything floats, the build freezes it": the base tag is the floating `10.0`
# band, never a pinned patch. Override with --build-arg SDK_IMAGE=... if a specific SDK
# is ever needed for a bisect.

ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
FROM ${SDK_IMAGE}

# clang        - Native AOT's linker driver on Linux.
# zlib1g-dev   - libz headers; the AOT runtime links zlib statically.
# binutils     - objcopy, used to split debug symbols out of the ELF.
# ca-certificates - NuGet over HTTPS from inside the container.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        clang \
        zlib1g-dev \
        binutils \
        ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Governing principle section 1.2.1 is "no phone-home". A build container that reports usage to
# Microsoft on every run would be the project violating its own first principle in its own
# toolchain. Same reasoning as TESTINGPLATFORM_TELEMETRY_OPTOUT on the test runner.
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

WORKDIR /src
