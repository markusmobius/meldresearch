#requires -Version 7.0
[CmdletBinding()]
param(
    [uri]$TokenSource = 'https://projectmeld.microsoft.com/api/inference/token',
    [string]$OutputPath = [IO.Path]::Combine($PSScriptRoot, '..', '.local-test', 'Foundry.Local.json')
)
$ErrorActionPreference = 'Stop'
if ($TokenSource.Scheme -ne 'https' -or $TokenSource.UserInfo -or $TokenSource.Query -or $TokenSource.Fragment) {
    throw 'Use an HTTPS development credential-service URL without credentials or a query string.'
}
try { $reply = Invoke-RestMethod -Uri $TokenSource.AbsoluteUri -TimeoutSec 30 }
catch { throw 'The development credential service is unavailable. Its response and any credentials were not printed.' }
if ($reply.provider -ne 'Foundry' -or [string]::IsNullOrWhiteSpace($reply.token) -or
    [DateTimeOffset]$reply.expiresOn -le [DateTimeOffset]::UtcNow.AddMinutes(2)) {
    throw 'The service did not supply current Foundry credentials. No provider fallback is allowed.'
}
$chat = [uri]$reply.endpoint
$embedding = [uri]$reply.embeddingEndpoint
$base = [uri]::new($chat.GetLeftPart([UriPartial]::Authority) + '/openai/v1/')
if ($chat.Scheme -ne 'https' -or $chat.UserInfo -or $chat.Query -or $chat.Fragment -or
    $chat.AbsolutePath -ne '/openai/v1/chat/completions' -or
    $embedding.AbsoluteUri -ne ([uri]::new($base, 'embeddings')).AbsoluteUri) {
    throw 'The service does not expose matching Azure OpenAI v1 Foundry routes.'
}
$settings = @{ Foundry = @{
    Endpoint = $base.AbsoluteUri
    ChatDeployment = $reply.chatDeployment
    EmbeddingDeployment = $reply.embeddingDeployment
    DevelopmentTokenSource = $TokenSource.AbsoluteUri
} }
$destination = [IO.Path]::GetFullPath($OutputPath)
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
[IO.File]::WriteAllText($destination, ($settings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
$reply = $null
Write-Host "Configured development Foundry routes in $destination"
Write-Host 'No token, API key, or Google secret was saved or printed. This credential-source option is rejected in Production.'
