// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.Text.RegularExpressions;

namespace CryptinSDK.Licensing;

/// <summary>
/// Client-side validation of the registration fields, run **before** the
/// payment.
///
/// The server deliberately never rejects these: an activation with a valid
/// payment must not fail over a personal-data field, because at that point the
/// customer has already paid. So it truncates, or degrades a bad tax id to
/// "N/D", and the licence is created either way. That is the right behaviour
/// there and it is exactly why the checking has to happen here — before the
/// checkout is the last moment the user can still fix anything. A malformed
/// field discovered after payment is a support case; discovered here it is a
/// red label.
/// </summary>
public static partial class ActivationValidation
{
    // Limits from §8.1 of the API contract. They are the protocol's, not the
    // column's: licenze.piva is VARCHAR(20) but the wire caps vat_id at 16.
    public const int MaxEmail = 255;
    public const int MaxName = 100;
    public const int MaxCompany = 255;
    public const int MaxVatId = 16;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailShape();

    /// <summary>Italian codice fiscale: 6 letters, 2 digits, letter, 2 digits,
    /// letter, 3 alphanumerics, letter. Format only — the check character needs
    /// the full table and a typo in it degrades to "N/D" server-side anyway.</summary>
    [GeneratedRegex(@"^[A-Z]{6}\d{2}[A-Z]\d{2}[A-Z][0-9A-Z]{3}[A-Z]$")]
    private static partial Regex CodiceFiscale();

    /// <summary>EU VAT: two country letters then 2 to 12 alphanumerics.</summary>
    [GeneratedRegex(@"^[A-Z]{2}[0-9A-Z]{2,12}$")]
    private static partial Regex EuVat();

    /// <returns>null if fine, otherwise the message to show next to the field.</returns>
    public static string? Email(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return null;                 // optional everywhere

        // Not truncated but discarded, server-side, if it is too long — and a
        // truncated address is wrong rather than incomplete. Say so here instead.
        if (value.Length > MaxEmail) return $"too long (max {MaxEmail})";
        return EmailShape().IsMatch(value) ? null : "not a valid address";
    }

    public static string? Name(string value, string what)
        => value.Trim().Length > MaxName ? $"{what}: too long (max {MaxName})" : null;

    public static string? Company(string value)
        => value.Trim().Length > MaxCompany ? $"too long (max {MaxCompany})" : null;

    /// <summary>
    /// Codice fiscale or VAT number, in that order of likelihood: a private
    /// buyer has the first and not the second, which is why the field is
    /// labelled "Codice Fiscale / P.IVA" and not "P.IVA".
    /// </summary>
    public static string? VatId(string value)
    {
        value = Normalize(value);
        if (value.Length == 0) return null;
        if (value.Length > MaxVatId) return $"too long (max {MaxVatId})";

        if (CodiceFiscale().IsMatch(value)) return null;

        // Italian VAT: 11 digits, with or without the IT prefix.
        var digits = value.StartsWith("IT", StringComparison.Ordinal) ? value[2..] : value;
        if (digits.Length == 11 && digits.All(char.IsAsciiDigit))
            return ItalianVatChecksum(digits) ? null : "VAT number: wrong check digit";

        if (EuVat().IsMatch(value)) return null;

        return "not a valid tax code or VAT number";
    }

    public static string Normalize(string value)
        => value.Trim().Replace(" ", "").Replace(".", "").ToUpperInvariant();

    /// <summary>
    /// The Luhn-style check digit of an Italian VAT number. Worth implementing
    /// rather than waving through: it catches a transposed pair of digits, which
    /// is the typo people actually make, and which would otherwise reach the
    /// server, degrade to "N/D" in silence and surface months later on an
    /// invoice.
    /// </summary>
    private static bool ItalianVatChecksum(string digits)
    {
        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            var d = digits[i] - '0';
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        var check = (10 - sum % 10) % 10;
        return check == digits[10] - '0';
    }
}
