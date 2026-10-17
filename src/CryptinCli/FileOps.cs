// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.IO.Compression;
using CryptinSDK.Crypto;
using CryptinSDK.Dongle;

namespace CryptinCli;

/// <summary>
/// Turning a path into an encrypted file and back.
///
/// ⚠️ **This mirrors CryptinPersonal/FileOperations.cs and must keep mirroring
/// it.** The two cannot share code today: that one lives in a WinForms project
/// targeting net9.0-windows, which a Linux CLI cannot reference. The naming
/// rules are the part that must not drift — if one appends `.crin` and the other
/// does something subtly different, a file encrypted with the app and decrypted
/// with the CLI lands somewhere the user did not expect.
///
/// The honest fix is to move this into CryptinSDK. That was deliberately not
/// done when the app was written ("building the archive is a filesystem
/// concern", CRYPTIN_ARCHITETTURA_CS.md §4), and reversing an architecture
/// decision belongs in its own change rather than smuggled into a new project.
///
/// The rule that forces the scheme: **neither container stores the original file
/// name.** The header is magic, version, flags, public key, salt and nonce, and
/// nothing else. So the name on disk is the only thing carrying it — append the
/// extension going in, strip it coming out. Anything cleverer loses the name.
/// </summary>
internal static class FileOps
{
    public const string CrinExtension = ".crin";
    public const string CryptinExtension = ".cryptin";

    /// <summary>Does this path look like something to decrypt? Only a hint for
    /// choosing the direction — the reader still dispatches on the magic, so a
    /// renamed file is opened correctly anyway.</summary>
    public static bool LooksEncrypted(string path)
        => File.Exists(path)
           && (path.EndsWith(CrinExtension, StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(CryptinExtension, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Encrypts a file or a folder to &lt;name&gt;.crin beside the original.
    /// A folder is zipped first and gets flag bit 0.
    /// </summary>
    public static string Encrypt(string sourcePath, CryptinDongle dongle,
                                 IProgress<int>? progress, CancellationToken ct)
    {
        var isFolder = Directory.Exists(sourcePath);
        if (!isFolder && !File.Exists(sourcePath))
            throw new FileNotFoundException("the path does not exist", sourcePath);

        // A folder is compressed in memory, never through a file on disk — see
        // FolderArchive for what a temporary file cost and why the 5 MB ceiling
        // is what makes holding it safe.
        using var archive = isFolder ? FolderArchive.Create(sourcePath) : null;

        var blob = archive is not null
            ? CryptinCipher.WriteCrin(dongle, archive.Bytes, true, progress, ct)
            : CryptinCipher.WriteCrin(dongle, File.ReadAllBytes(sourcePath), false, progress, ct);

        var target = Unique(sourcePath.TrimEnd(Path.DirectorySeparatorChar) + CrinExtension);
        File.WriteAllBytes(target, blob);
        return target;
    }

    /// <summary>Decrypts through the dongle. No licence is consulted, ever.</summary>
    public static string Decrypt(string sourcePath, CryptinDongle dongle,
                                 IProgress<int>? progress, CancellationToken ct)
    {
        var content = CryptinCipher.ReadCrin(dongle, File.ReadAllBytes(sourcePath), progress, ct);

        // Strip a known extension; if it has neither, the file was renamed and
        // we fall back to a suffix rather than overwriting the encrypted original.
        return WritePlaintext(sourcePath, content);
    }

    /// <summary>
    /// Decrypts with file_secret alone — derived from the 24 words, no dongle.
    /// Same naming as <see cref="Decrypt"/>.
    ///
    /// The header's own public key is passed as "the dongle's": without the
    /// dongle there is no K_identity to compare against (deriving it would need
    /// Ed25519, which .NET does not have), so step 7 cannot tell "another
    /// dongle's file" from "damaged file" here. Both end as a tag failure, and
    /// the caller has to word it that way.
    /// </summary>
    public static string DecryptOffline(string sourcePath, ReadOnlySpan<byte> fileSecret)
    {
        var blob = File.ReadAllBytes(sourcePath);
        var layout = CrinFormat.Detect(blob);
        var headerKey = blob.Length >= layout.PublicKeyOffset + CrinFormat.PublicKeyLength
            ? blob.AsSpan(layout.PublicKeyOffset, CrinFormat.PublicKeyLength).ToArray()
            : new byte[CrinFormat.PublicKeyLength];

        var content = CryptinCipher.Read(blob, fileSecret, headerKey);
        return WritePlaintext(sourcePath, content);
    }

    private static string WritePlaintext(string sourcePath, CryptinContent content)
    {
        var target = LooksEncrypted(sourcePath)
            ? Path.Combine(Path.GetDirectoryName(sourcePath) ?? "",
                           Path.GetFileNameWithoutExtension(sourcePath))
            : sourcePath + ".decrypted";

        target = Unique(target);

        if (!content.IsFolder)
        {
            File.WriteAllBytes(target, content.Plaintext);
            return target;
        }

        Directory.CreateDirectory(target);
        using var archive = new ZipArchive(new MemoryStream(content.Plaintext), ZipArchiveMode.Read);
        archive.ExtractToDirectory(target);
        return target;
    }

    /// <summary>Never overwrite. A command is one gesture and an overwrite is not
    /// undoable, so a collision gets a new name instead of a prompt the user
    /// would answer without reading — and in a script there is nobody to ask.</summary>
    private static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}
