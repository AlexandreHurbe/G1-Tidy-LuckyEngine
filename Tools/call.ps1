# Call one editor tool and print its text result.
#   .\call.ps1 get_current_scene
#   .\call.ps1 set_entity_property '{"entityID":"123","componentName":"Script","propertyName":"MaxEpisodes","value":"2"}'
param([string]$Name, [string]$ArgsJson = '{}', [string]$Out)
$here = Split-Path $MyInvocation.MyCommand.Path
$o = & "$here\mcp.ps1" tools/call ('{"name":"' + $Name + '","arguments":' + $ArgsJson + '}') | ConvertFrom-Json
$t = ($o.result.content | ForEach-Object { $_.text }) -join "`n"
try {
    $p = $t | ConvertFrom-Json
    if ($p.result) { $t = $p.result } elseif ($p.error) { $t = 'ERROR: ' + $p.error }
} catch { }
if ($Out) { Set-Content -Encoding utf8 -Path $Out -Value $t } else { $t }
