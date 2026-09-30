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
    if ($Command.StartsWith('find ') -and $Command.Contains('inboxOrganizado')) {
        if ($global:TestNoOrders) { return }
        return ([string]([IO.FileInfo]$global:TestOrder).Length) + "`t" + 'pedido.txt'
    }
    if ($Command.StartsWith('find ') -and $Command.Contains('paraDescargar')) { return '35/ticket.xml' }
    if ($Command.StartsWith('sha256sum ')) { return (Get-Hash $global:TestTicket) + '  ticket.xml' }
    if ($Command.StartsWith('rm -- ')) {
        if ($Command.Contains('paraDescargar')) { throw 'Se intento borrar un ticket en Ubuntu' }
        $global:TestOrderDeleted = $true
        Remove-Item -LiteralPath $global:TestOrder
        return
    }
    throw "Comando inesperado: $Command"
}
'@
    'Receive-Archivo' = @'
function Receive-Archivo([string]$Remote, [string]$Local) {
    if ($Remote.Contains('paraDescargar')) {
        if ($global:TestCorruptTicket) { [IO.File]::WriteAllText($Local, 'corrupto') }
        else { [IO.File]::Copy($global:TestTicket, $Local, $true) }
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
    $global:TestTicket = Join-Path $folder 'remote-ticket.xml'
    $global:TestOrderDeleted = $false
    $global:TestCorruptTicket = $false
    $global:TestNoOrders = $false
    [IO.File]::WriteAllText($global:TestOrder, 'pedido')
    [IO.File]::WriteAllText($global:TestTicket, '<TicketBai/>')
    return @{ InboxPrueba = $copy; Inbox = $inbox; UsarClaveSsh = $true }
}
try {
    $args = New-Scenario 'ambos'
    & $download @args
    Assert-True $global:TestOrderDeleted 'El pedido no se retiro de Ubuntu'
    Assert-True (Test-Path (Join-Path $args.InboxPrueba 'pedido.txt')) 'Pedido no llego a InboxPrueba'
    Assert-True (Test-Path (Join-Path $args.Inbox 'pedido.txt')) 'Pedido no llego a inbox'
    Assert-True (Test-Path (Join-Path $args.InboxPrueba 'ticket.xml')) 'Ticket no llego a InboxPrueba'
    Assert-True (Test-Path $global:TestTicket) 'Se borro el ticket en Ubuntu'
    Assert-True (-not (Test-Path (Join-Path $args.Inbox 'ticket.xml'))) 'Se publico un ticket en inbox'

    $args = New-Scenario 'solo-ticket'
    $global:TestNoOrders = $true
    & $download @args
    & $download @args
    Assert-True (Test-Path $global:TestTicket) 'La repeticion borro el ticket'
    Assert-True (-not (Test-Path (Join-Path $args.Inbox 'ticket.xml'))) 'La repeticion publico el ticket en inbox'

    $args = New-Scenario 'ticket-corrupto'
    $global:TestNoOrders = $true
    $global:TestCorruptTicket = $true
    $failed = $false
    try { & $download @args } catch { $failed = $true }
    Assert-True $failed 'Descarga corrupta no detectada'
    Assert-True (-not (Test-Path (Join-Path $args.InboxPrueba 'ticket.xml'))) 'Ticket corrupto publicado'
    Assert-True (Test-Path $global:TestTicket) 'Ticket corrupto borrado de Ubuntu'

    $args = New-Scenario 'ticket-colision'
    $global:TestNoOrders = $true
    [IO.File]::WriteAllText((Join-Path $args.InboxPrueba 'ticket.xml'), 'existente')
    $failed = $false
    try { & $download @args } catch { $failed = $true }
    Assert-True $failed 'Colision no detectada'
    Assert-True ((Get-Content (Join-Path $args.InboxPrueba 'ticket.xml') -Raw) -eq 'existente') 'Colision sobrescrita'
    Write-Host '4 escenarios correctos: pedidos originales y tickets solo InboxPrueba (SSH simulado).'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf).StartsWith('unified-download-test-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Variable TestOrder, TestTicket, TestOrderDeleted, TestCorruptTicket, TestNoOrders -Scope Global -ErrorAction SilentlyContinue
}
