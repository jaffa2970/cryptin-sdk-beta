using System.IO.Compression;
using CryptinSDK.Crypto;
using CryptinSDK.Dongle;

namespace CryptinPersonal;

/// <summary>
/// Turning a dropped path into an encrypted file and back.
///
/// This is where zipping lives, deliberately not in the SDK: the container has a
/// flag saying "the payload is a ZIP of a folder" and the SDK reads and writes
/// that flag honestly, but building the archive is a filesystem concern.
///
/// It is also where the **file name** lives, and that is not a detail. Neither
/// container stores the original name — the header is magic, version, flags,
/// public key, salt and nonce, and nothing else. So the name on disk is the only
/// thing carrying it, which forces the scheme below: append the extension on the
/// way in, strip it on the way out. Anything cleverer loses the name.
/// </summary>
public static class FileOperations
{
    public const string CrinExtension = ".crin";
    public const string CryptinExtension = ".cryptin";

    /// <summary>Does this path look like something to decrypt rather than encrypt?
    /// Only a hint for choosing the direction — the reader still dispatches on the
    /// magic, so a renamed file is opened correctly anyway.</summary>
    public static bool LooksEncrypted(string path)
        => File.Exists(path)
           && (path.EndsWith(CrinExtension, StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(CryptinExtension, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Encrypts a file or a folder to <c>&lt;name&gt;.crin</c> beside the original.
    /// A folder is zipped first and gets flag bit 0.
    /// </summary>
    /// <summary>
    /// Encrypts through the dongle, which checks the licence itself.
    ///
    /// The secret never reaches this process: that is the difference from the
    /// overload below, and the reason this one exists. Encrypting host-side with
    /// a secret the dongle handed over would leave the licence check in the
    /// firmware bypassed by its own client.
    ///
    /// <paramref name="progress"/> and <paramref name="cancellationToken"/> go
    /// straight to the SDK, which is where the slow part is. Nothing is written
    /// until the whole blob exists, so cancelling leaves no partial file on disk.
    /// Since 2026-08-06 there is no scratch ZIP to leave either — a folder is
    /// compressed in memory, see FolderArchive.
    /// </summary>
    public static string Encrypt(string sourcePath, CryptinDongle dongle,
                                 IProgress<int>? progress = null,
                                 CancellationToken cancellationToken = default)
    {
        var isFolder = Directory.Exists(sourcePath);
        if (!isFolder && !File.Exists(sourcePath))
            throw new FileNotFoundException("the path does not exist", sourcePath);

        // In memory, never through a file on disk — see FolderArchive.
        using var archive = isFolder ? FolderArchive.Create(sourcePath) : null;

        var blob = archive is not null
            ? CryptinCipher.WriteCrin(dongle, archive.Bytes, true, progress, cancellationToken)
            : CryptinCipher.WriteCrin(dongle, File.ReadAllBytes(sourcePath), false,
                                      progress, cancellationToken);

        var target = Unique(sourcePath.TrimEnd(Path.DirectorySeparatorChar) + CrinExtension);
        File.WriteAllBytes(target, blob);
        return target;
    }

    /// <summary>Decrypts through the dongle. No licence is consulted.</summary>
    public static string Decrypt(string sourcePath, CryptinDongle dongle,
                                 IProgress<int>? progress = null,
                                 CancellationToken cancellationToken = default)
    {
        var content = CryptinCipher.ReadCrin(
            dongle, File.ReadAllBytes(sourcePath), progress, cancellationToken);
        return WriteDecrypted(sourcePath, content);
    }

    /// <summary>
    /// Kept for the host-side path and for tests that hold a secret directly.
    /// Not used by the app any more — see the dongle overload above.
    /// </summary>
    public static string Encrypt(string sourcePath, byte[] fileSecret, byte[] identityPubKey)
    {
        var isFolder = Directory.Exists(sourcePath);
        if (!isFolder && !File.Exists(sourcePath))
            throw new FileNotFoundException("the path does not exist", sourcePath);

        // In memory here too, even though this overload is only reached by tests:
        // a scratch file left by the unused path would be just as readable as one
        // left by the used path.
        using var archive = isFolder ? FolderArchive.Create(sourcePath) : null;

        var blob = archive is not null
            ? CryptinCipher.WriteCrin(archive.Bytes, fileSecret, identityPubKey, true)
            : CryptinCipher.WriteCrin(File.ReadAllBytes(sourcePath), fileSecret,
                                      identityPubKey, false);

        var target = Unique(sourcePath.TrimEnd(Path.DirectorySeparatorChar) + CrinExtension);
        File.WriteAllBytes(target, blob);
        return target;
    }

    /// <summary>
    /// Decrypts to the path with the encrypted extension stripped. A folder
    /// payload is unzipped into a directory of that name.
    /// </summary>
    /// <exception cref="WrongDongleException">The file belongs to another dongle.
    /// The caller must report this differently from a corrupt file — the two need
    /// opposite advice.</exception>
    /// <exception cref="CorruptedCryptinFileException">Tag mismatch.</exception>
    public static string Decrypt(string sourcePath, byte[] fileSecret, byte[] identityPubKey)
    {
        var blob = File.ReadAllBytes(sourcePath);
        var content = CryptinCipher.Read(blob, fileSecret, identityPubKey);
        return WriteDecrypted(sourcePath, content);
    }

    /// <summary>Shared by both decrypt paths: naming and unzipping do not depend
    /// on where the plaintext came from.</summary>
    private static string WriteDecrypted(string sourcePath, CryptinContent content)
    {
        // Strip a known extension; if it has neither, the file was renamed and we
        // fall back to a suffix rather than overwriting the encrypted original.
        var target = LooksEncrypted(sourcePath)
            ? Path.Combine(Path.GetDirectoryName(sourcePath)!, Path.GetFileNameWithoutExtension(sourcePath))
            : sourcePath + ".decrypted";

        if (!content.IsFolder)
        {
            target = Unique(target);
            File.WriteAllBytes(target, content.Plaintext);
            return target;
        }

        target = Unique(target);
        Directory.CreateDirectory(target);
        using var archive = new ZipArchive(new MemoryStream(content.Plaintext), ZipArchiveMode.Read);
        archive.ExtractToDirectory(target);
        return target;
    }

    /// <summary>Never overwrite. A drop is one gesture and an overwrite is not
    /// undoable, so a collision gets a new name instead of a confirmation the
    /// user would click through anyway.</summary>
    private static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}
