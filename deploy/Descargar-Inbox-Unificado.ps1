# Windows PowerShell 5.1 / PowerShell 7.
# Pedidos: inboxOrganizado -> InboxPrueba -> inbox -> borrar salida de Ubuntu.
# Tickets: paraDescargar -> InboxPrueba. Fin; conserva la salida de Ubuntu.
[CmdletBinding()]
param(
    [string]$SshUsuario = 'trujillo',
    [string]$SshHost = '85.234.145.90',
    [string]$PedidosRemotos = '/home/tpv_recepcion/inboxOrganizado',
    [string]$TicketsRemotos = '/home/tpv_recepcion/tickets/paraDescargar',
    [string]$InboxPrueba = '\\pedidos\C\TPVISION\InboxPrueba',
    [string]$Inbox = '\\pedidos\C\TPVISION\inbox',
    [string]$CredentialFile = '',
    [switch]$UsarClaveSsh,
    [switch]$MostrarConsola
)
$ErrorActionPreference = 'Stop'
$logDirectory = if ($PSScriptRoot) { $PSScriptRoot } else { $env:TEMP }
$logPath = Join-Path $logDirectory 'Descargar-Inbox-Unificado.log'
$transcriptStarted = $false
try {
    Start-Transcript -LiteralPath $logPath -Append -ErrorAction Stop | Out-Null
    $transcriptStarted = $true
    Write-Host "Inicio de descarga unificada: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
} catch {
    Write-Warning "No se pudo abrir el registro $logPath : $($_.Exception.Message)"
}

function Test-RutaRemota([string]$Path) {
    if ($Path -notmatch '^/[A-Za-z0-9_/-]+$' -or $Path -match '/\.\.') {
        throw "Ruta remota no admitida: $Path"
    }
}
function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Invoke-Remoto([string]$Command) {
    $output = & ssh.exe @sshArgs $destinoSsh $Command
    if ($LASTEXITCODE -ne 0) { throw "SSH fallo con codigo $LASTEXITCODE" }
    return $output
}
function Receive-Archivo([string]$Remote, [string]$Local) {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = (Get-Command ssh.exe).Source
    $psi.Arguments = (($sshArgs + @($destinoSsh, "cat -- '$Remote'")) | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.CreateNoWindow = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $psi
    $file = [IO.File]::Create($Local)
    try {
        [void]$process.Start()
        $process.StandardOutput.BaseStream.CopyTo($file)
        $file.Flush($true)
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw 'Descarga incompleta.' }
    } finally { $file.Dispose(); $process.Dispose() }
}
function Copy-Checked([string]$Source, [string]$Destination, [long]$Size) {
    # El proceso de pedidos conserva el mismo criterio de verificacion por tamaño.
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    if (-not (Test-Path -LiteralPath $Destination -PathType Leaf) -or
        (Get-Item -LiteralPath $Destination).Length -ne $Size) {
        throw "Copia incompleta: $Destination"
    }
}
function Invoke-Pedidos {
    $rows = @(Invoke-Remoto "find '$PedidosRemotos' -mindepth 1 -maxdepth 1 -type f -printf '%s\t%f\n'")
    $items = @()
    foreach ($row in $rows) {
        if ([string]::IsNullOrWhiteSpace($row)) { continue }
        $parts = $row.Trim() -split "`t", 2
        if ($parts.Count -ne 2 -or $parts[0] -notmatch '^\d+$' -or $parts[1] -notmatch '^[A-Za-z0-9._-]+$') {
            Write-Warning "Pedido omitido por listado no reconocido: $row"
            $script:errors++
            continue
        }
        if ($parts[1] -match '\.[xX][mM][lL]$') {
            Write-Warning "XML omitido de inboxOrganizado para no enviarlo a inbox: $($parts[1])"
            continue
        }
        $items += [pscustomobject]@{ Name = $parts[1]; Size = [long]$parts[0]; InTest = $false; InInbox = $false }
    }
    Write-Host "Pedidos encontrados: $($items.Count)"
    # Mantiene las tres fases del script original para los pedidos.
    foreach ($item in $items) {
        try {
            $remote = "$PedidosRemotos/$($item.Name)"
            $local = Join-Path $working $item.Name
            Receive-Archivo $remote $local
            if ((Get-Item -LiteralPath $local).Length -ne $item.Size) { throw 'Tamaño descargado distinto de Ubuntu.' }
            Copy-Checked $local (Join-Path $InboxPrueba $item.Name) $item.Size
            $item.InTest = $true
            Write-Host "Pedido copiado a InboxPrueba: $($item.Name)"
        } catch { $script:errors++; Write-Warning "Pedido no copiado a InboxPrueba: $($item.Name). $($_.Exception.Message)" }
    }
    foreach ($item in @($items | Where-Object InTest)) {
        try {
            Copy-Checked (Join-Path $working $item.Name) (Join-Path $Inbox $item.Name) $item.Size
            $item.InInbox = $true
            Write-Host "Pedido copiado a inbox: $($item.Name)"
        } catch { $script:errors++; Write-Warning "Pedido no copiado a inbox: $($item.Name). $($_.Exception.Message)" }
    }
    foreach ($item in @($items | Where-Object InInbox)) {
        try {
            Invoke-Remoto "rm -- '$PedidosRemotos/$($item.Name)'" | Out-Null
            Write-Host "Pedido retirado de Ubuntu: $($item.Name)"
        } catch { $script:errors++; Write-Warning "Pedido conservado en Ubuntu: $($item.Name). $($_.Exception.Message)" }
    }
}
function Invoke-Tickets {
    $rows = @(Invoke-Remoto "find '$TicketsRemotos' -mindepth 2 -maxdepth 2 -type f -iname '*.xml' -printf '%P\n'")
    $rows = @($rows | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    Write-Host "Tickets encontrados: $($rows.Count)"
    foreach ($relative in $rows) {
        try {
            if ($relative -notmatch '^(?<id>[1-9][0-9]*)/(?<name>[A-Za-z0-9_.-]+\.[xX][mM][lL])$') {
                throw "Nombre de ticket no admitido: $relative"
            }
            $id = $Matches.id
            $name = $Matches.name
            $remote = "$TicketsRemotos/$relative"
            $hashLine = [string](Invoke-Remoto "sha256sum -- '$remote'")
            if ($hashLine -notmatch '^(?<hash>[a-fA-F0-9]{64})\s') { throw 'Hash remoto invalido.' }
            $hash = $Matches.hash.ToLowerInvariant()
            $destination = Join-Path $InboxPrueba $name
            if (Test-Path -LiteralPath $destination) {
                if ((Get-Hash $destination) -ne $hash) { throw "Colision en InboxPrueba: $name" }
            } else {
                $partial = Join-Path $InboxPrueba ('.ticket-' + $id + '.partial')
                Receive-Archivo $remote $partial
                if ((Get-Hash $partial) -ne $hash) { throw 'Descarga de ticket no verificada.' }
                [IO.File]::Move($partial, $destination)
            }
            Write-Host "Ticket verificado en InboxPrueba: $name. Ubuntu conservado."
        } catch { $script:errors++; Write-Warning "Ticket pendiente: $relative. $($_.Exception.Message)" }
    }
}

Test-RutaRemota $PedidosRemotos
Test-RutaRemota $TicketsRemotos
if ($SshUsuario -notmatch '^[A-Za-z0-9_-]+$' -or $SshHost -notmatch '^[A-Za-z0-9.-]+$') { throw 'Destino SSH no admitido.' }
if ([IO.Path]::GetFullPath($InboxPrueba).TrimEnd('\') -eq [IO.Path]::GetFullPath($Inbox).TrimEnd('\')) {
    throw 'InboxPrueba e inbox deben ser carpetas distintas.'
}
if (-not (Test-Path -LiteralPath $InboxPrueba -PathType Container)) { throw "No hay acceso a InboxPrueba: $InboxPrueba" }
if (-not (Test-Path -LiteralPath $Inbox -PathType Container)) { throw "No hay acceso a inbox de pedidos: $Inbox" }
[void](Get-Command ssh.exe)
$destinoSsh = "${SshUsuario}@${SshHost}"
$sshArgs = @('-o', 'ConnectTimeout=15', '-o', 'StrictHostKeyChecking=accept-new')
$oldAskpass = $env:SSH_ASKPASS
$oldRequire = $env:SSH_ASKPASS_REQUIRE
$oldDisplay = $env:DISPLAY
$oldPassword = $env:TICKETS_SSH_PASSWORD
$askpass = $null
$runLock = $null
$script:errors = 0
$working = Join-Path $env:TEMP ('inbox-unificado-' + [guid]::NewGuid().ToString('N'))
try {
    # Impide que dos ejecuciones programadas trabajen sobre los mismos archivos.
    $runLock = [IO.File]::Open((Join-Path $env:TEMP 'inbox-unificado.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    if (-not $UsarClaveSsh) {
        if ([string]::IsNullOrWhiteSpace($CredentialFile)) {
            $CredentialFile = Join-Path $PSScriptRoot 'inbox-unificado-ssh.secret'
        }
        if (-not $env:TICKETS_SSH_PASSWORD) {
            if (Test-Path -LiteralPath $CredentialFile -PathType Leaf) {
                $encrypted = (Get-Content -LiteralPath $CredentialFile -Raw).Trim()
                if ($encrypted.Length -eq 0) { throw 'El archivo de contraseña SSH está vacío.' }
                $secret = ConvertTo-SecureString -String $encrypted
            } else {
                $secret = Read-Host 'Contrasena SSH de Ubuntu' -AsSecureString
            }
            $credential = New-Object Management.Automation.PSCredential($SshUsuario, $secret)
            $env:TICKETS_SSH_PASSWORD = $credential.GetNetworkCredential().Password
        }
        $askpass = Join-Path $env:TEMP ('inbox-askpass-' + [guid]::NewGuid().ToString('N') + '.cmd')
        [IO.File]::WriteAllText($askpass, '@powershell.exe -NoProfile -NonInteractive -Command "[Console]::WriteLine($env:TICKETS_SSH_PASSWORD)"', [Text.Encoding]::ASCII)
        $env:SSH_ASKPASS = $askpass
        $env:SSH_ASKPASS_REQUIRE = 'force'
        $env:DISPLAY = 'localhost:0.0'
        $sshArgs += @('-o', 'PreferredAuthentications=password', '-o', 'PubkeyAuthentication=no', '-o', 'NumberOfPasswordPrompts=1')
    }
    [void][IO.Directory]::CreateDirectory($working)
    try { Invoke-Pedidos } catch { $script:errors++; Write-Warning "No se pudo listar o completar pedidos: $($_.Exception.Message)" }
    try { Invoke-Tickets } catch { $script:errors++; Write-Warning "No se pudo listar o completar tickets: $($_.Exception.Message)" }
} finally {
    if ($runLock) { $runLock.Dispose() }
    $env:SSH_ASKPASS = $oldAskpass
    $env:SSH_ASKPASS_REQUIRE = $oldRequire
    $env:DISPLAY = $oldDisplay
    $env:TICKETS_SSH_PASSWORD = $oldPassword
    if ($askpass) { Remove-Item -LiteralPath $askpass -Force -ErrorAction SilentlyContinue }
    $fullWorking = [IO.Path]::GetFullPath($working)
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    if ($fullWorking.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path $fullWorking -Leaf).StartsWith('inbox-unificado-')) {
        Remove-Item -LiteralPath $fullWorking -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Fin de descarga unificada: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'). Incidencias: $($script:errors)"
    if ($transcriptStarted) { try { Stop-Transcript | Out-Null } catch { } }
    if ($MostrarConsola) {
        Write-Host 'La ventana se cerrara en 45 segundos.'
        Start-Sleep -Seconds 45
    }
}
if ($script:errors) { throw "Descarga terminada con $($script:errors) incidencia(s)." }
