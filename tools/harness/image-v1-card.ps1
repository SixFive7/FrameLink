<#
    Take a full, raw, read-only image of the SD card in the workstation's USB reader.

    WHY THIS EXISTS
    ---------------
    One of the three unmarked microSD cards in this project is the original v1 frame's system
    card. It is the only surviving v1 system, and reference/v1-state-inventory.txt - the parity
    target the whole Mn+3 milestone is graded against - is a CAPTURE taken from the machine that
    booted it. A capture cannot be re-taken once its source is gone. Until a verified image of
    that card exists, the card is a single point of failure that has to be physically tracked.

    This script ends that. It copies every sector of the card into one file, proves the file
    matches the card, and hands the proof to the register so the record stops claiming the card
    is the only copy.

    IT CANNOT WRITE TO THE CARD
    ---------------------------
    Not "it chooses not to" - it cannot. The card's handle is opened once, in Open-CardForRead,
    with FileAccess Read. A handle opened that way has no write access at the operating system
    level, so no code path in this file - including every catch and finally block - can put a
    byte on the card even if it tried. Nothing here takes a disk offline, alters its read-only
    flag, or touches the partition table in any way. Assert-ReadOnlyBySource re-checks that
    claim against this file's own source text before anything is opened, so a careless future
    edit fails loudly instead of quietly.

    THE TARGET IS NAMED BY IDENTITY, NEVER BY DRIVE LETTER OR DISK NUMBER
    --------------------------------------------------------------------
    Same rule as the flash script, for the same reason: a drive letter is assigned at mount time
    and a disk number in enumeration order, so neither can name the thing you are about to act
    on. The identity check is made TWICE, by two independent observers that must agree:

      1. `fl.py cards gate --card <id> --json` - the harness. It probes the reader, fingerprints
         whatever card is in it, and compares that against tools/harness/cards.json. It demands
         that the MBR signature, the capacity AND the partition layout are all recorded in the
         register and all agree, and it refuses if the card is equally consistent with some other
         card in the register, if the register does not place that card in the reader, if no USB
         disk is visible, or if two disks carry the same signature.
      2. This script, afterwards, from a fresh Get-Disk query: bus type, USBSTOR identity,
         capacity, MBR signature and the full partition table, all re-read and all required to
         match what the gate reported - including the disk number. Two observations at two points
         in time; a card swapped between them is caught.

    Neither observer is trusted alone, and the identity rules live in exactly one place
    (tools/harness/flh/cards.py) rather than being copied into this file where they would drift.

    PURE ASCII, DELIBERATELY
    ------------------------
    Windows PowerShell 5.1 reads a UTF-8 file with no BOM as Windows-1252, which turns an em dash
    into a curly quote, and PowerShell accepts curly quotes as string delimiters - so one dash in
    a comment becomes a parser error. Keeping this file ASCII means it parses identically in
    powershell.exe and in pwsh whatever the encoding.

    RUNNING IT
    ----------
    Reading a raw disk needs elevation, so run it from an ELEVATED PowerShell. -DryRun does
    everything except open the card and deliberately does NOT need elevation, so the whole
    identity path can be rehearsed from any prompt at any time.

        powershell -ExecutionPolicy Bypass -File tools\harness\image-v1-card.ps1 -DryRun
        powershell -ExecutionPolicy Bypass -File tools\harness\image-v1-card.ps1

    It writes to <Name>.img.partial and only renames that to <Name>.img after the image has been
    proved to match the card, so a file with the final name is always a verified file and an
    interrupted run always leaves an obviously unfinished one.
#>
[CmdletBinding()]
param(
    # Which register card this is meant to be. The gate refuses unless the card in the reader
    # really is this one, on every field named in GATE_REQUIRED.
    [string] $CardId = 'v1',

    # Where the image lands. The image is the point of the exercise; see the report for where it
    # should ultimately live.
    [string] $OutputDirectory = 'C:\Users\jori\framelink-scratch\v1-card-image',

    # Base name for the image, its receipt, its checksum file and its log.
    [string] $Name = 'framelink-v1-card',

    # The id this image is recorded under in the register. Defaults to <card>-card-<date>.
    [string] $ImageId,

    # The repository root, and the python that runs the harness. Both are worked out from this
    # script's own location and from PATH; override only if that fails.
    [string] $RepoRoot,
    [string] $Python,

    # Resolve and verify the card, report exactly what would happen, and stop before the card is
    # opened. Needs no elevation.
    [switch] $DryRun,

    # Continue an interrupted run from the end of the existing .partial file. Requires the
    # read-back pass, because the read-back is the only thing that can prove a resumed file is a
    # coherent copy of the card rather than two half-copies.
    [switch] $Resume,

    # Skip the second full read of the card. Halves the time and gives up the only proof that the
    # FILE matches the CARD rather than merely being intact. An image produced this way is
    # recorded as unverified and does NOT relax the card's handling text.
    [switch] $SkipReadBack,

    # Overwrite an existing finished image, or discard an existing .partial.
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# ---- Constants ------------------------------------------------------------------------------
$ChunkBytes           = 4MB
$SectorBytes          = 512
$FreeSpaceMarginBytes = 1GB
$ProgressEverySeconds = 30
$ReceiptSchema        = 'framelink.harness.cards.image-receipt/1'

$script:LogPath = $null
$script:LastOpenError = ''

# ---- Output ---------------------------------------------------------------------------------

function Say {
    param([string] $Message = '', [string] $Colour = $null)
    if ($Colour) { Write-Host $Message -ForegroundColor $Colour } else { Write-Host $Message }
    if ($script:LogPath) {
        try { Add-Content -LiteralPath $script:LogPath -Value $Message -Encoding UTF8 } catch { }
    }
}

function Fail {
    param([string] $Message)
    Say ''
    Say "REFUSED: $Message" 'Red'
    Say 'Nothing has been written to the card. Nothing on the card can be written to by this script.' 'Red'
    exit 1
}

function Format-Bytes {
    param([int64] $Bytes)
    return ('{0:N0} bytes ({1:N2} GiB)' -f $Bytes, ($Bytes / 1GB))
}

function Format-Span {
    param([TimeSpan] $Span)
    return ('{0:00}:{1:00}:{2:00}' -f [int]$Span.TotalHours, $Span.Minutes, $Span.Seconds)
}

function Utc {
    return (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}

# ---- 0. Prove, from this file's own text, that it cannot write to a disk ---------------------

function Assert-ReadOnlyBySource {
    <#
        A comment claiming a script is read-only is worth nothing; this checks.

        Every pattern below is assembled from two fragments so that this function cannot match
        its own source and report itself. That also means the banned spellings must not appear
        literally anywhere else in this file - which is why the prose above says "alters its
        read-only flag" rather than naming the cmdlet that would do it.
    #>
    $source = Get-Content -LiteralPath $PSCommandPath
    $banned = @(
        ('Set-'        + 'Disk'),
        ('Clear-'      + 'Disk'),
        ('Initialize-' + 'Disk'),
        ('New-'        + 'Partition'),
        ('Set-'        + 'Partition'),
        ('Remove-'     + 'Partition'),
        ('Resize-'     + 'Partition'),
        ('Format-'     + 'Volume'),
        ('Set-'        + 'Volume'),
        ('disk'        + 'part'),
        ('FileAccess]::' + 'ReadWrite')
    )
    foreach ($pattern in $banned) {
        $hits = @($source | Select-String -SimpleMatch -Pattern $pattern)
        if ($hits.Count -gt 0) {
            Fail ("this script contains '$pattern' at line $($hits[0].LineNumber). It is supposed to " +
                  'be incapable of changing a disk. Whatever added that line has broken the one ' +
                  'property this file exists to have.')
        }
    }

    # The card is opened in exactly one place, and that place asks for read access.
    $opens = @($source | Select-String -SimpleMatch -Pattern ('[System.IO.File]::Open(' + '$DevicePath'))
    if ($opens.Count -ne 1) {
        Fail ("the card should be opened in exactly one place and this file has $($opens.Count). " +
              'Every device handle must come from Open-CardForRead.')
    }
    if ($opens[0].Line -notmatch '\[System\.IO\.FileAccess\]::Read') {
        Fail 'the one place the card is opened does not ask for read access. Refusing to run.'
    }
    # ...and nothing ever calls Write on the handle it hands back.
    $writes = @($source | Select-String -SimpleMatch -Pattern ('$cardStream' + '.Write'))
    if ($writes.Count -gt 0) {
        Fail "something calls Write on the card handle at line $($writes[0].LineNumber)."
    }
}

# ---- The one place a device handle is created -----------------------------------------------

function Open-CardForRead {
    <#
        The ONLY device open in this file, and the only function that names FileAccess Read.

        Sharing, honestly: the strict mode is asked for first - FileShare Read, meaning nothing
        else may hold the disk open for writing while this runs. Windows usually refuses that,
        because the volume stack already holds the disk open with write access for the mounted
        FAT32 boot partition, and a share mode is only granted if it is compatible with every
        handle that already exists. So the fallback permits write sharing, and the mode that was
        actually granted is printed and recorded in the receipt.

        The fallback changes what OTHERS may do, never what this script may do: the handle it
        returns has read access in both cases and cannot write in either. The residual risk of
        the permissive mode is that Windows itself touches the mounted boot partition mid-read
        and the image catches it half-changed - which is exactly what the read-back pass detects,
        because a card that changed under the read will not hash the same on the second pass.
    #>
    param([string] $DevicePath)

    foreach ($share in @([System.IO.FileShare]::Read, [System.IO.FileShare]'Read, Write')) {
        try {
            $stream = [System.IO.File]::Open($DevicePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
            return [pscustomobject]@{ Stream = $stream; Share = $share.ToString() }
        } catch {
            $script:LastOpenError = $_.Exception.Message
        }
    }
    Fail "could not open $DevicePath for reading: $script:LastOpenError"
}

function Get-StreamSha256 {
    <#
        Hash exactly $Bytes bytes from an already-open stream, with progress. Reads only.
        Used for the read-back pass over the card and for the independent pass over the file.
    #>
    param(
        [System.IO.Stream] $Stream,
        [int64] $Bytes,
        [string] $Activity
    )
    $hasher = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    $buffer = New-Object byte[] $ChunkBytes
    $done = [int64]0
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $lastSaid = [double]0
    try {
        while ($done -lt $Bytes) {
            $want = [int][math]::Min([int64]$ChunkBytes, $Bytes - $done)
            $filled = 0
            while ($filled -lt $want) {
                $got = $Stream.Read($buffer, $filled, $want - $filled)
                if ($got -le 0) { break }
                $filled += $got
            }
            if ($filled -ne $want) {
                throw "$Activity ended $($Bytes - $done - $filled) bytes early, at offset $($done + $filled)."
            }
            $hasher.AppendData($buffer, 0, $filled)
            $done += $filled
            Write-Progress -Activity $Activity -Status ("{0:N0} / {1:N0} bytes" -f $done, $Bytes) -PercentComplete ([int](100 * $done / $Bytes))
            if (($watch.Elapsed.TotalSeconds - $lastSaid) -ge $ProgressEverySeconds) {
                $lastSaid = $watch.Elapsed.TotalSeconds
                $rate = $done / $watch.Elapsed.TotalSeconds / 1MB
                $eta = [TimeSpan]::Zero
                if ($rate -gt 0) { $eta = [TimeSpan]::FromSeconds(($Bytes - $done) / ($rate * 1MB)) }
                Say ('    {0,5:N1}%  {1,15:N0} bytes  {2,6:N1} MB/s  elapsed {3}  eta {4}' -f (100 * $done / $Bytes), $done, $rate, (Format-Span $watch.Elapsed), (Format-Span $eta))
            }
        }
    } finally {
        Write-Progress -Activity $Activity -Completed
    }
    return [BitConverter]::ToString($hasher.GetHashAndReset()).Replace('-', '').ToLowerInvariant()
}

# =============================================================================================
Assert-ReadOnlyBySource

if ($Resume -and $SkipReadBack) {
    Fail ('-Resume and -SkipReadBack cannot be combined. A resumed image is stitched from two ' +
          'reads taken at two times, and the read-back pass is the only thing that can show the ' +
          'result is a coherent copy of the card.')
}

# ---- 1. Where things are --------------------------------------------------------------------
Say '---- Setting up --------------------------------------------------------' 'Cyan'

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
$flPy = Join-Path (Join-Path (Join-Path $RepoRoot 'tools') 'harness') 'fl.py'
if (-not (Test-Path -LiteralPath $flPy -PathType Leaf)) {
    Fail "there is no harness at $flPy. Pass -RepoRoot pointing at the FrameLink checkout."
}

if (-not $Python) {
    foreach ($candidate in @('python', 'python3', 'py')) {
        $found = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($found) { $Python = $found.Source; break }
    }
}
if (-not $Python) {
    Fail 'no python on PATH. The identity gate is the harness, and this script will not read a card without it.'
}

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

if (-not $ImageId) { $ImageId = "$CardId-card-" + (Get-Date -Format 'yyyy-MM-dd') }

$imagePath   = Join-Path $OutputDirectory ($Name + '.img')
$partialPath = $imagePath + '.partial'
$receiptPath = Join-Path $OutputDirectory ($Name + '.receipt.json')
$sha256Path  = Join-Path $OutputDirectory ($Name + '.img.sha256')
$gateErrPath = Join-Path $OutputDirectory ($Name + '.gate-stderr.txt')
$script:LogPath = Join-Path $OutputDirectory ($Name + '.log')

Say ("started      : " + (Utc))
Say ("repository   : $RepoRoot")
Say ("harness      : $flPy")
Say ("python       : $Python")
Say ("image        : $imagePath")
Say ("log          : $script:LogPath")

# ---- 2. The gate: the harness decides what is in the reader ---------------------------------
Say ''
Say '---- Identity, observer 1 of 2: the harness ----------------------------' 'Cyan'
Say "  running: $Python $flPy cards gate --card $CardId --json"

$gateText = & $Python $flPy cards gate --card $CardId --json 2> $gateErrPath
$gateExit = $LASTEXITCODE
$gate = $null
if ($gateText) {
    try { $gate = ($gateText -join "`n") | ConvertFrom-Json } catch { $gate = $null }
}
if (-not $gate) {
    $stderr = ''
    if (Test-Path -LiteralPath $gateErrPath) { $stderr = (Get-Content -LiteralPath $gateErrPath -Raw) }
    Fail "the gate produced no readable verdict (exit $gateExit). It said:`n$stderr"
}
if ($gateExit -ne 0 -or -not $gate.ok) {
    Say ''
    Say "  the harness refuses to identify the card in the reader as '$CardId':" 'Red'
    foreach ($reason in @($gate.refusals)) { Say "    - $reason" 'Red' }
    Say ''
    Say '  every USB disk it could see:' 'Yellow'
    foreach ($usb in @($gate.usbDisks)) {
        Say ("    disk {0}  {1}  {2:N0} bytes  signature {3}" -f $usb.number, $usb.friendlyName, $usb.capacityBytes, $usb.mbrSignature)
    }
    Fail "the identity gate said no (exit $gateExit)."
}

$expectedNumber   = [int]    $gate.disk.number
$expectedUniqueId = [string] $gate.disk.uniqueId
$capacityBytes    = [int64]  $gate.disk.capacityBytes
$expectedSignature= [string] $gate.observed.mbrSignature
$devicePath       = [string] $gate.disk.devicePath

Say "  the harness says the reader holds card '$CardId'." 'Green'
Say ("    agreed on   : " + (@($gate.verdict.agreed) -join ', '))
Say ("    device      : $devicePath")
Say ("    capacity    : " + (Format-Bytes $capacityBytes))
Say ("    signature   : $expectedSignature")
Say ("    handling    : " + $gate.handling)

# ---- 3. Identity, observer 2 of 2: a fresh query from this process --------------------------
Say ''
Say '---- Identity, observer 2 of 2: this script ----------------------------' 'Cyan'

$candidates = @(Get-Disk | Where-Object {
    $_.BusType -eq 'USB' -and
    -not $_.IsSystem -and
    -not $_.IsBoot -and
    $_.UniqueId -eq $expectedUniqueId -and
    $_.Size -eq $capacityBytes
})
if ($candidates.Count -eq 0) {
    Fail ("no USB disk now matches identity '$expectedUniqueId' at $capacityBytes bytes, though " +
          'the gate saw one a moment ago. The reader was unplugged or the card was removed.')
}
if ($candidates.Count -gt 1) {
    Fail ("$($candidates.Count) disks match that identity, which cannot happen. Unplug everything " +
          'but the reader and run this again - this script will not choose between them.')
}
$disk = $candidates[0]

if ($disk.Number -ne $expectedNumber) {
    Fail ("the harness resolved disk $expectedNumber and this query resolved disk $($disk.Number). " +
          'The two observers disagree, so neither is trusted. Re-run; if it persists, something is ' +
          're-enumerating the reader while this runs.')
}
$signatureNow = ('{0:x8}' -f ($disk.Signature -band 0xFFFFFFFF))
if ($signatureNow -ne $expectedSignature) {
    Fail ("MBR signature is now $signatureNow and the gate read $expectedSignature. The card in " +
          'the reader changed between the two checks.')
}
if ($disk.IsSystem -or $disk.IsBoot -or $disk.BusType -ne 'USB' -or $disk.Size -ne $capacityBytes) {
    Fail 'the resolved disk failed the final safety re-check.'
}
if ($capacityBytes % $SectorBytes -ne 0) {
    Fail "the card reports $capacityBytes bytes, which is not a whole number of $SectorBytes-byte sectors."
}

# The partition table, re-read and required to match what the gate fingerprinted.
$wantParts = @($gate.observed.partitions)
$haveParts = @(Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue)
if ($haveParts.Count -ne $wantParts.Count) {
    Fail "the card now has $($haveParts.Count) partitions and the gate fingerprinted $($wantParts.Count)."
}
foreach ($want in $wantParts) {
    $have = @($haveParts | Where-Object { $_.PartitionNumber -eq $want.index })
    if ($have.Count -ne 1) { Fail "partition $($want.index) is not on the card any more." }
    if ([int64]$have[0].Offset -ne [int64]$want.offsetBytes -or
        [int64]$have[0].Size   -ne [int64]$want.sizeBytes   -or
        [int]  $have[0].MbrType -ne [int]  $want.mbrType) {
        Fail ("partition $($want.index) has moved or changed size: the gate recorded type " +
              "$($want.mbrType) at $($want.offsetBytes) for $($want.sizeBytes) bytes, the card now " +
              "reports type $([int]$have[0].MbrType) at $($have[0].Offset) for $($have[0].Size) bytes.")
    }
}

Say '  independent re-resolution agrees with the harness on every field.' 'Green'
Say ("    disk number : $($disk.Number)   <- read from the matched device, never typed")
Say ("    friendly    : $($disk.FriendlyName)")
Say ("    identity    : $($disk.UniqueId)")
Say ("    signature   : $signatureNow")
Say ("    partitions  : $($haveParts.Count), all at the fingerprinted offsets and sizes")
Say ("    read-only   : $($disk.IsReadOnly)  (a property of the disk, not of this script)")

# ---- 4. Where the bytes go ------------------------------------------------------------------
Say ''
Say '---- The image ---------------------------------------------------------' 'Cyan'

$startOffset = [int64]0
if (Test-Path -LiteralPath $imagePath) {
    if (-not $Force) {
        Fail ("there is already a finished image at $imagePath. A file with that name has passed " +
              'verification, and it is the thing this whole exercise exists to produce, so it is ' +
              'not overwritten by accident. Move it aside, or pass -Force.')
    }
    Say "  -Force: the existing $imagePath will be replaced once the new one verifies." 'Yellow'
}
if (Test-Path -LiteralPath $partialPath) {
    $partialLength = [int64](Get-Item -LiteralPath $partialPath).Length
    if ($Resume) {
        $startOffset = $partialLength - ($partialLength % $ChunkBytes)
        Say ("  -Resume: $partialPath holds " + (Format-Bytes $partialLength) + '.')
        Say ("           restarting at offset $startOffset (the last whole $ChunkBytes-byte chunk is re-read).")
    } elseif ($Force) {
        Remove-Item -LiteralPath $partialPath -Force
        Say '  -Force: the previous unfinished file was discarded.' 'Yellow'
    } else {
        Fail ("an unfinished $partialPath is already there, holding $partialLength bytes. It is " +
              'from an interrupted run. Continue it with -Resume, or discard it with -Force. This ' +
              'script will not silently decide which.')
    }
}
if ($startOffset -gt $capacityBytes) {
    Fail "the existing partial file is longer than the card. It is not an image of this card."
}

$driveLetter = (Split-Path -Qualifier $OutputDirectory).TrimEnd(':')
$freeBytes = [int64](Get-PSDrive -Name $driveLetter).Free
$needBytes = $capacityBytes - $startOffset + $FreeSpaceMarginBytes
Say ("  to read      : " + (Format-Bytes ($capacityBytes - $startOffset)))
Say ("  free on drive $driveLetter : " + (Format-Bytes $freeBytes))
if ($freeBytes -lt $needBytes) {
    Fail ("$driveLetter has " + (Format-Bytes $freeBytes) + ' free and this needs ' + (Format-Bytes $needBytes) + '.')
}

Say ''
Say '  This is a RAW image: every sector of the card in card order, uncompressed, including'
Say '  unallocated space and anything deleted but not yet overwritten. That is the point - a'
Say '  used-blocks-only capture would be far smaller and would silently discard the parts of'
Say '  the card nobody has thought to want yet.'
Say ''
Say '  Nothing is written to the card. Nothing is written anywhere except the image file, its'
Say '  receipt, its checksum file and this log.'

if ($DryRun) {
    Say ''
    Say 'DRY RUN - stopping here. The card was never opened.' 'Green'
    Say "Run it for real from an elevated PowerShell to produce $imagePath."
    exit 0
}

# ---- 5. Elevation ---------------------------------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail ('reading a raw disk needs elevation, and this prompt is not elevated. Start PowerShell ' +
          'with "Run as administrator" and run it again. -DryRun works unelevated and rehearses ' +
          'everything up to this point.')
}

# ---- 6. Read the card -----------------------------------------------------------------------
Say ''
Say '---- Reading -----------------------------------------------------------' 'Cyan'

$capturedUtc = Utc
$cardShaFirst = $null
$shareUsed = $null
$overallWatch = [System.Diagnostics.Stopwatch]::StartNew()

$card = $null
$out = $null
try {
    $card = Open-CardForRead -DevicePath $devicePath
    $cardStream = $card.Stream
    $shareUsed = $card.Share
    Say "  opened $devicePath for reading, sharing mode $shareUsed"
    if ($shareUsed -ne 'Read') {
        Say ('  (Windows would not grant exclusive read sharing while the boot partition is mounted. ' +
             'This handle is still read-only; the read-back pass is what catches anything that ' +
             'changes underneath it. Leave the card alone while this runs.)') 'Yellow'
    }

    $out = [System.IO.File]::Open($partialPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $out.SetLength($startOffset)
    $out.Position = $startOffset
    $cardStream.Position = $startOffset

    $hasher = $null
    if ($startOffset -eq 0) {
        $hasher = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    }

    $buffer = New-Object byte[] $ChunkBytes
    $position = $startOffset
    $readWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $lastSaid = [double]0
    while ($position -lt $capacityBytes) {
        $want = [int][math]::Min([int64]$ChunkBytes, $capacityBytes - $position)
        $filled = 0
        while ($filled -lt $want) {
            $got = $cardStream.Read($buffer, $filled, $want - $filled)
            if ($got -le 0) { break }
            $filled += $got
        }
        if ($filled -ne $want) {
            $out.Dispose()
            $out = $null
            Fail ("the card stopped returning data at offset $($position + $filled), with " +
                  "$($capacityBytes - $position - $filled) bytes still to go. The unfinished file " +
                  "has been left at $partialPath; continue it with -Resume once you know why.")
        }
        $out.Write($buffer, 0, $filled)
        if ($hasher) { $hasher.AppendData($buffer, 0, $filled) }
        $position += $filled

        Write-Progress -Activity "Reading $devicePath" -Status ("{0:N0} / {1:N0} bytes" -f $position, $capacityBytes) -PercentComplete ([int](100 * $position / $capacityBytes))
        if (($readWatch.Elapsed.TotalSeconds - $lastSaid) -ge $ProgressEverySeconds) {
            $lastSaid = $readWatch.Elapsed.TotalSeconds
            $rate = ($position - $startOffset) / $readWatch.Elapsed.TotalSeconds / 1MB
            $eta = [TimeSpan]::Zero
            if ($rate -gt 0) { $eta = [TimeSpan]::FromSeconds(($capacityBytes - $position) / ($rate * 1MB)) }
            Say ('    {0,5:N1}%  {1,15:N0} bytes  {2,6:N1} MB/s  elapsed {3}  eta {4}' -f (100 * $position / $capacityBytes), $position, $rate, (Format-Span $readWatch.Elapsed), (Format-Span $eta))
        }
    }
    $out.Flush($true)
    $readWatch.Stop()
    if ($hasher) {
        $cardShaFirst = [BitConverter]::ToString($hasher.GetHashAndReset()).Replace('-', '').ToLowerInvariant()
    }
    Say ("  read " + (Format-Bytes ($capacityBytes - $startOffset)) + " in " + (Format-Span $readWatch.Elapsed))
} catch [System.IO.IOException] {
    Say ''
    Say "READ ERROR: $($_.Exception.Message)" 'Red'
    Say ''
    Say 'An I/O error part-way through a raw read is usually a bad sector on the card. The'
    Say "unfinished file is still at $partialPath and nothing was written to the card."
    Say 'A card with a bad sector cannot be imaged by this script, which stops at the first'
    Say 'unreadable block on purpose rather than quietly substituting zeros. Recovering one'
    Say 'needs a tool built for it - ddrescue on a Linux machine, which retries, records every'
    Say 'region it could not read, and can be re-run to fill gaps.'
    exit 1
} finally {
    Write-Progress -Activity "Reading $devicePath" -Completed
    if ($out) { $out.Dispose() }
    if ($card) { $card.Stream.Dispose() }
}

# ---- 7. Hash the file that was written, independently ---------------------------------------
Say ''
Say '---- Hashing the file --------------------------------------------------' 'Cyan'
$fileLength = [int64](Get-Item -LiteralPath $partialPath).Length
if ($fileLength -ne $capacityBytes) {
    Fail "the image is $fileLength bytes and the card is $capacityBytes. It is not complete; it has been left at $partialPath."
}
$fileHandle = [System.IO.File]::Open($partialPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
try {
    $fileSha = Get-StreamSha256 -Stream $fileHandle -Bytes $capacityBytes -Activity 'Hashing the image file'
} finally {
    $fileHandle.Dispose()
}
Say "  file sha256 : $fileSha"

# ---- 8. Read the card a second time and compare ---------------------------------------------
$cardShaSecond = $null
if ($SkipReadBack) {
    Say ''
    Say '---- Read-back SKIPPED -------------------------------------------------' 'Yellow'
    Say '  -SkipReadBack was given, so the card was read once and never re-read. The hash above'
    Say '  proves the FILE is intact and matches the bytes this run took off the card. It does'
    Say '  NOT prove the card reads the same way twice, which is the failure a marginal card'
    Say '  actually produces. This image will be recorded as UNVERIFIED and the register will go'
    Say '  on treating the card as irreplaceable.'
} else {
    Say ''
    Say '---- Reading the card again to compare ---------------------------------' 'Cyan'
    Say '  A hash of a file proves the file is intact. It cannot prove the file matches the card,'
    Say '  because both hashes would come from the same single read. This pass reads all of the'
    Say '  card a second time, from a new handle, and hashes it independently.'
    $second = $null
    try {
        $second = Open-CardForRead -DevicePath $devicePath
        $second.Stream.Position = 0
        $cardShaSecond = Get-StreamSha256 -Stream $second.Stream -Bytes $capacityBytes -Activity "Verifying $devicePath"
    } finally {
        if ($second) { $second.Stream.Dispose() }
    }
    Say "  card sha256 : $cardShaSecond"
}

# ---- 9. The verdict -------------------------------------------------------------------------
Say ''
Say '---- Verdict -----------------------------------------------------------' 'Cyan'
$hashes = @{}
if ($cardShaFirst)  { $hashes['card, first read']  = $cardShaFirst }
if ($cardShaSecond) { $hashes['card, second read'] = $cardShaSecond }
$hashes['image file'] = $fileSha
foreach ($key in $hashes.Keys) { Say ("  {0,-18} {1}" -f $key, $hashes[$key]) }

$distinct = @($hashes.Values | Sort-Object -Unique)
$allAgree = ($distinct.Count -eq 1) -and ($hashes.Count -ge 2)
if (-not $allAgree) {
    Say ''
    Say 'THE HASHES DO NOT ALL AGREE.' 'Red'
    Say "The unfinished file has been left at $partialPath and has NOT been renamed, so nothing"
    Say 'here can be mistaken for a verified image. Nothing was written to the card.'
    Say 'If the two card reads differ from each other, the card is reading unstably and needs'
    Say 'ddrescue on Linux rather than this script. If the file differs from both, the write to'
    Say 'the workstation disk is at fault.'
    exit 1
}

# One last look at the card, resolved by identity rather than by the disk number it had an hour
# ago, because a number is exactly what stops meaning the same thing across a re-enumeration.
$finalDisk = @(Get-Disk -ErrorAction SilentlyContinue | Where-Object { $_.UniqueId -eq $expectedUniqueId })
if ($finalDisk.Count -ne 1) {
    Fail ("the reader no longer presents exactly one disk with identity $expectedUniqueId " +
          "($($finalDisk.Count) found). The card was removed or re-enumerated while this ran, so " +
          "the image at $partialPath is not trusted and has not been renamed.")
}
$finalSignature = ('{0:x8}' -f ($finalDisk[0].Signature -band 0xFFFFFFFF))
if ($finalSignature -ne $expectedSignature -or $finalDisk[0].Size -ne $capacityBytes) {
    Fail ("the card in the reader now reports signature $finalSignature at $($finalDisk[0].Size) " +
          "bytes, and this run began on $expectedSignature at $capacityBytes bytes. It was swapped " +
          "while this ran. The image has been left at $partialPath and is not trusted.")
}

if (Test-Path -LiteralPath $imagePath) { Remove-Item -LiteralPath $imagePath -Force }
Move-Item -LiteralPath $partialPath -Destination $imagePath
$overallWatch.Stop()
Say ''
Say ("  verified. $imagePath is a byte-for-byte copy of the card.") 'Green'
Say ("  total elapsed: " + (Format-Span $overallWatch.Elapsed))

# ---- 10. The receipt, for the register ------------------------------------------------------
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

[System.IO.File]::WriteAllText($sha256Path, "$fileSha *$($Name).img`n", $utf8NoBom)

$receipt = [ordered]@{
    schema      = $ReceiptSchema
    imageId     = $ImageId
    cardId      = $CardId
    path        = $imagePath
    sizeBytes   = $capacityBytes
    sha256      = $fileSha
    format      = 'raw, uncompressed, whole device including the MBR and all unallocated space'
    capturedUtc = $capturedUtc
    completedUtc= (Utc)
    durationSeconds = [math]::Round($overallWatch.Elapsed.TotalSeconds, 1)
    capturedBy  = 'tools/harness/image-v1-card.ps1'
    workstation = ($env:COMPUTERNAME).ToLowerInvariant()
    resumed     = [bool]$Resume
    device      = [ordered]@{
        path          = $devicePath
        number        = $disk.Number
        uniqueId      = $disk.UniqueId
        friendlyName  = $disk.FriendlyName
        capacityBytes = $capacityBytes
        shareMode     = $shareUsed
        access        = 'Read'
    }
    sourceFingerprint = $gate.observed
    verification = [ordered]@{
        method = ('a second full read of the card through a new handle, hashed independently, ' +
                  'compared against a third independent hash of the written file')
        cardSha256FirstPass  = $cardShaFirst
        cardSha256SecondPass = $cardShaSecond
        fileSha256           = $fileSha
        allAgree             = $allAgree
        skipped              = [bool]$SkipReadBack
        verifiedUtc          = (Utc)
    }
    gate = $gate
}
[System.IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 12), $utf8NoBom)

Say ''
Say '---- Done --------------------------------------------------------------' 'Cyan'
Say ("  image    : $imagePath")
Say ("  sha256   : $fileSha")
Say ("  checksum : $sha256Path")
Say ("  receipt  : $receiptPath")
Say ("  log      : $script:LogPath")
Say ''
Say 'Record it in the card register - this is what stops the register claiming the card is the'
Say 'only copy:'
Say ''
Say "    python `"$flPy`" cards image --receipt `"$receiptPath`"" 'Green'
Say ''
Say 'Then put the image on a second medium and say so, which is what turns one copy into a'
Say 'backup:'
Say ''
Say "    python `"$flPy`" cards image --image $ImageId --add-copy '<where it now also lives>'" 'Green'
exit 0
