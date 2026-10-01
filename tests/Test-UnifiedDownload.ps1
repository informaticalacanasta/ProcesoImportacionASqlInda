# Pruebas locales del script unificado con transporte SSH simulado.
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '../deploy/Descargar-Inbox-Unificado.ps1'
$source = [IO.File]::ReadAllText($scriptPath)
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$replacements = @{
    'Invoke-Remoto' = @'
function Invoke-Remoto([string]$Command) {
    if ($Command.StartsWith('find ') -and $Command.Contains('/Pedidos/Salida')) {
        if (-not $Command.Contains("-iname '*.txt'")) { throw 'El listado de pedidos debe excluir archivos temporales.' }
        if ($global:TestNoOrders) { return }
        return ([string]([IO.FileInfo]$global:TestOrder).Length) + "`t" + 'pedido.txt'
    }
    if ($Command.StartsWith('find ') -and $Command.Contains('/Tickets/Salida')) {
        if ($global:TestIncompleteTicket) { return @('35/fact_sin_firmar.xml', '35/fact_sin_firmar.pdf') }
        return @('35/fact_sin_firmar.xml', '35/fact_sin_firmar.pdf', '35/fact_a4_sin_firmar.pdf')
    }
    if ($Command.StartsWith('sha256sum ')) {
        $name = [IO.Path]::GetFileName($Command.TrimEnd("'"))
        return (Get-Hash $global:TestTicketFiles[$name]) + '  ' + $name
    }
    if ($Command.StartsWith('rm -- ')) {
        if ($Command.Contains('/Tickets/Salida')) { throw 'Se intento borrar un ticket en Ubuntu' }
        $testCopy = Join-Path $global:TestInboxPrueba 'pedido.txt'
        $inboxCopy = Join-Path $global:TestInbox 'pedido.txt'
        if (-not (Test-Path -LiteralPath $testCopy -PathType Leaf) -or
            -not (Test-Path -LiteralPath $inboxCopy -PathType Leaf) -or
            (Get-Hash $testCopy) -ne (Get-Hash $global:TestOrder) -or
            (Get-Hash $inboxCopy) -ne (Get-Hash $global:TestOrder)) {
            throw 'Se intento borrar el pedido antes de verificar ambas copias'
        }
        $global:TestOrderDeleted = $true
        Remove-Item -LiteralPath $global:TestOrder
        return
    }
    throw "Comando inesperado: $Command"
}
'@
    'Receive-Archivo' = @'
function Receive-Archivo([string]$Remote, [string]$Local) {
    if ($Remote.Contains('/Tickets/Salida')) {
        $name = [IO.Path]::GetFileName($Remote)
        if ($global:TestCorruptTicket -and $name.EndsWith('.xml')) { [IO.File]::WriteAllText($Local, 'corrupto') }
        else { [IO.File]::Copy($global:TestTicketFiles[$name], $Local, $true) }
    } else { [IO.File]::Copy($global:TestOrder, $Local, $true) }
}
'@
}
$functions = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $replacements.ContainsKey($node.Name) }, $true)
foreach ($function in ($functions | Sort-Object { $_.Extent.StartOffset } -Descending)) {
    $source = $source.Remove($function.Extent.StartOffset, $function.Extent.EndOffset - $function.Extent.StartOffset).Insert($function.Extent.StartOffset, $replacements[$function.Name])
}
$download = [scriptblock]::Create($source)
$root = Join-Path ([IO.Path]::GetTempPath()) ('unified-download-test-' + [guid]::NewGuid().ToString('N'))
function Assert-True($Condition, $Message) { if (-not $Condition) { throw $Message } }
function New-Scenario([string]$Name) {
    $folder = Join-Path $root $Name
    $copy = Join-Path $folder 'prueba'; $inbox = Join-Path $folder 'inbox'
    [void][IO.Directory]::CreateDirectory($copy); [void][IO.Directory]::CreateDirectory($inbox)
    $global:TestOrder = Join-Path $folder 'remote-order.txt'
    $global:TestTicketFiles = @{
        'fact_sin_firmar.xml' = Join-Path $folder 'remote-ticket.xml'
        'fact_sin_firmar.pdf' = Join-Path $folder 'remote-ticket.pdf'
        'fact_a4_sin_firmar.pdf' = Join-Path $folder 'remote-ticket-a4.pdf'
    }
    $global:TestTicket = $global:TestTicketFiles['fact_sin_firmar.xml']
    $global:TestInboxPrueba = $copy
    $global:TestInbox = $inbox
    $global:TestOrderDeleted = $false
    $global:TestCorruptTicket = $false
    $global:TestIncompleteTicket = $false
    $global:TestNoOrders = $false
    [IO.File]::WriteAllText($global:TestOrder, 'pedido')
    [IO.File]::WriteAllText($global:TestTicket, '<TicketBai/>')
    [IO.File]::WriteAllText($global:TestTicketFiles['fact_sin_firmar.pdf'], 'pdf-normal')
    [IO.File]::WriteAllText($global:TestTicketFiles['fact_a4_sin_firmar.pdf'], 'pdf-a4')
    return @{ InboxPrueba = $copy; Inbox = $inbox; UsarClaveSsh = $true }
}
try {
    $args = New-Scenario 'ambos'
    & $download @args
    Assert-True $global:TestOrderDeleted 'El pedido no se retiro de Ubuntu'
    Assert-True (Test-Path (Join-Path $args.InboxPrueba 'pedido.txt')) 'Pedido no llego a InboxPrueba'
    Assert-True (Test-Path (Join-Path $args.Inbox 'pedido.txt')) 'Pedido no llego a inbox'
    foreach ($name in $global:TestTicketFiles.Keys) {
        Assert-True (Test-Path (Join-Path $args.InboxPrueba $name)) "Falta $name en InboxPrueba"
        Assert-True (-not (Test-Path (Join-Path $args.Inbox $name))) "Se publico $name en inbox"
    }
    Assert-True (Test-Path $global:TestTicket) 'Se borro el ticket en Ubuntu'

    $args = New-Scenario 'solo-ticket'
    $global:TestNoOrders = $true
    & $download @args
    & $download @args
    Assert-True (Test-Path $global:TestTicket) 'La repeticion borro el ticket'
    foreach ($name in $global:TestTicketFiles.Keys) {
        Assert-True (Test-Path (Join-Path $args.InboxPrueba $name)) "La repeticion no conservo $name"
        Assert-True (-not (Test-Path (Join-Path $args.Inbox $name))) "La repeticion publico $name en inbox"
    }

    $args = New-Scenario 'ticket-incompleto'
    $global:TestNoOrders = $true
    $global:TestIncompleteTicket = $true
    $failed = $false
    try { & $download @args } catch { $failed = $true }
    Assert-True $failed 'Conjunto sin PDF A4 no detectado'
    Assert-True (-not (Test-Path (Join-Path $args.InboxPrueba 'fact_sin_firmar.xml'))) 'Se copio un conjunto incompleto'

    $args = New-Scenario 'ticket-corrupto'
    $global:TestNoOrders = $true
    $global:TestCorruptTicket = $true
    $failed = $false
    try { & $download @args } catch { $failed = $true }
    Assert-True $failed 'Descarga corrupta no detectada'
    Assert-True (-not (Test-Path (Join-Path $args.InboxPrueba 'fact_sin_firmar.xml'))) 'Ticket corrupto publicado'
    Assert-True (Test-Path $global:TestTicket) 'Ticket corrupto borrado de Ubuntu'

    $args = New-Scenario 'ticket-existente'
    $global:TestNoOrders = $true
    [IO.File]::WriteAllText((Join-Path $args.InboxPrueba 'fact_sin_firmar.xml'), 'existente')
    & $download @args
    Assert-True ((Get-Content (Join-Path $args.InboxPrueba 'fact_sin_firmar.xml') -Raw) -eq 'existente') 'Ticket existente sobrescrito'
    Assert-True (Test-Path (Join-Path $args.InboxPrueba 'fact_sin_firmar.pdf')) 'PDF normal no copiado cuando el XML ya existia'
    Assert-True (Test-Path (Join-Path $args.InboxPrueba 'fact_a4_sin_firmar.pdf')) 'PDF A4 no copiado cuando el XML ya existia'
    Write-Host '5 escenarios correctos: pedidos y conjuntos XML + 2 PDF solo en InboxPrueba (SSH simulado).'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf).StartsWith('unified-download-test-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Variable TestOrder, TestTicket, TestTicketFiles, TestInboxPrueba, TestInbox, TestOrderDeleted, TestCorruptTicket, TestIncompleteTicket, TestNoOrders -Scope Global -ErrorAction SilentlyContinue
}
