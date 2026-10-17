// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.Reflection;
using System.Security.Cryptography;

namespace CryptinSDK.Seed;

/// <summary>
/// The BIP-39 English wordlist and the checksum over a 24-word phrase.
///
/// The wordlist in `Resources/` is a **copy** of `Krypt/firmware/bip39_english.txt`,
/// the same file the firmware compiles in. A copy, because this project becomes
/// its own repository and cannot reach into Krypt from there; checked, because a
/// copy nobody compares is a copy that will drift. `check-protocol-mirrors.py`
/// compares the two by SHA-256 on every commit.
///
/// Drift here would be the worst kind: a phrase the dongle accepts and the app
/// rejects, or the reverse, found by a user in the middle of a recovery.
///
/// Checking each word against the list is not enough on its own. A wrong word
/// that happens to be in the dictionary passes every per-word check and produces
/// a valid-looking phrase; only the checksum catches it. With 24 words that is 8
/// bits, so it lets one bad phrase through in 256 rather than none at all — but
/// it turns the common case, a mistyped or transposed word, into an error the
/// user sees now instead of after flashing a chip.
/// </summary>
public static class Bip39
{
    public const int WordCount = 24;

    private static readonly Lazy<string[]> Words = new(Load);
    private static readonly Lazy<Dictionary<string, int>> Index = new(
        () => Words.Value.Select((w, i) => (w, i)).ToDictionary(x => x.w, x => x.i, StringComparer.Ordinal));

    private static string[] Load()
    {
        using var stream = Assembly.GetExecutingAssembly()
                               .GetManifestResourceStream("bip39_english.txt")
                           ?? throw new InvalidOperationException("the BIP-39 wordlist is not embedded");
        using var reader = new StreamReader(stream);

        var words = reader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (words.Length != 2048)
            throw new InvalidOperationException(
                $"the BIP-39 wordlist has {words.Length} words, 2048 expected");
        return words;
    }

    /// <summary>True when the word exists in the list. Used for live per-field
    /// feedback while typing, before there are 24 of anything.</summary>
    public static bool IsWord(string word)
        => word.Length > 0 && Index.Value.ContainsKey(Normalize(word));

    public static string Normalize(string word) => word.Trim().ToLowerInvariant();

    /// <summary>Candidate completions, for the type-ahead on each cell.</summary>
    public static IEnumerable<string> StartingWith(string prefix)
    {
        prefix = Normalize(prefix);
        return prefix.Length == 0
            ? []
            : Words.Value.Where(w => w.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Encodes 32 bytes of entropy as the 24 words.
    ///
    /// The inverse of what <see cref="Validate"/> decodes, and the reason the
    /// dongle sends entropy rather than words over HID: 24 words do not fit in a
    /// 63-byte report, and every client that offers recovery already needs this
    /// wordlist anyway. Rendering happens here so the wire stays canonical.
    /// </summary>
    public static string EntropyToMnemonic(ReadOnlySpan<byte> entropy)
    {
        if (entropy.Length != 32)
            throw new ArgumentException(
                $"32 bytes of entropy are needed, there are {entropy.Length}", nameof(entropy));

        // 256 bits + the first 8 bits of SHA-256 over them = 264 = 24 x 11.
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(entropy, digest);

        var words = new string[WordCount];
        for (var w = 0; w < WordCount; w++)
        {
            var index = 0;
            for (var b = 0; b < 11; b++)
            {
                var bit = w * 11 + b;
                var value = bit < 256
                    ? (entropy[bit / 8] >> (7 - bit % 8)) & 1
                    : (digest[0] >> (7 - (bit - 256))) & 1;
                index = (index << 1) | value;
            }
            words[w] = Words.Value[index];
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// Validates the whole phrase: 24 known words, then the BIP-39 checksum.
    /// </summary>
    /// <returns>null when the phrase is good, otherwise what is wrong with it.</returns>
    public static string? Validate(IReadOnlyList<string> words)
    {
        Span<byte> entropy = stackalloc byte[32];
        try
        {
            return Decode(words, entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    /// <summary>
    /// The 24 words back to the 32 bytes of entropy they encode — the seed the
    /// dongle derives everything from (firmware <c>bip39_mnemonic_to_entropy</c>,
    /// then <c>Keystore::deriveFromEntropy</c>). Same decoder as
    /// <see cref="Validate"/>, so the two cannot disagree on a phrase.
    /// </summary>
    /// <exception cref="ArgumentException">The phrase does not validate; the
    /// message says why.</exception>
    public static byte[] MnemonicToEntropy(IReadOnlyList<string> words)
    {
        var entropy = new byte[32];
        var error = Decode(words, entropy);
        if (error is null) return entropy;
        CryptographicOperations.ZeroMemory(entropy);
        throw new ArgumentException(error, nameof(words));
    }

    private static string? Decode(IReadOnlyList<string> words, Span<byte> entropy)
    {
        entropy.Clear();
        if (words.Count != WordCount)
            return $"{WordCount} words are needed, there are {words.Count}";

        var indices = new int[WordCount];
        for (var i = 0; i < WordCount; i++)
        {
            if (!Index.Value.TryGetValue(Normalize(words[i]), out indices[i]))
                return $"word {i + 1} is not in the BIP-39 list";
        }

        // 24 words x 11 bits = 264 = 256 bits of entropy + 8 of checksum.
        var bit = 0;
        byte checksum = 0;

        foreach (var index in indices)
        {
            for (var b = 10; b >= 0; b--)
            {
                var value = (index >> b) & 1;
                if (bit < 256)
                {
                    if (value != 0) entropy[bit / 8] |= (byte)(0x80 >> (bit % 8));
                }
                else
                {
                    checksum = (byte)((checksum << 1) | value);
                }
                bit++;
            }
        }

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(entropy, digest);

        return digest[0] == checksum
            ? null
            : "all 24 words are there but the phrase does not check out: "
              + "one is wrong or out of order";
    }
}
