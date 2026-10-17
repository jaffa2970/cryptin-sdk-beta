# Crypt-in SDK — beta sources

Host-side source code of [Crypt-in](https://cryptin.lake8.dev), a USB hardware
dongle that holds the keys your files are encrypted with. This repository is
everything that runs **on the computer**: the client library, the command-line
tool, the Windows application and the firmware flashing tool.

> **Beta.** This is the code of the public beta, published as it is. It is not
> a supported release, the API may change without notice, and there is no
> support channel for it. The ready-to-run beta (executables, firmware image,
> instructions) is in [Crypt-in-BETA](https://github.com/jaffa2970/Crypt-in-BETA)
> and on [cryptin.lake8.dev/beta/](https://cryptin.lake8.dev/beta/).

**The dongle firmware is not in this repository.** Its wire protocol and file
format are public (see [Specifications](#specifications)); its source is not
published at this time.

## What is here

| Path | What it is | Licence |
|---|---|---|
| `src/CryptinSDK/` | The client library: HID transport to the dongle, `.crin` file format, licence client, BIP-39 recovery phrase | Apache-2.0 |
| `src/CryptinCli/` | `cryptin`, the cross-platform command-line client (Linux, Windows, macOS) | Apache-2.0 |
| `src/CryptinPersonal/` | The Windows application (WinForms) | GPL-3.0 |
| `src/CryptinUpdater/` | Firmware flashing tool — **test build**, see below | GPL-3.0 |
| `tests/CryptinSDK.Compat/` | Compatibility gate: opens every fixture through the SDK | GPL-3.0 |
| `tests/CryptinSDK.Hardware/` | Harness for a real dongle — not an automated test | GPL-3.0 |
| `tests/fixtures/compat/` | Byte-exact `.crin` / `.cryptin` fixtures, all made with synthetic keys | — |

## Security model, in one paragraph

Nothing on the host is a security boundary. The keys never leave the dongle,
and every check that matters — PIN, attempt counter, licence, session lock —
runs **on the chip**. Any check in this code can be removed by recompiling it,
and that is by design: if removing one changed what an attacker can do, it
would be a bug in the firmware, not here. The full model, and the limits we
declare, are at [/docs/security-model/](https://cryptin.lake8.dev/docs/security-model/)
and [/docs/threat-model/](https://cryptin.lake8.dev/docs/threat-model/).

## Building

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).
`CryptinPersonal` targets `net9.0-windows` and builds on Windows only; the
library, the CLI and the tests build anywhere .NET 9 runs.

```
dotnet build CryptinSDK.sln -c Release
```

Three configurations:

| Configuration | Licence server |
|---|---|
| `Release`, `Debug` | production — `CryptinSDK.Licensing.LicenseClient.DefaultBaseUrl` |
| `RedTeam` | the red-team clone, `https://redteam-license.lake8.dev`, with the endpoint stamped on every window title |

`RedTeam` exists for the [red-team track](https://cryptin.lake8.dev/security/red-team-rules/):
an activation made with it writes to the clone and never to production. The
only difference is the `CRYPTIN_REDTEAM` symbol, read in one place,
`src/CryptinPersonal/LicenseEndpoint.cs`.

The CLI alone, as a single self-contained file for Linux (no .NET runtime
needed on the target):

```
dotnet publish src/CryptinCli -c Release -r linux-x64 \
       -p:PublishSingleFile=true -p:SelfContained=true -o dist/linux-x64
```

Linux needs a udev rule to reach the dongle without root:
`src/CryptinCli/packaging/99-cryptin.rules`, explained in
`src/CryptinCli/packaging/LINUX.md`.

### The firmware image is not included

The **Program** button of `CryptinPersonal` and `CryptinUpdater` write a
firmware image to the dongle, and this repository does not contain one. Take
`firmware/firmware.factory.bin` from the beta package and put it in a
`firmware/` folder beside the executable, or pass it with `--image`.
`CryptinUpdater` knows the SHA-256 of the image it was built for
(`BundledImageSha256` in `src/CryptinUpdater/Program.cs`) and accepts that file
wherever it sits.

### `CryptinUpdater` is a test build

It writes the image it is given and **verifies no signature**: it is the
reference implementation of the updater contract, meant for a development
dongle on a bench. The header of `src/CryptinUpdater/Program.cs` says what the
production updater verifies before flashing.

## Running the tests

```
dotnet run --project tests/CryptinSDK.Compat -c Release
```

Expected: `23 ok, 0 fail`, then `17 ok, 0 fail (writer)` and
`18 ok, 0 fail (seme)`. Exit code `0` when everything passes, `1` on any
failure, `2` if the fixtures cannot be found. The fixtures are located from the
first argument, then `CRYPTIN_COMPAT_FIXTURES`, then the nearest
`tests/fixtures/compat/` above the executable.

The fixtures are byte-exact, so `.gitattributes` keeps git from rewriting their
line endings. Do not remove that rule: on a Linux checkout without it, the
gate fails on files nobody touched.

`tests/CryptinSDK.Hardware` talks to a real dongle. It refuses any dongle whose
USB serial is not the one passed on the command line, reads the PIN from
`CRYPTIN_TEST_PIN` and stops at the first wrong PIN without retrying.
**A dongle allows three wrong PINs, ever.**

## Specifications

The code follows the published specifications. Where the two disagree, the
specification is right and the code has a bug.

- HID protocol: [/docs/protocol/](https://cryptin.lake8.dev/docs/protocol/)
- `.crin` file format: [/docs/crin-spec/](https://cryptin.lake8.dev/docs/crin-spec/)
- Third-party components: [/docs/sbom/](https://cryptin.lake8.dev/docs/sbom/)

The comments in the code often cite internal documents by name (for example
`CRYPTIN_PROTOCOL.md` or `STATO_LAVORI.md`) and are partly in Italian. Those
documents are not in this repository; the specifications above are their
public form.

## Reporting a vulnerability

Do not open a public issue. Follow the disclosure policy at
[/docs/security/](https://cryptin.lake8.dev/docs/security/), or write to
security@lake8.dev.

This repository does not take issues or pull requests during the beta.

## Licence

Two licences, split by folder:

- **Apache-2.0** — `src/CryptinSDK/` and `src/CryptinCli/`. Each folder has its
  own `LICENSE` and `NOTICE`, and every source file carries
  `SPDX-License-Identifier: Apache-2.0`.
- **GPL-3.0** — everything else, under the [`LICENSE`](LICENSE) at the root.

The direction matters: a GPL-3.0 application may link an Apache-2.0 library,
which is what `CryptinPersonal` does with `CryptinSDK`. The library and the CLI
carry the permissive licence because they are the parts meant for others to
build on.

Third-party components, none redistributed in this source tree:

| Component | Used by | Licence |
|---|---|---|
| [HidSharp](https://www.nuget.org/packages/HidSharp/) 2.6.4, © James F. Bellinger | `CryptinSDK` | Apache-2.0 |
| [System.IO.Ports](https://www.nuget.org/packages/System.IO.Ports/), © .NET Foundation | `CryptinSDK`, `CryptinPersonal` | MIT |

`src/CryptinSDK/Resources/bip39_english.txt` is the standard BIP-39 English
wordlist from [bitcoin/bips](https://github.com/bitcoin/bips/blob/master/bip-0039/english.txt).

Copyright 2026 lake8.dev.
