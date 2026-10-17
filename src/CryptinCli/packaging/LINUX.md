# `cryptin` su Linux — installazione e verifica

Linux è **la piattaforma per cui questa CLI esiste**: è il deliverable del 30
settembre, `/beta/` la elenca fra i Downloads, ed è il motivo per cui
`CryptinSDK` è stata rilicenziata Apache-2.0.

Ed è anche l'unica piattaforma su cui **non è mai stata eseguita**. Tutto quello
che segue è da fare, non da rileggere.

---

## 1. Quello che non è stato provato, in ordine di rischio

Prima della checklist, perché sapere *cosa* può rompersi cambia come si legge un
fallimento.

| Rischio | Perché |
|---|---|
| **`HidSharp` su `hidraw`** | Il backend Linux esiste nell'assembly (`LinuxHid`, `hidraw`, `libudev`), ma «il backend c'è» non è «parla con questo dongle». È l'ipotesi su cui poggia l'intera decisione .NET-contro-Python. |
| **`ListBridgePorts()`** | Su Windows **ritorna sempre vuoto** per costruzione, quindi il corpo che scandisce `/dev/serial/by-id/` non è mai stato eseguito da nessuno. Non è codice poco provato: è codice mai girato. |
| **Le regole udev** | `99-cryptin.rules` è scritto a partire dalla documentazione, non da un `hidraw` osservato negare l'accesso e poi concederlo. |
| **`System.IO.Ports` su `/dev/ttyUSB*`** | RTS via termios invece che via Win32. Il nativo (`libSystem.IO.Ports.Native.so`) viaggia nel publish, ma non è mai stato caricato. |
| **`libudev.so.1`** | Dipendenza di runtime di HidSharp, non del publish. Presente di default su Ubuntu; su una distro minimale o un container va installata (`libudev1`). |

---

## 2. Ambiente

VM VirtualBox con Ubuntu. Il dettaglio che semplifica tutto: **il dongle e il
ponte CH343 sono dispositivi full-speed USB 1.1**, quindi il controller OHCI
integrato basta a passarli — **niente Extension Pack**.

1. VM Ubuntu (Desktop o Server), USB abilitato su **USB 1.1 (OHCI)**
2. due filtri USB, così i dispositivi entrano nella VM da soli a ogni replug:
   - `303a:8123` — il dongle con l'applicazione in esecuzione
   - `1a86:55d3` — il ponte CH343, solo se serve provare `reset`
   - ⚠️ **non** filtrare `303a:1001`: è lo stesso connettore con il chip in
     USB-Serial/JTAG, e catturarlo nella VM complica il recupero invece di
     aiutarlo
3. dentro la VM:
   ```bash
   sudo apt update && sudo apt install -y libudev1 git python3-pip
   sudo usermod -aG plugdev,dialout "$USER"     # poi disconnettersi e rientrare
   ```
   **L'SDK, non il solo runtime**: il passo 5 compila. Se il binario arriva già
   pronto da un'altra macchina basta il runtime — ma compilare *dentro* la VM
   vale la pena, perché dimostra anche che il progetto si costruisce su Linux e
   non solo che ci gira un artefatto altrui.

   ⚠️ **`apt install dotnet-sdk-9.0` non funziona sulle Ubuntu recenti.** Sulla
   26.04 LTS i repo offrono solo la serie 10, e questo progetto è `net9.0`.
   Misurato il 2026-08-05: l'SDK 9 va preso dallo script ufficiale, e **non** va
   sostituito con il runtime 10 «tanto è compatibile», perché introdurrebbe una
   variabile estranea proprio nei risultati che la sessione deve produrre.

   ```bash
   curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0
   export DOTNET_ROOT="$HOME/.dotnet"
   export PATH="$DOTNET_ROOT:$PATH"          # metterle in ~/.bashrc
   ```

   🔴 **`DOTNET_ROOT` diventa obbligatorio** con l'SDK installato così: senza,
   ogni comando esce **131** con `Failed to resolve libhostfxr.so`. Non è un
   difetto della CLI e non capita con un `dotnet` da pacchetto, ma chi segue
   questa procedura alla lettera lo incontra al primo comando.
4. le regole udev:
   ```bash
   sudo cp 99-cryptin.rules /etc/udev/rules.d/
   sudo udevadm control --reload-rules && sudo udevadm trigger
   # staccare e riattaccare il dongle
   ls -l /dev/hidraw*        # deve essere leggibile senza sudo
   ```
5. il binario **per provare**, che è quello che serve a questa procedura:
   ```bash
   dotnet publish src/CryptinCli -c Release -r linux-x64 --self-contained false -o out
   ./out/cryptin --version
   ```

### ⚠️ Questo NON è il binario che si spedisce

Il comando qui sopra produce un artefatto **framework-dependent**: tre assembly,
e la macchina deve avere il runtime .NET 9. Va benissimo dentro la VM, dove
l'SDK c'è già ed è più veloce da ricostruire.

Il pacchetto per un beta tester è un altro, e la differenza non è cosmetica —
**un tester che deve prima installare .NET 9 su Ubuntu è un tester che non prova
il prodotto**:

```bash
dotnet publish src/CryptinCli -c Release -r linux-x64 \
       -p:PublishSingleFile=true -p:SelfContained=true -o dist/linux-x64
```

Un file solo, nessun runtime da installare. Deciso il 2026-08-06, e le due
righe convivono di proposito: una è lo strumento di lavoro, l'altra è il
prodotto. Al tester va **solo `cryptin`** — gli eventuali `.pdb` accanto sono
simboli di debug e non servono a eseguirlo.

⚠️ **«Un file solo» è diventato vero il 2026-08-06, e prima non lo era.**
Misurato: senza `IncludeNativeLibrariesForSelfExtract` il publish `linux-x64`
lasciava **`libSystem.IO.Ports.Native.so`** accanto al binario, ed è la libreria
che serve a `System.IO.Ports`. Un tester che copiava solo `cryptin` — cioè che
faceva esattamente quello che gli si diceva — otteneva `status`, `encrypt` e
`decrypt` funzionanti e **`reset` e `recover` rotti**: il sottoinsieme peggiore,
visto che `recover` è il comando per quando il dongle sembra assente. La
proprietà ora sta in `CryptinCli.csproj`, quindi la riga di comando qui sopra
basta da sola e non c'è un flag da ricordarsi.

🟡 **Da provare nella sessione Linux**: copiare il solo `cryptin` in una
cartella vuota e lanciare `cryptin reset --port /dev/ttyUSB0`. Che il `.so` sia
un file esterno è misurato; che la sua assenza rompesse `reset` è
un'**inferenza** su come funziona il P/Invoke, e non è stata eseguita.

🔴 **La SBOM descrive il secondo, non il primo.** Self-contained significa che
il runtime .NET viaggia *dentro* quello che spedisci, quindi entra
nell'inventario dei componenti: la SBOM di un publish self-contained ha
centinaia di voci Microsoft, quella di un framework-dependent ne ha due.
Generarla dal binario sbagliato produce un documento che descrive un artefatto
che nessuno riceve. La fonte autorevole è il `.deps.json` del publish di
distribuzione.

**Verificare l'accesso senza `sudo` prima di qualunque altra cosa.** Se la prima
prova gira come root, ogni risultato successivo è inutile: root bypassa proprio
il permesso che questo file esiste per concedere.

---

## 3. Checklist

> **La checklist operativa completa vive in un documento interno**
> (`CRYPTIN_CLI_LINUX_PROMPT_SESSIONE.md`, §5, nell'archivio del progetto e non in
> questo repository), insieme a come si legge il PIN senza bruciare tentativi e a
> come si chiude la sessione. Quella che segue è la stessa lista in forma
> ridotta, per chi ha in mano solo questo file di packaging — **se le due
> divergono, vince quella.**

Gli esiti attesi sono **quelli misurati su Windows il 2026-08-05**. Servono così:
una divergenza è un difetto specifico di Linux, ed è esattamente ciò che questa
sessione deve trovare.

### Senza dongle collegato

| # | Comando | Atteso |
|---|---|---|
| 1 | `cryptin status` | exit **3**, `dongle: none` |
| 2 | `cryptin --help` | exit **0**, uso su stdout |
| 3 | `cryptin bruciapelo` | exit **2** |

### Con dongle, bloccato

| # | Comando | Atteso |
|---|---|---|
| 4 | `cryptin status` | exit **4**, `state: locked` — **e questo è il primo vero test di HidSharp su hidraw** |
| 5 | `cryptin encrypt f` | exit **4** |
| 6 | `cryptin decrypt inesistente` | exit **2** |

### Con dongle, sbloccato

| # | Comando | Atteso |
|---|---|---|
| 7 | `cryptin unlock < pin.txt` | exit **0** → `unlocked`, `license: valid` |
| 8 | round trip 100 KB | exit 0/0, **SHA-256 identico**, ~4 s |
| 9 | round trip 1 MB | exit 0/0, **SHA-256 identico**, ~38 s → confrontare il throughput con i ~27 KB/s di Windows |
| 10 | round trip di una **cartella annidata** | struttura e contenuti identici |
| 11 | `.crin` di un altro dongle | exit **7** |
| 12 | `.crin` con un byte del ciphertext alterato | exit **8** |
| 13 | file che non è un container | exit **9** |
| 14 | file oltre 5 MB | exit **10** |
| 15 | `Ctrl-C` a metà di un trasferimento da 1 MB | exit **130**, **nessun file prodotto** |

### `reset` — solo con il cavo UART

| # | Comando | Atteso |
|---|---|---|
| 16 | `cryptin reset` **senza** `--port` | ⚠️ **qui Linux deve comportarsi diversamente da Windows**: `ListBridgePorts()` deve identificare il CH343 da `/dev/serial/by-id/` e usarlo **senza chiedere**. Se risponde exit 2 come su Windows, il filtro per vendor non funziona |
| 17 | `cryptin reset --port /dev/ttyUSB0` da sbloccato | exit **0**, il dongle torna **`locked`** |
| 18 | `cryptin reset --port /dev/ttyS0` (porta che non raggiunge EN) | exit **11**, «*never left the bus*» |

Il numero 16 è il più importante dei tre, perché è l'unico punto in cui il
comportamento atteso su Linux **non** è quello osservato su Windows.

---

## 4. Da annotare comunque

- il **throughput** misurato, anche se identico: è il numero che decide se il
  tetto dei 5 MB va alzato, e non è detto che uno stack `hidraw` si comporti
  come uno stack Windows;
- se `/dev/hidraw*` compare con permessi diversi da quelli attesi, **il numero
  del nodo e il proprietario**, non solo «non funziona»;
- se HidSharp non trova il dongle, controllare `libudev.so.1` *prima* di
  sospettare il dispositivo: un backend che non carica e un dispositivo assente
  danno lo stesso sintomo.
