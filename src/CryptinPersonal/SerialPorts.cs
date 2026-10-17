using Microsoft.Win32;

namespace CryptinPersonal;

/// <summary>
/// The COM ports on this machine, and which of them is the dongle's UART bridge.
///
/// Read from the registry rather than through System.IO.Ports, which is a
/// separate NuGet package: this needs one number and one name, and taking a
/// dependency that has to restore for that is not worth it.
///
/// Two lists come out of here and they are not interchangeable.
///
/// <see cref="Programmable"/> is the one the wizards put on screen: the CH343
/// bridges and nothing else. The reason is not tidiness. On 2026-09-05 the
/// mechanism by which the wrong port gets flashed was pinned down: LoadPorts
/// reads the chosen port back from <c>SelectedItem</c>, where it can have
/// arrived through the box's own <c>SelectedIndex = 0</c> fallback; from that
/// moment the fallback is indistinguishable from a deliberate choice and is
/// preserved across every later reload. At this bench it fell on the
/// motherboard's COM1 and was harmless. On a desk with two boards attached it
/// falls on the other board — and the wizard's next button flashes, which
/// destroys a seed *and* a K_attestation, and the 24 words do not bring the
/// second one back. Filtering kills that at the root: if nothing wrong is in
/// the list, no fallback can land on anything wrong.
///
/// <see cref="List"/> keeps every port, and earns its place by telling apart
/// the two empty states — a machine with no COM port at all, and a machine with
/// four of them where none is ours. They send the user to different places, and
/// the second one is also what a failed CH343 lookup looks like.
///
/// Behind both there is a physical constraint that confuses everyone the first
/// time. Flashing needs a **serial port that does not exist during normal
/// use**: with only the native OTG cable the dongle is pure HID and Windows
/// shows no COM port at all, which is correct and is what an end user should
/// see. The flash and the `recover` command go through the *other* socket, the
/// UART/CH343 one, and the wizard has to say so rather than presenting an empty
/// list.
/// </summary>
internal sealed record SerialPortInfo(string PortName, string Description, bool IsCh343)
{
    public override string ToString() => $"{PortName} — {Description}";
}

internal static class SerialPorts
{
    /// <summary>WCH, the maker of the CH340/CH343 bridges on these boards.</summary>
    private const string Ch343Vendor = "VID_1A86";

    /// <summary>
    /// The sentence that goes next to every port list. Says what is missing from
    /// the list on purpose, because a user who owns a working dongle and sees
    /// one entry — or none — is otherwise entitled to think the app is broken.
    /// </summary>
    public const string OnlyUartNote =
        "Only the dongle's own UART bridge is listed. On the native socket the dongle is HID " +
        "only and shows no COM port — that is not a fault, and it is why the second cable exists.";

    /// <summary>
    /// Every COM port the machine has, CH343 or not. For diagnostics and for
    /// telling the two empty states apart — not for putting in front of a user
    /// who is about to press Program.
    /// </summary>
    public static IReadOnlyList<SerialPortInfo> List()
    {
        var ch343 = Ch343PortNames();
        var found = new List<SerialPortInfo>();

        using var map = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        if (map is not null)
        {
            foreach (var entry in map.GetValueNames())
            {
                if (map.GetValue(entry) is not string port || port.Length == 0) continue;
                var isCh343 = ch343.Contains(port);
                found.Add(new SerialPortInfo(port, isCh343 ? "CH343, the dongle's UART socket" : entry, isCh343));
            }
        }

        // The dongle's own first, and then by name: List() is still allowed to
        // be shown somewhere one day, and an ordering that puts the answer first
        // costs nothing here.
        return found.OrderByDescending(p => p.IsCh343).ThenBy(p => p.PortName, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The ports that can actually carry a flash: the CH343 bridges. This is
    /// what belongs in a combo box, and what a port must be found in again
    /// immediately before anything is written.
    /// </summary>
    public static IReadOnlyList<SerialPortInfo> Programmable()
        => List().Where(p => p.IsCh343).ToList();

    /// <summary>
    /// What to put in the box when <see cref="Programmable"/> comes back empty.
    /// Two different sentences because they are two different problems: no cable
    /// on the UART socket, or a machine whose other ports are not the dongle's.
    /// Naming the ports that were seen also makes the one failure this cannot
    /// distinguish — a registry lookup that came back empty on a locked-down
    /// machine — visible instead of silent.
    /// </summary>
    public static string NoPortText()
    {
        var others = List().Select(p => p.PortName).ToList();
        if (others.Count == 0)
            return "no COM port at all — connect the cable to the socket marked UART";

        return others.Count == 1
            ? $"no dongle port — {others[0]} is not the UART bridge"
            : $"no dongle port — {string.Join(", ", others)} are not the UART bridge";
    }

    /// <summary>
    /// Walks USB enumeration looking for WCH bridges and reads the COM name each
    /// one was assigned. Best effort: a locked-down machine can refuse these
    /// keys, and then nothing is marked. That used to mean a worse list; now it
    /// means an empty one, so <see cref="NoPortText"/> names the ports that were
    /// there — which is the only clue this case ever gives.
    /// </summary>
    private static HashSet<string> Ch343PortNames()
    {
        var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb is null) return ports;

            foreach (var device in usb.GetSubKeyNames())
            {
                if (!device.StartsWith(Ch343Vendor, StringComparison.OrdinalIgnoreCase)) continue;

                using var deviceKey = usb.OpenSubKey(device);
                foreach (var instance in deviceKey?.GetSubKeyNames() ?? [])
                {
                    using var parameters = deviceKey!.OpenSubKey($@"{instance}\Device Parameters");
                    if (parameters?.GetValue("PortName") is string port && port.Length > 0)
                        ports.Add(port);
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Nothing marked, so nothing programmable: NoPortText() explains it.
        }

        return ports;
    }
}
