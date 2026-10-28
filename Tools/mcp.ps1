# Minimal client for the editor's local MCP server (HTTP, JSON-RPC). Used by call.ps1; not run directly.
#   .\mcp.ps1 tools/list
#   .\mcp.ps1 tools/call '{"name":"get_current_scene","arguments":{}}'
# The server only exists while the Lucky Engine editor is open; it listens on 127.0.0.1:19331 and has no password.
param([string]$Method, [string]$ParamsJson = '{}')
$u = 'http://127.0.0.1:19331/mcp'
$h = @{ 'Content-Type' = 'application/json'; 'Accept' = 'application/json, text/event-stream' }
$r = Invoke-WebRequest -Uri $u -Method Post -Headers $h -UseBasicParsing -Body '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"tidy-tools","version":"1"}}}'
$h['Mcp-Session-Id'] = $r.Headers['Mcp-Session-Id']
Invoke-WebRequest -Uri $u -Method Post -Headers $h -UseBasicParsing -Body '{"jsonrpc":"2.0","method":"notifications/initialized"}' | Out-Null
$body = '{"jsonrpc":"2.0","id":2,"method":"' + $Method + '","params":' + $ParamsJson + '}'
(Invoke-WebRequest -Uri $u -Method Post -Headers $h -UseBasicParsing -Body $body).Content
