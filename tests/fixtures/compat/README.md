# Compatibility fixtures — synchronised copy

**This is a copy. The original is `Krypt/tests/fixtures/compat/`.**

It exists because `cryptin-sdk` becomes its own repository (`lake8dev/cryptin-sdk`,
opening 2026-09-30) and the gate cannot reach outside it once that happens. Of
the three ways to solve that, this is the one that was chosen:

| | Why not |
|---|---|
| git submodule | would expose the private `Krypt` as a public dependency |
| a package | new infrastructure for a handful of files |
| **a copy, checked** | duplication, but the divergence is caught mechanically |

A copy is only safe if something notices when it stops matching. That something
is `Krypt/scripts/check-protocol-mirrors.py`, which compares every file here
against the original by SHA-256 and refuses the commit if any differ. It runs
from the pre-commit hook, so a drifted fixture cannot be committed by accident.

## What is here, and what deliberately is not

Copied: `manifest.json`, `crin/`, `crin-dongle/`, `crin-v2/` (since 2026-10-09), `crin-chip/` and `seed-vectors.json` (since 2026-10-11), `cryptin/`, `plaintext/` —
everything the C# gate reads. `crin-dongle/` (since 2026-09-30) holds the test
vectors of the dongle derivation, the one the chip actually writes.

Not copied, and not by omission:

- **`verify_fixtures.py`** implements the reader from the specification, so what
  it verifies is `CRYPTIN_FILE_FORMAT.md`. That document lives in `Krypt`, so
  the gate that checks it stays there too. The gate here calls the SDK, and
  therefore verifies the SDK.
- **`generator/`** links `KriptC#/CryptIn/Crypto/CrinFormat.cs` to produce these
  files from the code being superseded. It cannot work without that checkout,
  and regeneration belongs where the legacy code is.

## If you need to change a fixture

Change it in `Krypt`, then run `Krypt/scripts/sync-to-sdk.sh`. Editing this copy
is the one thing that turns a checked duplicate back into an unchecked one — the
SHA-256 comparison will refuse the commit, which is the intended outcome, not an
obstacle to work around.
