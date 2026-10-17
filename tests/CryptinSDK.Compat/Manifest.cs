using System.Text.Json.Serialization;

namespace CryptinSDK.Compat;

/// <summary>
/// manifest.json, which the README of the fixtures calls "the contract".
/// Only the fields this gate consumes are modelled; the rest is documentation
/// for humans and is deliberately not mirrored here.
/// </summary>
internal sealed class Manifest
{
    [JsonPropertyName("synthetic_vectors")]
    public SyntheticVectors Vectors { get; init; } = new();

    [JsonPropertyName("cases")]
    public List<FixtureCase> Cases { get; init; } = [];
}

/// <summary>
/// Fixed, published, nobody's secrets — see the fixtures README. They exist so
/// the fixtures are byte-reproducible and the gate needs no dongle and no
/// particular chip.
/// </summary>
internal sealed class SyntheticVectors
{
    [JsonPropertyName("file_secret")]
    public string FileSecret { get; init; } = "";

    /// <summary>"the dongle that encrypted this".</summary>
    [JsonPropertyName("pubkey_a")]
    public string PubkeyA { get; init; } = "";
}

internal sealed class FixtureCase
{
    [JsonPropertyName("file")]
    public string File { get; init; } = "";

    /// <summary>"crin" or "cryptin".</summary>
    [JsonPropertyName("format")]
    public string Format { get; init; } = "";

    /// <summary>"dongle" (what the chip writes) or "legacy" (host-written
    /// before 2026-08-02). CRYPTIN_FILE_FORMAT.md §1. Absent means legacy:
    /// every case written before the field existed is one.</summary>
    [JsonPropertyName("derivation")]
    public string Derivation { get; init; } = "legacy";

    [JsonPropertyName("header_bytes")]
    public int HeaderBytes { get; init; }

    /// <summary>"plaintext", "folder" or "wrong-dongle".</summary>
    [JsonPropertyName("expect")]
    public string Expect { get; init; } = "";

    [JsonPropertyName("plaintext")]
    public string Plaintext { get; init; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = "";
}
