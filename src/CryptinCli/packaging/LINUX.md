# `cryptin` su Linux — installazione e verifica

> **Riverificata il 2026-11-03** su Ubuntu 26.04.1 LTS (VM VMware), contro Krypt `bb6ccbf`, con β
> `14C19F22897C` (identità nuova del rekey G2 del 30/10, firmware `2e1bbef`, licenza 4 del clone).
> Formato `.crin` v2 e `open-offline` eseguiti su Linux per la prima volta. Esiti caso per caso in
> §3; il rapporto completo è in Tartufo (progetto `cryptin`, argomento «CLI e SDK su Linux»).
> Il pomeriggio dello stesso giorno: casi 20-21 (`.crin` scambiati con Windows nei due versi) ✅, e
> `reset` eseguito col ponte CH343 nella VM, anche col **solo** binario di distribuzione ✅.
> **Pubblicata il 2026-11-03** nella release
> [`cli-linux-2026-11-03`](https://github.com/jaffa2970/Crypt-in-BETA/releases/tag/cli-linux-2026-11-03)
> di Crypt-in-BETA, separata e **non Latest**: tar `37bd4de3…05b8` (binario `77ad14c7…f0e9`, da `551e75b`),
> SBOM `cd009ff8…58fa`, `SHA256SUMS-cli-linux-2026-11-03.txt`.
> Le misure precedenti (α, firmware e formato v1) sono del 5-6/08 e stanno in `STATO_LAVORI.md` §8.

Linux è **la piattaforma per cui questa CLI esiste**: è il deliverable del 30
settembre ed è il motivo per cui `CryptinSDK` è stata rilicenziata Apache-2.0.

⚠️ **`activate` (dal 04/11, non ancora in un pacchetto pubblicato).** Il pacchetto `cli-linux-2026-11-03` non
ce l'ha: lì il dongle arriva già attivato da Windows. Il codice nuovo (`Activation.cs`) fa l'attivazione beta
anonima come l'app, e senza `--replace` tiene una licenza valida già sul chip. ✅ Visto su Windows contro il clone il 04/11: i rifiuti (bloccato, già licenziato, `ALREADY_ACTIVATED` → exit 13)
e **un'attivazione riuscita** su γ (exit 0, token sul chip, poi cifra e decifra). ✅ **Su Linux il 04/11** (§3, casi
A1-A6, build RedTeam di `645a594` su β contro il clone): tutti i rifiuti e i codici d'uscita; un'attivazione
*riuscita* su Linux di proposito no (costerebbe una licenza in più sul clone). `recover` col solo binario ✅ con
`--port` lo stesso giorno; ⛔ senza `--port` la ricerca muore su `/dev/ttyS1` (§1, §3). Il setup (flash, parole, PIN) resta su Windows. La decifratura non chiede mai licenza; la
cifratura sì.

---

## 1. Cosa resta non provato su Linux

| Cosa | Perché conta |
|---|---|
| **`recover` *senza* `--port` con un dongle venduto** | ✅ con `--port` il 04/11 (§3). Senza, la ricerca moriva su `/dev/ttyS1` della VM (`ArgumentException` → exit 11): corretto lo stesso giorno, ora le `ttyS*` si saltano (B3). Ma col CH343 collegato aprire `ttyACM0` sembra resettare β prima che la ricerca arrivi alla sua porta, quindi l'esito vero senza CH343 — dove la porta del chip è l'unica `ttyACM` — non è misurato |
| **`recover` sull'incastro vero** | Il 04/11 `1001` è stato ottenuto col download mode (DTR/RTS dal CH343), non con la gara al boot: con `VMUSBArbService` avviato e sette riattacchi β non c'è mai caduto. Il percorso ROM → watchdog è lo stesso; la causa no |
| **`recover` fuori dalla VM** | Inferito, non misurato. Tutte le prove Linux sono passate dall'arbitratore USB di VMware |
| **PIN da terminale (senza eco)** | Il codice c'è (`Program.cs`, ramo `!Console.IsInputRedirected`); le prove hanno usato sempre stdin rediretto |
| **`open-offline` con il dongle fisicamente staccato** | Eseguito con β collegata, ma `strace -e openat` mostra **zero** aperture di `hidraw` o `libudev`: il comando non tocca il dongle |
| **`kill -9` durante la scrittura finale** | La CLI scrive il risultato con un solo `File.WriteAllBytes` a operazione finita, quindi un'interruzione durante il trasferimento non lascia file. Nell'istante della scrittura stessa un file troncato resta possibile |

---

## 2. Ambiente

VM VMware con Ubuntu 26.04.1 LTS. **Una sola presa** passata al guest, quella
nativa (`303a:8123`, «Crypt-in Dongle (PoC)»): è l'unica che ha un dongle spedito.
Il ponte CH343 (`1a86:55d3`) serve solo per `reset`.

🔴 **Leggere il nome che VMware offre.** Se il dialogo propone *USB JTAG/serial
debug unit*, il chip è incastrato in `303a:1001` per colpa dell'arbitratore
(dimostrato il 06/08): annullare, staccare, riattaccare. Il 03/11 β è arrivata
come `303a:8123` al primo tentativo.

1. dentro la VM:
   ```bash
   sudo apt update && sudo apt install -y libudev1 git python3-pip
   sudo usermod -aG plugdev,dialout "$USER"     # poi disconnettersi e rientrare
   ```
   ⚠️ **`apt install dotnet-sdk-9.0` non funziona sulle Ubuntu recenti**: sulla
   26.04 i repo offrono solo la serie 10, e il progetto è `net9.0`. Non sostituire
   col runtime 10.
   ```bash
   curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0
   export DOTNET_ROOT="$HOME/.dotnet"
   export PATH="$DOTNET_ROOT:$PATH"          # metterle in ~/.bashrc
   ```
   🔴 Senza `DOTNET_ROOT` ogni comando esce **131** con `Failed to resolve
   libhostfxr.so`. Vale solo per il binario di lavoro: quello di distribuzione
   è self-contained e gira con `env -i PATH=/usr/bin:/bin` (misurato 03/11).
2. le regole udev:
   ```bash
   sudo cp 99-cryptin.rules /etc/udev/rules.d/
   sudo udevadm control --reload-rules
   sudo udevadm trigger --action=add --subsystem-match=hidraw   # un trigger nudo NON basta
   ls -l /dev/hidraw*        # il nodo del dongle: crw-rw---- root plugdev
   ```
   ⚠️ **Regge `GROUP="plugdev"`, non `TAG+="uaccess"`.** Misurato il 05/08 e di
   nuovo il 03/11: `getfacl` sul nodo mostra solo le voci di base, nessuna ACL
   utente. Una regola con solo `uaccess` lascerebbe il dongle illeggibile.
3. il binario di lavoro (framework-dependent):
   ```bash
   cd cryptin-sdk
   dotnet publish src/CryptinCli -c Release -r linux-x64 --self-contained false -o out
   ./out/cryptin --help
   ```

🔴 **Mai da root.** Una prova con `sudo` scavalca proprio il permesso che le
regole udev concedono, e non dimostra niente.

### Il binario che si spedisce

```bash
dotnet publish src/CryptinCli -c Release -r linux-x64 \
       -p:PublishSingleFile=true -p:SelfContained=true -o ../dist/linux-x64
```

Un file solo (71,6 MB il 03/11), nessun runtime da installare. Il publish lascia
accanto `cryptin.pdb` e `CryptinSDK.pdb`: **non vanno nel pacchetto**. Il tar
(owner `root/root`, una cartella `cryptin-linux-x64-<data>-<commit>/`) contiene:

- `cryptin`, `99-cryptin.rules`, `README-LINUX.md`;
- `LICENSE` e `NOTICE` di questa cartella: Apache-2.0 §4 li vuole accanto a un
  binario ridistribuito;
- `licenses/dotnet-runtime-LICENSE.TXT` e `licenses/dotnet-runtime-THIRD-PARTY-NOTICES.TXT`,
  cioè `LICENSE.TXT` e `THIRD-PARTY-NOTICES.TXT` del runtime pack **della versione
  nel binario** (`~/.nuget/packages/microsoft.netcore.app.runtime.linux-x64/<versione>/`,
  9.0.18 il 03/11). Il `NOTICE` li cita per nome: se mancano, il `NOTICE` mente.

Accanto al tar va un `SHA256SUMS` in formato `sha256sum`, col tar, la SBOM e
ogni file come sta dentro il tar.

🔴 **La SBOM descrive il binario di distribuzione**, e non si legge dal
single-file (il `.deps.json` finisce dentro). Serve un terzo publish,
self-contained ma disteso, come inventario:

```bash
dotnet publish src/CryptinCli -c Release -r linux-x64 \
       -p:PublishSingleFile=false -p:SelfContained=true -o ../dist/_inventory-linux-x64
cd .. && python3 scripts/make-sbom-cli.py --rid linux-x64 \
       --inventory dist/_inventory-linux-x64 --artifact dist/linux-x64/cryptin \
       --out dist/cryptin-cli-linux-x64-sbom.json --date <AAAA-MM-GG>T00:00:00Z
```

`--date` è obbligatorio di proposito: presa dall'orologio, la SBOM cambierebbe a
ogni esecuzione anche a componenti identici. Il 03/11: SPDX-2.3, 8 pacchetti,
`libudev` e OpenSSL dichiarati come dipendenze esterne.

🔴 **Ogni binario nuovo vuole una SBOM nuova**, anche a dipendenze identiche: la
SBOM porta lo SHA e la dimensione di `cryptin`, e il binario cambia a ogni commit
perché il commit è scritto nella versione (`1.0.0+<sha>`). Il 03/11 due
ricostruzioni del tar l'hanno lasciata indietro (`STATO_LAVORI.md` §9). E
l'inventario va pubblicato **dallo stesso commit** del binario: se `HEAD` è andato
avanti, da un `git worktree` su quel commit.

---

## 3. Checklist ed esiti del 2026-11-03

Exit code, stdout e stderr registrati separatamente. «stdout vuoto» vale per
gli errori di `encrypt`/`decrypt`/`open-offline`; `status` stampa sempre il suo
resoconto su stdout (`dongle: none` compreso), per scelta di `cdaa35b`.

### Senza dongle

| # | Comando | Atteso | 03/11 |
|---|---|---|---|
| 1 | `cryptin status` | exit **3**, `dongle: none` | ✅ |
| 2 | `cryptin --help` | exit **0**, uso su stdout | ✅ |
| 3 | `cryptin bruciapelo` | exit **2**, stdout vuoto | ✅ |

### β collegata, bloccata

| # | Comando | Atteso | 03/11 |
|---|---|---|---|
| 4 | `cryptin status` | exit **4**, `state: locked`, seriale | ✅ primo contatto `hidraw` col firmware `2e1bbef`, da non-root |
| 5 | `cryptin encrypt f` | exit **4**, nessun `.crin` | ✅ |
| 6 | `cryptin decrypt /non/esiste` | exit **2** | ✅ |

### β sbloccata

| # | Comando | Atteso | 03/11 |
|---|---|---|---|
| 7 | `cryptin unlock < pin` | exit **0**, poi `status` → `unlocked`, `license: valid until … UTC` | ✅ primo tentativo. La scadenza in `status` è stata aggiunta lo stesso giorno (`2027-03-31 23:59 UTC` su β) |
| 8 | round trip 100 KB | 0/0, SHA-256 identico | ✅ 6,2 s + 6,2 s |
| 9 | round trip 1 MB | 0/0, SHA-256 identico | ✅ 41,6 s cifra (**25 KB/s**), 50,2 s decifra (**21 KB/s**); v1 ad agosto ~27 KB/s |
| 10 | round trip cartella annidata (con file vuoto) | albero e contenuti identici | ✅ |
| 11 | versione del `.crin` | byte 4 = **2**; flags bit 0 = 1 solo per la cartella | ✅ |
| 12 | un byte del ciphertext alterato | exit **8**, nessun file | ✅ |
| 13 | un byte della K_identity nell'header alterato | exit **7**, nessun file | ✅ |
| 14 | file che non è un container | exit **9** | ✅ |
| 15 | file da 5,1 MB | exit **10**, nessun `.crin` | ✅ |
| 16 | SIGINT a 10 s su 1 MB, cifra e decifra | exit **130**, nessun file | ✅ entrambi; anche `kill -9` (137): nessun file, β risponde subito dopo |
| 17 | `open-offline` con le 24 parole da stdin | exit 0, SHA-256 identico | ✅ file e cartella; nessuna apertura di `hidraw` (strace) |
| 18 | `open-offline` con parole sbagliate | errore pulito, nessun file | ✅ ordine sbagliato → exit **2** («does not check out»); seme valido di un altro → exit **8** |
| 19 | dopo 10 minuti fermo, `status` | `locked`, exit 4 | ✅ ultimo comando 12:00:44, `locked` alle 12:12:01 |

### Col binario di distribuzione, da solo in una cartella vuota, senza .NET nell'ambiente

Casi 4, 7, 8 e 17 ripetuti con `env -i HOME=… PATH=/usr/bin:/bin`: ✅. Il 7 due
volte: la prima su β già sbloccata (`already unlocked`, il PIN non viene letto),
la seconda dopo l'auto-lock, col PIN verificato davvero. Chiude il
🟡 di agosto «copiare il solo `cryptin` in una cartella vuota» per tutto
tranne `reset`/`recover` (§1).

### Con Windows — casi 20-21, 03/11 pomeriggio

| # | Verso | Atteso | 03/11 |
|---|---|---|---|
| 20 | `.crin` di CryptinPersonal **RedTeam** (`bb6ccbf`, 204.800 byte) aperto su Linux | SHA-256 del chiaro identico | ✅ con `cryptin decrypt` da β sbloccata **e** con `open-offline` dalle 24 parole |
| 21 | `.crin` della CLI (102.400 byte) aperto da CryptinPersonal beta del 01/11 | SHA-256 del chiaro identico | ✅ (sessione Windows, Tartufo `c65d7f8f`) |

Header del file Windows: `CRIN`, v2, flags 0, la stessa K_identity dei file Linux.
⚠️ Per **cifrare** con β su Windows serve la build RedTeam: la beta chiede la
licenza alla produzione, e la licenza 4 sta sul clone. Decifrare passa sempre.

### `activate` — 04/11, build RedTeam di `645a594`, β contro il clone

Il binario `Release` parla con la produzione: per attivare si usa solo `dotnet build -c RedTeam`
(`redteam-license.lake8.dev`, lo dice anche la riga «activating with …» su stderr).

| # | Comando | Atteso | Esito |
|---|---|---|---|
| A1 | `cryptin activate` con β bloccata | exit **4**, «dongle is locked — run 'cryptin unlock' first» | ✅ |
| A2 | `unlock < pin`, poi `cryptin activate` | exit **2**, «already has a valid license until 2027-03-31 — nothing was changed», nessuna chiamata al server | ✅ sotto `strace -f -e trace=connect`: **zero** `connect()` |
| A3 | `cryptin activate --replace` | exit **13**, «already has an active license on the server — nothing was changed» | ✅ 11:38:44Z, verso `redteam-license.lake8.dev` |
| A4 | `cryptin activate --boh` | exit **2**, «unknown option» | ✅ |
| A5 | `nmcli networking off`, `cryptin activate --replace` | exit **14**, «license server unreachable» | ✅ rete riaccesa subito dopo |
| A6 | `cryptin status` alla fine | `license: valid until 2027-03-31 23:59 UTC` | ✅ invariata |

Un comando `lock` non esiste: la sessione sbloccata finisce coi 10 minuti di inattività o
togliendo corrente.

### `recover` col solo binario di distribuzione — 04/11

`cryptin` del tar `551e75b` (SHA256SUMS verificato), da solo in una cartella vuota, `env -i HOME=… PATH=/usr/bin:/bin`.
β in `303a:1001` messo **in download mode** dal CH343 (la sequenza DTR/RTS «classic reset» di esptool, con
pyserial): la USB nativa ricompare come «USB JTAG/serial debug unit», `/dev/ttyACM1`. L'arbitratore di VMware
non c'è riuscito (§1).

| # | Comando | Esito |
|---|---|---|
| B1 | `cryptin recover --port /dev/ttyACM1` | ✅ «recovered on /dev/ttyACM1 — the dongle is back, and locked», exit **0** in 8 s; poi `303a:8123`, `status` → `locked` |
| B2 | `cryptin recover` senza `--port` (due volte) | ⛔ `ttyACM0` (il CH343) provato, `ttyACM1` «held by another process, skipped», poi `ArgumentException: Value does not fall within the expected range.` su `/dev/ttyS1` → exit **11**. β torna `8123` lo stesso: probabilmente il SYNC è arrivato alla ROM dalla UART0 via CH343 e il watchdog è partito da lì, e ACM1 è sparita sotto la ricerca (ipotesi) |
| B3 | come B2, con `ArgumentException` presa nel ciclo di `Recover` (build RedTeam, stesso giorno) | ✅ niente exit 11: ogni `ttyS*` «Value does not fall within the expected range., skipped», poi «no dongle answered on any port» → exit **3**. β è tornato `8123` lo stesso: la ricerca non se ne accorge. Probabilmente aprire il CH343 (`ttyACM0`) muove EN e β riparte normale prima che la ricerca arrivi a `ttyACM1` (ipotesi, non misurata). È un effetto del banco: un dongle venduto non ha CH343 |

`libSystem.IO.Ports.Native.so` estratta in `$HOME/.net/cryptin/<hash>/`, come per `reset`. ModemManager è attivo
nella VM e apre le `ttyACM` nuove per qualche secondo: prima di `--port`, `fuser` sulla porta.

⚠️ **A5 non va rifatto con `nmcli networking off`.** Riaccesa la rete, NetworkManager non
rimette l'indirizzo gateway ai bridge di Docker: i container restano healthy ma l'host non
li raggiunge più (il 04/11: Tartufo, il license-server locale e gli altri, per una ventina di
minuti). Si ripara con `sudo ip addr add <gateway>/<prefisso> dev <bridge>` per ogni rete
bridge di `docker network inspect`. Per la prossima volta: rendere irraggiungibile solo il
clone per la CLI (`/etc/hosts` o un namespace di rete), non tutta la VM.

### `reset` — solo con il cavo UART

| # | Comando | Atteso |
|---|---|---|
| R1 | `cryptin reset` **senza** `--port` | `ListBridgePorts()` trova il CH343 in `/dev/serial/by-id/` e lo usa senza chiedere (✅ 06/08; ✅ 03/11 pomeriggio da sbloccato, β di nuovo `locked`, prima con la build `out/` e poi col solo binario di distribuzione, sotto) |
| R2 | `cryptin reset --port /dev/ttyUSB0` da sbloccato | exit **0**, il dongle torna `locked` (✅ 06/08) |
| R3 | `cryptin reset --port /dev/ttyS0` | exit **11**, «never left the bus» |

**Col solo binario di distribuzione (03/11 pomeriggio)** ✅: `cryptin` estratto dal tar
`953c99c`, da solo in una cartella vuota, `env -i HOME=… PATH=/usr/bin:/bin`. `status`
`locked` (4) → `unlock` (0) → `unlocked` → `reset` senza `--port` (0, in 2 s, «dongle is
back, and locked») → `status` `locked` (4). `libSystem.IO.Ports.Native.so` viene estratta
in `$HOME/.net/cryptin/<hash>/`: serve una `HOME` scrivibile. Ripetuto sul binario del tar
`bc2b486`, con in più un round trip da 50 KB, e su quello del tar `551e75b` con
`umask 002` (chiari a 600, cartella a 700): ✅.

---

## 4. Da annotare comunque

- il **throughput**: decide se il tetto dei 5 MB va alzato. Col v2 è sceso un
  poco rispetto al v1 (25/21 KB/s contro ~27);
- se `/dev/hidraw*` compare con permessi diversi, **il numero del nodo e il
  proprietario**, non solo «non funziona»;
- se HidSharp non trova il dongle, controllare `libudev.so.1` *prima* di
  sospettare il dispositivo: un backend che non carica e un dispositivo assente
  danno lo stesso sintomo;
- i chiari scritti da `decrypt` e `open-offline` sono **600**, e una cartella
  decifrata è **700** (03/11, deciso da Tommy). Prima seguivano l'umask: 664 sulla
  VM, leggibili dagli altri utenti. Il modo è dato alla creazione, non con un
  `chmod` dopo. Dentro la cartella i file restano col modo dello zip o dell'umask:
  li protegge la cartella. Misurato con `umask 002`: file 600, cartella 700, e
  una collisione di nome dà `(2)` ancora a 600. Il `.crin` scritto da `encrypt`
  segue ancora l'umask: è cifrato.
