// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
namespace CryptinCli;

/// <summary>
/// Getting the PIN in without it ending up somewhere it outlives the command.
///
/// **There is no `--pin` flag, and adding one would undo this file.** Arguments
/// are world-readable in `ps` on a shared machine and land in shell history, so
/// a PIN passed that way survives long after the command that used it.
///
/// Returned as <c>char[]</c> and wiped by the caller, for the same reason
/// PinForm does it: a string cannot be overwritten — it is immutable, and the
/// GC may copy it while compacting, so every copy lives until collection with
/// no way to reach any of them.
/// </summary>
internal static class Pin
{
    /// <summary>
    /// Reads six digits from a terminal without echo, or a line from stdin when
    /// input is redirected.
    ///
    /// The length is not enforced here on purpose. The firmware answers
    /// ERR_PIN_BLOCKED *before* it checks length, so a client that rejected a
    /// malformed PIN locally would report "wrong PIN" for a dongle that is
    /// actually blocked — and send the user looking for a typo instead of the
    /// recovery they need.
    /// </summary>
    public static char[] Read()
    {
        if (Console.IsInputRedirected)
        {
            // A pipe. The line arrives as a string and nothing can be done about
            // that -- Console.In has no char-oriented line read. This is a
            // reduction, not an elimination, and saying so is better than
            // implying the PIN never existed as a string.
            var line = Console.In.ReadLine() ?? "";
            return line.Trim().ToCharArray();
        }

        Console.Error.Write("PIN: ");
        var digits = new List<char>(8);

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter) break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (digits.Count > 0) digits.RemoveAt(digits.Count - 1);
                continue;
            }

            // Ctrl-C arrives here as well as at the handler; treat it as a
            // refusal rather than an empty PIN, which would burn an attempt.
            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                throw new OperationCanceledException();

            if (!char.IsControl(key.KeyChar)) digits.Add(key.KeyChar);
        }

        Console.Error.WriteLine();

        var pin = digits.ToArray();
        // The List's own backing array keeps a copy until it is collected, so
        // clear it too. Same reduction, one fewer copy left lying around.
        for (var i = 0; i < digits.Count; i++) digits[i] = '\0';
        return pin;
    }
}
