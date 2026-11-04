# `cryptin` for Linux — tester notes

A command-line client for the Crypt-in hardware dongle, built for x86-64 Linux.
One self-contained file: no .NET runtime to install.

## Read this first: set up on Windows, activate here or there

**The dongle is set up from Windows, with the CryptinPersonal app**: flashing
the firmware and handing over your 24 words and PIN happen there. This Linux
build cannot do either.

**The beta licence can then be activated from here**, if the app did not
already do it:

```bash
cryptin unlock          # the PIN, from the terminal or stdin
cryptin activate        # anonymous beta licence, written to the dongle
```

`activate` keeps a licence the dongle already holds, and says so; `--replace`
activates anyway. Exit code 13 means the licence server refused (it says why),
14 that it could not be reached.

- **Encrypting** needs an active licence on the dongle.
- **Decrypting never asks for a licence**: any `.crin` your dongle wrote can
  always be opened with it, and with your 24 recovery words even without it.
- **Files move freely between Linux and Windows**: a `.crin` written here
  opens in CryptinPersonal, and one written by CryptinPersonal opens here.

## Requirements

- x86-64 Linux with `libudev.so.1` (preinstalled on Ubuntu and most desktop
  distributions; on a minimal system: `sudo apt install libudev1`)
- your user in the `plugdev` group:
  ```bash
  sudo usermod -aG plugdev "$USER"     # then log out and back in
  ```

## Install

```bash
sudo install -m 755 cryptin /usr/local/bin/cryptin
sudo cp 99-cryptin.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules
sudo udevadm trigger --action=add --subsystem-match=hidraw
```

`--action=add` matters: a bare `udevadm trigger` does not update a device
node that already exists. Unplugging and replugging the dongle also works.

Check that you can reach the dongle **without sudo**:

```bash
cryptin status
```

Do not run `cryptin` as root to work around a permission error: fix the group
or the udev rule instead.

## Use

```bash
cryptin status                    # what is attached, locked or unlocked
cryptin unlock < pin.txt          # the PIN is read from stdin, never the command line
cryptin encrypt report.pdf        # writes report.pdf.crin
cryptin encrypt photos/           # a folder works too: writes photos.crin
cryptin decrypt report.pdf.crin   # writes report.pdf back
cryptin open-offline report.pdf.crin < words.txt   # no dongle: the 24 recovery words, from stdin
```

- Keep `pin.txt` and `words.txt` readable only by you (`chmod 600`) and
  delete them when done. Typing the PIN at the prompt also works.
- The dongle stays unlocked until it loses power or sits idle for ten minutes.
- **Three wrong PINs lock the dongle.** The attempt is counted before it is
  checked, so a correct PIN interrupted by unplugging still counts.
- `cryptin status` shows how long the licence lasts once unlocked:
  `license: valid until 2027-03-31 23:59 UTC`.
- **5 MB per file or folder.** Larger inputs are refused with exit code 10.
- Throughput is roughly 20–25 KB/s: a 1 MB file takes about 40–50 seconds.
- Decrypted files are readable only by you (mode 600); a decrypted folder is
  mode 700.
- On success stdout carries only the path written; every error goes to stderr.

## Exit codes

| Code | Meaning | Code | Meaning |
|---|---|---|---|
| 0 | ok | 7 | file belongs to a different dongle |
| 2 | usage, or recovery words not accepted | 8 | file damaged or tampered with |
| 3 | no dongle found | 9 | not a Crypt-in file |
| 4 | PIN required | 10 | over the transfer limit |
| 5 | dongle locked out | 11 | dongle error |
| 6 | no licence | 12 | filesystem error |
| | | 13 | licence server refused (`activate`) |
| | | 14 | licence server unreachable (`activate`) |
| | | 130 | interrupted (nothing is written) |

## If `status` says no dongle

1. Unplug and plug the dongle back in.
2. In a virtual machine: give the guest the device named **Crypt-in Dongle**
   (`303a:8123`). If the VM offers a *USB JTAG/serial debug unit* instead,
   decline, unplug and replug.
3. Check `ls -l /dev/hidraw*`: the dongle's node should belong to group
   `plugdev` with mode `crw-rw----`.
4. If `lsusb` shows the dongle as `303a:1001` (*USB JTAG/serial debug unit*)
   and replugging does not help, run `cryptin recover`: it reboots the chip
   into its application and the dongle comes back locked. Your user needs the
   `dialout` group for this one (`sudo usermod -aG dialout "$USER"`), and
   `--port /dev/ttyACM0` names the port if the search does not find it.
