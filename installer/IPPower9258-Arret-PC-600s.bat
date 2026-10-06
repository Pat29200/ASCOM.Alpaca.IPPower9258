@echo off
setlocal
rem GitHub edition based on revision 4: explicit Basic authentication and bounded streaming HTTP reads.
rem NINA: execute this file without arguments to schedule outlet 2 OFF in 600s.
rem /check: read configuration and outlet status only. No power change.
rem /test: outlet 4 ON, then OFF after 30s. Outlet 4 must be unplugged.
set "IP9258_SCRIPT=%~f0"
set "IP9258_MODE=normal"
if /I "%~1"=="/check" set "IP9258_MODE=check"
if /I "%~1"=="/test" set "IP9258_MODE=test"
if not "%~1"=="" if /I not "%~1"=="/check" if /I not "%~1"=="/test" exit /b 2
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$raw=[IO.File]::ReadAllText($env:IP9258_SCRIPT); $code=($raw -split '(?m)^# POWERSHELL_PAYLOAD\r?$',2)[1]; & ([scriptblock]::Create($code))"
exit /b %errorlevel%
# POWERSHELL_PAYLOAD
$ErrorActionPreference = 'Stop'
$client = $null
$handler = $null
try {
    $settingsPath = Join-Path $env:ProgramData 'ASCOM-IPPower9258\ippower9258.settings.json'
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $deviceHost = [string]$settings.Host
    $username = [string]$settings.Username
    if ([string]::IsNullOrWhiteSpace($deviceHost) -or [string]::IsNullOrWhiteSpace($username)) {
        throw 'Adresse ou utilisateur absent de la configuration du driver.'
    }
    $deviceHost = $deviceHost.Trim()
    if ([Uri]::CheckHostName($deviceHost) -eq [UriHostNameType]::Unknown -or $deviceHost.Contains(':')) {
        throw 'Adresse IP IPv4 ou nom DNS attendu dans la configuration du driver.'
    }
    Add-Type -AssemblyName System.Security
    Add-Type -AssemblyName System.Net.Http
    if (-not [string]::IsNullOrWhiteSpace([string]$settings.PasswordProtected)) {
        $protectedBytes = [Convert]::FromBase64String([string]$settings.PasswordProtected)
        $clearBytes = [Security.Cryptography.ProtectedData]::Unprotect($protectedBytes, $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)
        $password = [Text.Encoding]::UTF8.GetString($clearBytes)
        [Array]::Clear($clearBytes, 0, $clearBytes.Length)
    } else {
        $password = [string]$settings.Password
    }
    if ([string]::IsNullOrEmpty($password)) { throw 'Mot de passe absent de la configuration.' }
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(15)
    $authBytes = [Text.Encoding]::UTF8.GetBytes($username + ':' + $password)
    $authToken = [Convert]::ToBase64String($authBytes)
    [Array]::Clear($authBytes, 0, $authBytes.Length)
    $client.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Basic', $authToken)
    $client.DefaultRequestHeaders.ConnectionClose = $true
    $authToken = $null
    $password = $null
    function Send-DeviceCommand([string]$command) {
        $response = $null
        $stream = $null
        $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, ('http://' + $deviceHost + '/set.cmd?cmd=' + $command))
        $request.Version = [Version]'1.1'
        $clock = [Diagnostics.Stopwatch]::StartNew()
        try {
            $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            if (-not $response.IsSuccessStatusCode) { throw ('Erreur HTTP ' + [int]$response.StatusCode) }
            $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            # Read one byte to avoid waiting for a larger read buffer to fill.
            $buffer = New-Object byte[] 1
            $body = ''
            while ($true) {
                $remaining = 15000 - [int]$clock.ElapsedMilliseconds
                if ($remaining -le 0) { throw 'Delai HTTP depasse avant reception d''une reponse complete reconnue.' }
                $readTask = $stream.ReadAsync($buffer, 0, $buffer.Length)
                if (-not $readTask.Wait($remaining)) { throw 'Delai HTTP depasse avant reception d''une reponse complete reconnue.' }
                $count = $readTask.GetAwaiter().GetResult()
                if ($count -eq 0) { throw 'Reponse IP9258 terminee sans etat attendu.' }
                $body += [Text.Encoding]::ASCII.GetString($buffer, 0, $count)
                if ($body.Length -gt 65536) { throw 'Reponse IP9258 trop volumineuse.' }
                if ($body -match '(?i)HTTPCMD_|unauthorized|error|invalid|<form|<input|<script') {
                    throw 'Reponse IP9258 inattendue ou signalant une erreur.'
                }
                # Do not wait for TCP close: firmware can leave the response open.
                # Require a delimiter after the value, so fragmented replies are not accepted early.
                if ($command -eq 'getpower') {
                    $match = [regex]::Match($body, '(?i)p61\s*=\s*[01]\s*,\s*p62\s*=\s*[01]\s*,\s*p63\s*=\s*[01]\s*,\s*p64\s*=\s*[01](?=[<\s,])')
                } else {
                    $target = [regex]::Match($command, '^setpower\+p6([1-4])=1\+').Groups[1].Value
                    if ([string]::IsNullOrEmpty($target)) { throw 'Commande non reconnue.' }
                    $match = [regex]::Match($body, ('(?i)p6' + $target + '\s*=\s*1(?=[<\s,])'))
                }
                if ($match.Success) {
                    if ($env:IP9258_MODE -eq 'check') {
                        Write-Host ('Reponse HTTP {0} : {1}' -f [int]$response.StatusCode, $match.Value.Trim())
                    }
                    return $match.Value.Trim()
                }
            }
        } finally {
            # Closing our stream releases the connection even if the device leaves it open.
            if ($null -ne $stream) { $stream.Dispose() }
            if ($null -ne $response) { $response.Dispose() }
            $request.Dispose()
            $clock.Stop()
        }
    }
    $status = Send-DeviceCommand 'getpower'
    if ($status -notmatch '(?i)p61\s*=\s*[01]' -and $status -notmatch '(?i)Power\s+Status\s*:\s*[01]') {
        throw 'Etat des prises non reconnu. Aucune coupure programmee.'
    }
    if ($env:IP9258_MODE -eq 'check') {
        Write-Output ('Verification OK : configuration lisible et IP9258 accessible. ' + $status.Trim())
        Write-Output 'Aucune prise modifiee. Aucun arret Windows demande.'
    } else {
        $outlet = 2
        $delay = 600
        if ($env:IP9258_MODE -eq 'test') { $outlet = 4; $delay = 30 }
        $command = 'setpower+p6{0}=1+p6{0}n=0+t6{0}={1}' -f $outlet, $delay
        $answer = Send-DeviceCommand $command
        Write-Output ('Commande transmise : prise {0} ON puis OFF dans {1} secondes.' -f $outlet, $delay)
        Write-Output ('Reponse IP9258 : ' + $answer.Trim())
        Write-Output 'Le compte a rebours est gere par IP9258. Ce script ne lance pas Shutdown Windows.'
    }
    $exitCode = 0
} catch {
    Write-Output ('ECHEC : ' + $_.Exception.Message)
    Write-Output 'Ne pas considerer la coupure differee comme confirmee.'
    $exitCode = 1
} finally {
    if ($null -ne $client) { $client.Dispose() }
    if ($null -ne $handler) { $handler.Dispose() }
}
exit $exitCode
