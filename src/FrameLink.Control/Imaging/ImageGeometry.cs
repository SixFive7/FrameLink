using System.Buffers.Binary;
using System.Globalization;

namespace FrameLink.Control.Imaging;

/// <summary>One primary partition, as the MBR describes it.</summary>
/// <param name="Index">One-based partition number, matching what <c>fdisk -l</c> prints.</param>
/// <param name="PartitionType">The MBR type byte — <c>0x0c</c> for FAT32 LBA, <c>0x83</c> for Linux.</param>
/// <param name="OffsetBytes">Byte offset of the partition's first sector within the image file.</param>
/// <param name="LengthBytes">Length of the partition in bytes.</param>
public sealed record ImagePartition(int Index, byte PartitionType, long OffsetBytes, long LengthBytes)
{
    /// <summary>Byte offset one past the partition's last byte.</summary>
    public long EndBytes => OffsetBytes + LengthBytes;

    /// <summary>Whether this is one of the FAT types Raspberry Pi OS uses for <c>bootfs</c>.</summary>
    /// <remarks>
    /// The measured image uses <c>0x0c</c> (FAT32 LBA). The neighbouring FAT types are accepted
    /// too, because which one a vendor stamps is a formatting detail and getting it wrong here
    /// would mean refusing a perfectly good image rather than protecting anything.
    /// </remarks>
    public bool IsFat => PartitionType is 0x01 or 0x04 or 0x06 or 0x0b or 0x0c or 0x0e;

    /// <summary>Whether this is the Linux type Raspberry Pi OS uses for <c>rootfs</c>.</summary>
    public bool IsLinux => PartitionType is 0x83;
}

/// <summary>
/// The partition layout of a disk image, read from its master boot record.
/// </summary>
/// <remarks>
/// <para>
/// This is the arithmetic every write in <see cref="ImagePlan"/> is aimed by, and it is the one
/// piece of the generator that is pure managed code rather than an argument handed to somebody
/// else's tool. That is deliberate: <c>mcopy</c> is told <c>image@@8388608</c> and <c>debugfs</c>
/// is told <c>image?offset=545259520</c>, and neither has any way to notice that the number is
/// wrong. They will happily read a superblock out of the middle of a file and, in <c>debugfs</c>'s
/// case, exit 0 while doing it. The offsets therefore have to be <i>derived</i> from the image and
/// bounds-checked before either tool ever sees them.
/// </para>
/// <para>
/// Sector size is fixed at 512. The MBR has no field for it — it is a property of the medium, not
/// of the table — and every Raspberry Pi OS image, every SD card and every USB stick this will
/// ever meet is 512-byte-sectored. The check that actually protects the caller is not a sector
/// size guess but <see cref="TryRead(ReadOnlySpan{byte}, long, out ImageGeometry, out string)"/>'s
/// requirement that every partition lie wholly inside the file: a table read with the wrong unit
/// produces offsets that run off the end, and that is rejected.
/// </para>
/// <para>
/// Extended partitions are not followed. Raspberry Pi OS ships exactly two primary partitions and
/// the generator writes to both of them; an image laid out any other way is refused by
/// <see cref="Boot"/>/<see cref="Root"/> returning null rather than guessed at.
/// </para>
/// </remarks>
public sealed class ImageGeometry
{
    /// <summary>Bytes per sector. Fixed, see the remarks on this type.</summary>
    public const int SectorSize = 512;

    /// <summary>Size of the master boot record.</summary>
    public const int MasterBootRecordSize = 512;

    private const int TableOffset = 446;
    private const int EntrySize = 16;
    private const int EntryCount = 4;

    private ImageGeometry(IReadOnlyList<ImagePartition> partitions) => Partitions = partitions;

    /// <summary>Every non-empty primary partition, in table order.</summary>
    public IReadOnlyList<ImagePartition> Partitions { get; }

    /// <summary>The FAT partition Raspberry Pi OS mounts at <c>/boot/firmware</c>, or null.</summary>
    public ImagePartition? Boot => Partitions.FirstOrDefault(p => p.IsFat);

    /// <summary>The ext4 partition Raspberry Pi OS mounts at <c>/</c>, or null.</summary>
    public ImagePartition? Root => Partitions.FirstOrDefault(p => p.IsLinux);

    /// <summary>
    /// Reads the partition table, or explains why the bytes are not a partitioned disk image.
    /// </summary>
    /// <param name="masterBootRecord">The first <see cref="MasterBootRecordSize"/> bytes of the image.</param>
    /// <param name="imageLengthBytes">Total length of the image file.</param>
    /// <param name="geometry">The layout, when this returns true.</param>
    /// <param name="problem">A sentence fit to show an operator, when this returns false.</param>
    public static bool TryRead(
        ReadOnlySpan<byte> masterBootRecord,
        long imageLengthBytes,
        out ImageGeometry? geometry,
        out string? problem)
    {
        geometry = null;
        problem = null;

        if (masterBootRecord.Length < MasterBootRecordSize)
        {
            problem = $"The image is shorter than a master boot record ({masterBootRecord.Length} bytes).";
            return false;
        }

        // The only thing in an MBR that says "this is an MBR". Without it, everything below is
        // reading structure out of arbitrary bytes.
        if (masterBootRecord[510] != 0x55 || masterBootRecord[511] != 0xAA)
        {
            problem = "The image has no MBR boot signature, so it is not a partitioned disk image.";
            return false;
        }

        var partitions = new List<ImagePartition>(EntryCount);

        for (var index = 0; index < EntryCount; index++)
        {
            var entry = masterBootRecord.Slice(TableOffset + (index * EntrySize), EntrySize);
            var type = entry[4];
            var firstSector = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..12]);
            var sectorCount = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..16]);

            if (type == 0x00 || sectorCount == 0)
            {
                continue;
            }

            var offset = (long)firstSector * SectorSize;
            var length = (long)sectorCount * SectorSize;

            // The bounds check the two external tools cannot do for themselves. A partition that
            // claims to extend past the end of the file means the table was misread, the download
            // was truncated, or this is not the image it claims to be — and in every one of those
            // cases the right answer is to stop rather than to hand `debugfs` an offset.
            if (offset >= imageLengthBytes || length > imageLengthBytes - offset)
            {
                problem = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Partition {index + 1} runs from {offset} to {offset + length} in an image of "
                    + $"{imageLengthBytes} bytes, so the partition table does not describe this file.");
                return false;
            }

            partitions.Add(new ImagePartition(index + 1, type, offset, length));
        }

        if (partitions.Count == 0)
        {
            problem = "The image has an MBR signature but no partitions.";
            return false;
        }

        geometry = new ImageGeometry(partitions);
        return true;
    }

    /// <summary>Reads the geometry of an image file on disk.</summary>
    public static bool TryRead(string imagePath, out ImageGeometry? geometry, out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        geometry = null;

        var info = new FileInfo(imagePath);
        if (!info.Exists)
        {
            problem = $"There is no image at {imagePath}.";
            return false;
        }

        Span<byte> header = stackalloc byte[MasterBootRecordSize];
        using (var stream = File.OpenRead(imagePath))
        {
            var read = stream.ReadAtLeast(header, MasterBootRecordSize, throwOnEndOfStream: false);
            if (read < MasterBootRecordSize)
            {
                problem = $"The image at {imagePath} is only {read} bytes long.";
                return false;
            }
        }

        return TryRead(header, info.Length, out geometry, out problem);
    }
}
