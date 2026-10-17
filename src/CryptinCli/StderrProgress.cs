// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
namespace CryptinCli;

/// <summary>
/// Draws progress on stderr, and only when stderr is a terminal.
///
/// At the measured 27.8 KB/s a 1 MB file takes over half a minute, which is far
/// past the point where a silent command reads as a hang. But the same carriage
/// returns that make a live percentage work turn a redirected stderr into a file
/// full of `\r` and a hundred repetitions of the same line — so when nobody is
/// watching, this says nothing at all and lets the exit code do the talking.
///
/// The SDK already reports only on a change of whole percent, so this is called
/// at most 101 times per file.
/// </summary>
internal sealed class StderrProgress(string label) : IProgress<int>, IDisposable
{
    private readonly bool _live = !Console.IsErrorRedirected;
    private int _last = -1;
    private bool _drew;

    public void Report(int percent)
    {
        if (!_live || percent == _last) return;
        _last = percent;
        _drew = true;

        // Padded so a shrinking number cannot leave a digit of the previous one
        // stranded at the end of the line.
        Console.Error.Write($"\r{label}  {percent,3}%   ");
    }

    /// <summary>Clears the line so the shell prompt, or whatever the caller
    /// prints next, does not land on top of a stale percentage.</summary>
    public void Done()
    {
        if (!_live || !_drew) return;
        Console.Error.Write($"\r{new string(' ', label.Length + 12)}\r");
        _drew = false;
    }

    /// <summary>Also on the way out: an exception mid-transfer must not leave
    /// half a progress line in front of the error message.</summary>
    public void Dispose() => Done();
}
