// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.Diagnostics;
using System.Text.Json;

namespace CryptinSDK.Updater;

/// <summary>
/// Runs <c>CryptinUpdater.exe</c> as a separate process and reads JSON from its
/// standard output.
///
/// **The process boundary is the point, not an implementation detail.** This
/// assembly is GPL-3.0 and the updater is proprietary and signed, so there is no
/// P/Invoke, no project reference and no closed library loaded into this address
/// space. A child process with JSON on stdout was chosen over a named pipe
/// because an outside reviewer can verify it with the tools they already have —
/// which, in a GPL context, is not a detail either.
///
/// Everything needed to build a replacement updater is here: the executable
/// name, the arguments, the line protocol and the exit codes. What is not here
/// is the implementation, which stays closed and signed by lake8.dev.
///
/// Wire protocol, one JSON object per line on stdout:
///
///   {"type":"progress","percent":0-100}   zero or more times
///   {"type":"error","message":"..."}      zero or more times
///   {"type":"done","result":"Success"}    exactly once, last
///
/// The result string is a <see cref="FlashResult"/> name. The exit code repeats
/// it — 0 for Success, non-zero otherwise — so a caller that cannot parse still
/// learns whether it worked.
///
/// Two commands: <c>first-flash</c> and <c>update-app</c> (see
/// <see cref="UpdateAppAsync"/>). One argument beyond <c>--port</c>:
/// <c>--force</c>, first-flash only, which suppresses the not-blank refusal.
/// Without it the updater probes the chip's nvs partition first and answers
/// <c>AlreadyProvisioned</c> having written nothing.
///
/// The `error` line carries text meant for a person and does not decide the
/// outcome; `done` does. Keeping them apart means the updater can explain a
/// failure without the parent having to guess a result from prose.
/// </summary>
public sealed class UpdaterProcess : IUpdater
{
    public const string ExecutableName = "CryptinUpdater.exe";

    private readonly string _executablePath;

    /// <param name="executablePath">Defaults to the updater sitting next to the
    /// calling application.</param>
    public UpdaterProcess(string? executablePath = null)
        => _executablePath = executablePath
                             ?? Path.Combine(AppContext.BaseDirectory, ExecutableName);

    public bool IsInstalled => File.Exists(_executablePath);

    public Task<FlashResult> FirstFlashAsync(string portName, IProgress<int>? progress = null,
                                             bool force = false)
        => RunAsync("first-flash", portName, progress, force);

    /// <summary>
    /// Updates the firmware of a dongle that is already set up, without touching
    /// its keys: the updater writes from 0xE000 on (otadata and the application)
    /// and leaves nvs alone, so seed, PIN, K_attestation and the licence token
    /// stay. It refuses, having written nothing, unless the chip's partition
    /// table matches the image's and the chip already is a dongle.
    ///
    /// Not on <see cref="IUpdater"/>: it exists only for -dev beta chips, which
    /// the ROM downloader still accepts. A burned chip has no such path, and an
    /// interface method would promise one.
    /// </summary>
    public Task<FlashResult> UpdateAppAsync(string portName, IProgress<int>? progress = null)
        => RunAsync("update-app", portName, progress, force: false);

    private async Task<FlashResult> RunAsync(string verb, string portName, IProgress<int>? progress,
                                             bool force)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);

        // Per run: callers keep one instance across attempts, and an error left
        // over from the previous one would be shown as the reason for this one.
        LastError = null;

        if (!IsInstalled)
            throw new UpdaterNotInstalledException(_executablePath);

        var startInfo = new ProcessStartInfo(_executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(verb);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(portName);
        if (force) startInfo.ArgumentList.Add("--force");

        using var process = Process.Start(startInfo)
                            ?? throw new UpdaterNotInstalledException(_executablePath);

        FlashResult? result = null;
        while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;

            if (Parse(line) is not { } message) continue;

            switch (message.Type)
            {
                case "progress": progress?.Report(message.Percent); break;
                case "done": result = message.Result; break;
                case "error": LastError = message.Message; break;
                // Anything else is the updater's own logging, and is ignored
                // rather than treated as a protocol violation: a future version
                // may say more than this one knows about.
            }
        }

        await process.WaitForExitAsync().ConfigureAwait(false);

        // The exit code is the fallback, not the authority: it cannot distinguish
        // SignatureInvalid from FlashFailed, which need different advice.
        return result ?? (process.ExitCode == 0 ? FlashResult.Success : FlashResult.FlashFailed);
    }

    /// <summary>
    /// The updater's last <c>error</c> line, if it sent one. Text for a person,
    /// not a decision: the outcome is the <see cref="FlashResult"/>.
    /// </summary>
    public string? LastError { get; private set; }

    private sealed record ParsedMessage(string? Type, int Percent, FlashResult Result, string? Message);

    private static ParsedMessage? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            var percent = 0;
            if (root.TryGetProperty("percent", out var p) && p.TryGetInt32(out var value))
                percent = Math.Clamp(value, 0, 100);

            var result = FlashResult.FlashFailed;
            if (root.TryGetProperty("result", out var r))
                Enum.TryParse(r.GetString(), ignoreCase: true, out result);

            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;

            return new ParsedMessage(type, percent, result, message);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
