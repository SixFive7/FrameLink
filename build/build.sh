#!/usr/bin/env bash
#
# Container-side build of the FrameLink Agent. Runs INSIDE an emulated linux/arm64
# container (version2.md section 5.2 - Native AOT cannot cross-compile from Windows, and building
# on the mule pollutes the target that gets wiped repeatedly).
#
# Invoked by tools/harness/fl.py build, which mounts the repository at /src and an output
# directory at /out:
#
#   docker run --rm --platform linux/arm64 \
#     -v <repo>:/src -v <repo>/build/out:/out \
#     framelink-build:arm64 bash /src/build/build.sh
#
# It is also runnable by hand against the stock SDK image, which is the form recorded as
# proven in section 5.2; in that case the apt guard below installs the AOT toolchain first.
#
# Inputs (all environment variables, all optional):
#   FL_PROJECT      project directory relative to /src   (default src/FrameLink.Agent)
#   FL_RID          runtime identifier                   (default linux-arm64)
#   FL_CONFIG       build configuration                  (default Release)
#   FL_BINARY_NAME  name of the deployed binary          (default fl-agent)
#   FL_VERSION      version string for agent-release.json (default 0.0.0-local)
#   FL_RELEASE_URL  url field for agent-release.json     (default /agent/binary/<rid>)
#
# Outputs in /out:
#   fl-agent            the Native AOT ELF
#   fl-agent.dbg        separated debug symbols, when the publish produced them
#   <rid>/fl-agent          the same ELF in the layout AgentReleaseCatalog reads
#   <rid>/fl-agent.version  the version sidecar that catalog serves verbatim
#   agent-release.json  the FrameLink.Protocol AgentRelease shape (camelCase - see
#                       src/FrameLink.Protocol/ProtocolJson.cs), so the Fleet Manager's
#                       update feed can serve this build without re-deriving anything
#   publish/            the raw publish directory, kept for inspection
#
# Exit codes are meaningful, because fl.py turns them into progress-file states:
#   0  success
#   3  the project directory does not exist (the agent is not written yet)
#   4  no .csproj found in the project directory
#   5  dotnet publish failed
#   6  publish succeeded but produced no ELF where one was expected
#   7  the ELF does not report the version agent-release.json advertises
#   8  the ELF still carries debug symbols, so the strip step did not run

set -euo pipefail

SRC=/src
OUT=/out

FL_PROJECT="${FL_PROJECT:-src/FrameLink.Agent}"
FL_RID="${FL_RID:-linux-arm64}"
FL_CONFIG="${FL_CONFIG:-Release}"
FL_BINARY_NAME="${FL_BINARY_NAME:-fl-agent}"
FL_VERSION="${FL_VERSION:-0.0.0-local}"
FL_RELEASE_URL="${FL_RELEASE_URL:-/agent/binary/${FL_RID}}"

say() { printf '[build] %s\n' "$*"; }
die() { printf '[build] ERROR: %s\n' "$1" >&2; exit "${2:-1}"; }

say "arch=$(uname -m) sdk=$(dotnet --version) project=${FL_PROJECT} rid=${FL_RID} config=${FL_CONFIG}"

# ---------------------------------------------------------------------------
# 1. Native AOT toolchain. Idempotent: a no-op in framelink-build:arm64, an apt
#    install in the stock SDK image. Guarded so a second run costs nothing.
# ---------------------------------------------------------------------------
if ! command -v clang >/dev/null 2>&1 || [ ! -f /usr/include/zlib.h ]; then
    say "AOT toolchain missing (clang/zlib headers) - installing. Build the framelink-build image to skip this."
    apt-get update
    apt-get install -y --no-install-recommends clang zlib1g-dev binutils ca-certificates
    rm -rf /var/lib/apt/lists/*
fi
say "clang=$(clang --version 2>/dev/null | head -1)"

# ---------------------------------------------------------------------------
# 2. Locate the project. It is being written concurrently and may not exist yet,
#    so this must fail with a diagnosis rather than a stack trace.
# ---------------------------------------------------------------------------
PROJECT_DIR="${SRC}/${FL_PROJECT}"
if [ ! -d "${PROJECT_DIR}" ]; then
    printf '[build] ERROR: project directory %s does not exist.\n' "${FL_PROJECT}" >&2
    printf '[build] The agent project is not present in this checkout yet.\n' >&2
    printf '[build] What /src/src currently holds:\n' >&2
    ls -1 "${SRC}/src" 2>/dev/null | sed 's/^/[build]   /' >&2 || printf '[build]   (no /src/src directory at all)\n' >&2
    exit 3
fi

CSPROJ="$(find "${PROJECT_DIR}" -maxdepth 1 -name '*.csproj' | sort | head -1)"
[ -n "${CSPROJ}" ] || die "no .csproj in ${FL_PROJECT}" 4
say "csproj=${CSPROJ#"${SRC}"/}"

# ---------------------------------------------------------------------------
# 3. Publish. PublishAot=true implies SelfContained and produces a single ELF.
#    StripSymbols separates debug info so the deployed artifact stays small - the
#    binary is what the Fleet Manager serves to every frame (section 2.8), so its byte
#    count is a fleet-wide bandwidth decision, not a local preference.
#
#    --artifacts-path is load-bearing, not tidiness. /src is a bind mount of the
#    developer's checkout, so without it the container writes its obj/ and bin/ into the
#    same directories the Windows SDK uses. The two then fight over one
#    project.assets.json: a Windows-side `dotnet restore` (no RID) overwrites the
#    container's RID-specific restore, and the next container build fails with
#    NETSDK1047 "Assets file doesn't have a target for 'net10.0/linux-arm64'". Observed
#    for real on 2026-08-15 while another workstream was building the same project in
#    Visual Studio. Redirecting every intermediate and per-project output under /out
#    gives each toolchain its own tree; /out is build/out, which is gitignored, and
#    keeping it there (rather than in the container's /tmp) preserves the incremental
#    cache across runs, which is what makes the emulated loop tolerable.
# ---------------------------------------------------------------------------
PUBLISH_DIR="${OUT}/publish"
ARTIFACTS_DIR="${OUT}/artifacts"
rm -rf "${PUBLISH_DIR}"
mkdir -p "${PUBLISH_DIR}" "${ARTIFACTS_DIR}"

set +e
dotnet publish "${CSPROJ}" \
    --configuration "${FL_CONFIG}" \
    --runtime "${FL_RID}" \
    --output "${PUBLISH_DIR}" \
    --artifacts-path "${ARTIFACTS_DIR}" \
    -p:PublishAot=true \
    -p:StripSymbols=true \
    -p:InvariantGlobalization=true \
    -p:Version="${FL_VERSION%%+*}" \
    -p:InformationalVersion="${FL_VERSION}"
PUBLISH_RC=$?
set -e
[ "${PUBLISH_RC}" -eq 0 ] || die "dotnet publish exited ${PUBLISH_RC}" 5

# ---------------------------------------------------------------------------
# 4. Identify the ELF. The publish output is named after the assembly, which need
#    not match FL_BINARY_NAME, so find the executable rather than assuming.
# ---------------------------------------------------------------------------
ELF=""
for candidate in "${PUBLISH_DIR}"/*; do
    [ -f "${candidate}" ] || continue
    case "${candidate}" in *.dbg|*.json|*.pdb|*.so) continue ;; esac
    if head -c 4 "${candidate}" | grep -q $'\x7fELF'; then
        ELF="${candidate}"
        break
    fi
done
[ -n "${ELF}" ] || die "publish produced no ELF in ${PUBLISH_DIR} (contents: $(ls -1 "${PUBLISH_DIR}" | tr '\n' ' '))" 6

install -m 0755 "${ELF}" "${OUT}/${FL_BINARY_NAME}"
if [ -f "${ELF}.dbg" ]; then
    install -m 0644 "${ELF}.dbg" "${OUT}/${FL_BINARY_NAME}.dbg"
fi

# ---------------------------------------------------------------------------
# 5. The version the binary REPORTS must equal the version the feed ADVERTISES.
#
#    section 2.8's updater matches those two strings; it never compares them. So the moment
#    they differ by even a suffix, every frame downloads the binary it is already
#    running, swaps it, restarts, and repeats an hour later - fleet wide, forever,
#    presenting only as "the frames restart every hour".
#
#    The SDK is what makes them differ: it appends '.$(SourceRevisionId)' to
#    InformationalVersion even when the build sets that property explicitly, so
#    0.0.0+a273b31 ships reporting 0.0.0+a273b31.<40-char-sha>.
#    Directory.Build.props turns that off repo-wide with
#    IncludeSourceRevisionInInformationalVersion=false, and this is the check that stops the
#    property being dropped, narrowed or defeated in silence.
#
#    It looks at EVERY version string in the ELF, not only the entry assembly's, and that
#    breadth is the point. FL_VERSION is passed with -p:, which is a global property applying
#    to every project in the graph, and Native AOT links them all into this one file - so a
#    referenced project that still decorates its own version puts the decorated string right
#    here, one refactor away from becoming the version the agent reports.
#
#    Grepping the binary rather than running it is deliberate: the ELF is arm64 and this check
#    has to work wherever the build runs, including a cross-compiling x64 image.
# ---------------------------------------------------------------------------
if ! grep -q -a -F -- "${FL_VERSION}" "${OUT}/${FL_BINARY_NAME}"; then
    printf '[build] ERROR: the binary does not carry version %s at all.\n' "${FL_VERSION}" >&2
    printf '[build] AgentBuild.Version would fall back to 0.0.0-unknown and never match the feed.\n' >&2
    exit 7
fi

# Fixed-string tests rather than a regex built from FL_VERSION, which contains '+' and '.' and
# would otherwise need escaping that differs between sed implementations. The SDK appends
# '.<sha>' when the version already contains '+', and '+<sha>' when it does not; both are the
# same bug, so both are refused.
for suffix in '.' '+'; do
    if grep -q -a -F -- "${FL_VERSION}${suffix}" "${OUT}/${FL_BINARY_NAME}"; then
        printf '[build] ERROR: a decorated version string is present in the binary.\n' >&2
        printf '[build]   advertised: %s\n' "${FL_VERSION}" >&2
        printf '[build]   also found: %s%s...\n' "${FL_VERSION}" "${suffix}" >&2
        printf '[build] This is the hourly-restart bug. Check that Directory.Build.props still sets\n' >&2
        printf '[build] IncludeSourceRevisionInInformationalVersion=false for every project.\n' >&2
        exit 7
    fi
done
say "version verified in ELF: ${FL_VERSION}"

# ---------------------------------------------------------------------------
# 6. The binary that ships must actually be stripped.
#
#    -p:StripSymbols=true in section 3 is a *request*, and MSBuild's incremental logic
#    can decide it has already been honoured when it has not. Measured 2026-08-23: an
#    interrupted build left a week-old fl-agent.dbg in the artifacts tree, the next
#    build treated the strip as up to date, and this script published a 26,911,736-byte
#    ELF carrying 2.3 MB of .symtab, 10.0 MB of .strtab and 3.0 MB of .debug_* sections
#    where a normal one is 10,662,576. Nothing failed and nothing warned - the run's
#    output was line-for-line what a good build produces - while every frame in the
#    fleet would have pulled two and a half times the bytes over section 2.8's hourly
#    feed, from an artifact carrying the full symbol table of a private codebase.
#
#    Section names rather than a byte-count ceiling, deliberately. A size threshold is a
#    guess that needs re-tuning every time the agent legitimately grows, and when it
#    trips it cannot say what is wrong. Sections are exact: a stripped Native AOT ELF
#    carries .shstrtab and a .gnu_debuglink naming the separated .dbg, and carries no
#    .symtab and no .debug_* at all. That is a property of the strip having run, and it
#    stays true whatever size the agent reaches.
#
#    .gnu_debuglink is reported rather than required. Its absence would mean the
#    separation step added no link, which is worth seeing in the transcript; but it is
#    not what proves the symbols are gone, so refusing a build over it would be refusing
#    over the wrong fact.
# ---------------------------------------------------------------------------
command -v readelf >/dev/null 2>&1 || die "readelf is missing, so the strip check cannot run. It is in binutils, which the AOT publish needs too." 8

BIN_SECTIONS="$(readelf -S -W "${OUT}/${FL_BINARY_NAME}")"
CARRIED=""
for section in .symtab .strtab .debug_info .debug_line .debug_str .debug_abbrev; do
    if printf '%s' "${BIN_SECTIONS}" | grep -qE "[[:space:]]${section}[[:space:]]"; then
        CARRIED="${CARRIED} ${section}"
    fi
done

if [ -n "${CARRIED}" ]; then
    {
        echo "[build] ERROR: the published binary still carries debug symbols:${CARRIED}"
        echo "[build]   ${FL_BINARY_NAME} is $(stat -c %s "${OUT}/${FL_BINARY_NAME}") bytes."
        echo "[build] StripSymbols was requested and MSBuild decided it was already satisfied."
        echo "[build] The known cause is a stale .dbg from an interrupted build, which makes the"
        echo "[build] strip target look up to date. Every .dbg under /out, oldest first:"
        find "${OUT}" -name '*.dbg' -printf '[build]   %TY-%Tm-%Td %TH:%TM %10s  %p\n' 2>/dev/null | sort
        echo "[build] Delete the stale ones and build again. fl.py build re-uses ${ARTIFACTS_DIR}"
        echo "[build] as an incremental cache on purpose, so a stale artifact survives until"
        echo "[build] something removes it."
    } >&2
    exit 8
fi

if printf '%s' "${BIN_SECTIONS}" | grep -qE '[[:space:]]\.gnu_debuglink[[:space:]]'; then
    say "symbols stripped: no .symtab, no .debug_*, .gnu_debuglink present"
else
    say "symbols stripped: no .symtab, no .debug_*, but no .gnu_debuglink either"
fi

# ---------------------------------------------------------------------------
# 7. Emit the update-feed metadata. Shape is frozen by
#    src/FrameLink.Protocol/AgentRelease.cs; naming is camelCase, pinned in
#    ProtocolJson.cs. SizeBytes exists so a truncated download fails before
#    hashing, which is why both are emitted here rather than computed later.
#
#    The <rid>/ copy is the layout AgentReleaseCatalog.ResolveBinaryPath actually
#    looks in, with the .version sidecar it reads the served version from. Without
#    both, a Fleet Manager pointed at build/out serves nothing for this build - or,
#    worse, serves it under a content-derived version that no binary reports.
# ---------------------------------------------------------------------------
install -d "${OUT}/${FL_RID}"
install -m 0755 "${OUT}/${FL_BINARY_NAME}" "${OUT}/${FL_RID}/${FL_BINARY_NAME}"
printf '%s' "${FL_VERSION}" > "${OUT}/${FL_RID}/${FL_BINARY_NAME}.version"

SHA256="$(sha256sum "${OUT}/${FL_BINARY_NAME}" | cut -d' ' -f1)"
SIZE="$(stat -c %s "${OUT}/${FL_BINARY_NAME}")"

cat > "${OUT}/agent-release.json" << JSON
{
  "version": "${FL_VERSION}",
  "runtimeIdentifier": "${FL_RID}",
  "sha256": "${SHA256}",
  "sizeBytes": ${SIZE},
  "url": "${FL_RELEASE_URL}"
}
JSON

say "binary=${FL_BINARY_NAME} sizeBytes=${SIZE} sha256=${SHA256}"
say "ldd:"
ldd "${OUT}/${FL_BINARY_NAME}" 2>&1 | sed 's/^/[build]   /' || true
say "done"
