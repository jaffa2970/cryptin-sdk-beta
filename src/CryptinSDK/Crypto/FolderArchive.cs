// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.IO.Compression;
using System.Security.Cryptography;
using CryptinSDK.Dongle;

namespace CryptinSDK.Crypto;

/// <summary>
/// A folder compressed into memory, never onto disk.
///
/// <para><b>Why this exists.</b> Encrypting a folder used to compress it into a
/// temporary file under the system temp directory, read that file back, and
/// delete it in a <c>finally</c>. Measured on 2026-08-06: the <c>finally</c>
/// runs on every path that has one — success, cancellation, and the too-large
/// exception — but a process killed outright never reaches it, and the ZIP
/// survives as a live file that nothing will ever clean up. Killed during the
/// HID transfer it was a complete, readable archive; killed during compression
/// it was 96 MB of truncated ZIP still carrying 49 of 60 file names and their
/// data. "Not a valid archive" is not a defence.</para>
///
/// <para>Two more problems went with it, both present even when nothing crashed:
/// <c>File.Delete</c> frees a directory entry rather than the data, so the
/// blocks stay recoverable until reused — and on an SSD, wear levelling makes
/// overwriting them unreliable too — and the copy landed on the system drive
/// even when the folder being protected lived on an encrypted volume or a
/// removable disk. Keeping the plaintext in memory removes all three at once,
/// because there is no file.</para>
///
/// <para><b>This is not a new design.</b> Of the three paths that handle a
/// plaintext, two already worked this way: a single file is read with
/// <c>File.ReadAllBytes</c>, and decrypting a folder unzips from a
/// <c>MemoryStream</c>. Encrypting a folder was the exception, and the comment
/// that justified it — "a folder has no size limit worth assuming" — was
/// answered by the next line, which read the whole archive into a
/// <c>byte[]</c> regardless. The temporary file bought no protection against a
/// large folder; it only added a copy on disk.</para>
///
/// <para><b>⚠️ The ceiling is what makes this safe, and it is load-bearing.</b>
/// <see cref="HidConstants.MaxEncryptBytes"/> is documented as a policy limit
/// about time — three minutes per transfer at 27.8 KB/s — but with the archive
/// held in memory it also became the thing that keeps the plaintext out of the
/// page file. A 5 MB array on an ordinary machine is not paged out. A 500 MB one
/// under memory pressure is, and hibernation writes physical memory to disk by
/// construction. So removing the check does not yield "the same but slower": it
/// puts the plaintext back on disk, in a place the user can neither see nor
/// clean, which is worse than the temporary file this replaced — that one at
/// least had a name.</para>
///
/// <para><b>If you are raising the ceiling, the answer is not a temporary
/// file.</b> It is to compress straight into the chunks the transfer sends, so
/// the plaintext is never materialised whole — not on disk and not in memory.
/// Until that exists, this type refuses to build an archive larger than the
/// ceiling, and it refuses while compressing rather than afterwards.</para>
///
/// <para>Holding it does not make the plaintext safe from everything: it stays
/// readable in this process's memory for as long as the lease is alive, and
/// <see cref="Dispose"/> only clears the copy this type owns. Locking the pages
/// (<c>VirtualLock</c>, <c>mlock</c>) would narrow the page-file window further
/// but not close it, since hibernation writes locked pages too. At this ceiling
/// the exposure is small and the mitigation would be partial, so it is
/// documented rather than engineered.</para>
/// </summary>
public sealed class FolderArchive : IDisposable
{
    private byte[]? _buffer;
    private readonly int _length;

    private FolderArchive(byte[] buffer, int length)
    {
        _buffer = buffer;
        _length = length;
    }

    /// <summary>
    /// Compresses <paramref name="folderPath"/> into memory.
    /// </summary>
    /// <param name="folderPath">The folder to archive. Its own name is not
    /// included as a top-level entry — the container has no field for a name, so
    /// the caller carries it on the file name instead.</param>
    /// <param name="maximumBytes">The ceiling, in bytes. Compression stops and
    /// throws as soon as the archive would pass it, so a folder far larger than
    /// the ceiling costs the ceiling in memory and not its own size.</param>
    /// <exception cref="CryptinFileTooLargeException">The archive does not fit
    /// under the ceiling. The exact size is not reported because it is not
    /// known: finding it out would mean compressing the whole folder, which is
    /// the work being refused.</exception>
    public static FolderArchive Create(string folderPath,
                                       int maximumBytes = HidConstants.MaxEncryptBytes)
    {
        // Allocated once, at the ceiling. A MemoryStream left to grow on its own
        // doubles its buffer and abandons the old one, and every abandoned buffer
        // is a partial copy of the plaintext left on the heap that nothing zeroes
        // and nothing can reach to zero. One allocation means one copy, which is
        // also the only copy Dispose is able to clear.
        var memory = new MemoryStream(maximumBytes);

        try
        {
            // ZipFile.CreateFromDirectory rather than walking the folder here, so
            // the archive is laid out exactly as the temporary-file version laid
            // it out: same entry names, same order, same handling of empty
            // directories. This change is about where the bytes live, not about
            // what they are.
            using (var capped = new CappedStream(memory, maximumBytes))
                ZipFile.CreateFromDirectory(folderPath, capped, CompressionLevel.Optimal,
                                            includeBaseDirectory: false);

            // GetBuffer, not ToArray: ToArray would make a second copy of the
            // plaintext that this type does not hold a reference to and therefore
            // could never clear.
            return new FolderArchive(memory.GetBuffer(), (int)memory.Length);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(memory.GetBuffer());
            throw;
        }
    }

    /// <summary>The archive bytes. Valid until <see cref="Dispose"/>.</summary>
    public ReadOnlySpan<byte> Bytes
        => _buffer is null
            ? throw new ObjectDisposedException(nameof(FolderArchive))
            : _buffer.AsSpan(0, _length);

    /// <summary>Clears the plaintext. Above 85 KB the buffer lives on the large
    /// object heap, which is not compacted by default, so without this the
    /// archive stays readable in the process long after the operation ended —
    /// until something else happens to be allocated over it.</summary>
    public void Dispose()
    {
        if (_buffer is null) return;
        CryptographicOperations.ZeroMemory(_buffer);
        _buffer = null;
    }

    /// <summary>
    /// Fails the write that would pass the ceiling, instead of letting the
    /// archive grow and rejecting it afterwards.
    ///
    /// Seeking is forwarded rather than refused. <see cref="ZipArchive"/> in
    /// create mode writes different bytes for a stream it cannot seek — it falls
    /// back to data descriptors — and the archive is the plaintext that gets
    /// encrypted, so changing its layout would be a change in behaviour smuggled
    /// into a change about where it is stored.
    /// </summary>
    private sealed class CappedStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _maximum;

        public CappedStream(Stream inner, int maximum)
        {
            _inner = inner;
            _maximum = maximum;
        }

        private void Guard(long wouldEndAt)
        {
            if (wouldEndAt > _maximum)
                throw new CryptinFileTooLargeException(_maximum);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Guard(_inner.Position + count);
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Guard(_inner.Position + buffer.Length);
            _inner.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Guard(_inner.Position + 1);
            _inner.WriteByte(value);
        }

        public override void SetLength(long value)
        {
            Guard(value);
            _inner.SetLength(value);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() => _inner.Flush();

        // The MemoryStream outlives this wrapper: Create still needs its buffer
        // after the ZipArchive has been disposed.
        protected override void Dispose(bool disposing) { }
    }
}
