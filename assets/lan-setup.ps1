# TopBar Extras - the network setup that needs admin rights. The installer
# runs it for its Network options, and Settings > Network for its buttons;
# Windows asks for approval either way.
#
#   -Firewall   let the other PCs' broadcasts in: UDP 51736, from this network only
#   -Wake       let the other PCs wake this one: magic packets wake its wired
#               network card(s), from sleep and from shut down. (Waking from
#               shut down also needs Wake-on-LAN turned on in the BIOS / UEFI.)
#   -Remove     take the firewall rule out again
param([switch]$Firewall, [switch]$Wake, [switch]$Remove)

$rule = 'TopBar Extras LAN'

if ($Firewall -or $Remove) {
    netsh advfirewall firewall delete rule name="$rule" | Out-Null
}
if ($Firewall) {
    netsh advfirewall firewall add rule name="$rule" dir=in action=allow protocol=UDP `
        localport=51736 remoteip=localsubnet profile=any | Out-Null
}

if ($Wake) {
    $cards = Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.MediaType -eq '802.3' }
    foreach ($card in $cards) {
        # The card's own switches. Names differ by vendor, so only the ones a
        # card actually has are touched: magic-packet wake, power-management
        # events, and (Realtek) waking from shut down.
        $changed = $false
        foreach ($keyword in '*WakeOnMagicPacket', 'EnablePME', 'S5WakeOnLan') {
            $p = Get-NetAdapterAdvancedProperty -Name $card.Name -RegistryKeyword $keyword -ErrorAction SilentlyContinue
            if ($p -and "$($p.RegistryValue)" -ne '1') {
                Set-NetAdapterAdvancedProperty -Name $card.Name -RegistryKeyword $keyword -RegistryValue 1 `
                    -NoRestart -ErrorAction SilentlyContinue
                $changed = $true
            }
        }
        # Windows' side: a magic packet may wake the PC ("Allow this device to
        # wake the computer" in Device Manager).
        Enable-NetAdapterPowerManagement -Name $card.Name -WakeOnMagicPacket -NoRestart -ErrorAction SilentlyContinue
        powercfg /deviceenablewake "$($card.InterfaceDescription)" | Out-Null
        # The card only reads its switches when it starts, so restart it (a
        # second or two offline) if any of them changed.
        if ($changed) { Restart-NetAdapter -Name $card.Name -ErrorAction SilentlyContinue }
    }
}
