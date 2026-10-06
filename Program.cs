// Version 0.14
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading;

var builder = WebApplication.CreateBuilder(args);

// Alpaca JSON member names are case-sensitive. Preserve the C# property names
// exactly (Value, ClientTransactionID, ServerTransactionID, ErrorNumber, ...).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
});

// ============================================================================
// CONFIGURATION v0.14
// ============================================================================
//
// La configuration persistante est enregistrée dans :
//   %ProgramData%\ASCOM-IPPower9258\ippower9258.settings.json
//
// v0.12 migre automatiquement l'ancien fichier situé à côté de l'exécutable.
// La migration est prudente : le nouveau fichier est d'abord écrit dans
// ProgramData puis relu. L'ancien fichier n'est supprimé qu'après validation.
//
// Le mot de passe persistant reste protégé par Windows DPAPI avec
// DataProtectionScope.LocalMachine. La page /setup ne l'affiche jamais.
//
// Les valeurs appsettings.json / variables d'environnement restent acceptées
// comme valeurs initiales pour faciliter la migration depuis la v0.9.
// ============================================================================

string settingsDirectoryPath =
    Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),
        "ASCOM-IPPower9258");

string settingsFilePath =
    Path.Combine(
        settingsDirectoryPath,
        "ippower9258.settings.json");

string legacySettingsFilePath =
    Path.Combine(
        AppContext.BaseDirectory,
        "ippower9258.settings.json");

void EnsureSettingsDirectory()
{
    Directory.CreateDirectory(settingsDirectoryPath);
}

void MigrateLegacySettingsFile()
{
    if (File.Exists(settingsFilePath) ||
        !File.Exists(legacySettingsFilePath))
    {
        return;
    }

    EnsureSettingsDirectory();

    string temporaryPath = settingsFilePath + ".migration.tmp";

    try
    {
        File.Copy(
            legacySettingsFilePath,
            temporaryPath,
            true);

        // Vérifie que la copie est lisible et contient bien un objet JSON
        // avant de remplacer/créer le fichier définitif.
        string migratedJson =
            File.ReadAllText(
                temporaryPath,
                Encoding.UTF8);

        using (JsonDocument document =
            JsonDocument.Parse(migratedJson))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "Le fichier de configuration à migrer n'est pas un objet JSON.");
            }
        }

        File.Move(
            temporaryPath,
            settingsFilePath,
            true);

        // Le fichier ProgramData existe et a été validé : l'ancienne copie
        // n'est plus nécessaire.
        File.Delete(legacySettingsFilePath);

        Console.WriteLine(
            $"Configuration migrée vers {settingsFilePath}");
    }
    finally
    {
        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);
    }
}

string? ipPowerHost = builder.Configuration["IPPower9258:Host"];
string? ipPowerUsername = builder.Configuration["IPPower9258:Username"];
string? ipPowerPassword = builder.Configuration["IPPower9258:Password"];

int ipPowerPort = 23;
int alpacaHttpPort = 5000;

if (int.TryParse(
    builder.Configuration["IPPower9258:Port"],
    NumberStyles.Integer,
    CultureInfo.InvariantCulture,
    out int configuredPort))
{
    ipPowerPort = configuredPort;
}

if (int.TryParse(
    builder.Configuration["Alpaca:Port"],
    NumberStyles.Integer,
    CultureInfo.InvariantCulture,
    out int configuredAlpacaPort) &&
    configuredAlpacaPort >= 1 &&
    configuredAlpacaPort <= 65535)
{
    alpacaHttpPort = configuredAlpacaPort;
}

string[] switchNames =
{
    "Prise 1",
    "Prise 2",
    "Prise 3",
    "Prise 4"
};

var configurationLock = new object();

string ProtectPassword(string password)
{
    if (string.IsNullOrEmpty(password))
        return "";

    byte[] clearBytes = Encoding.UTF8.GetBytes(password);
    byte[] protectedBytes = ProtectedData.Protect(
        clearBytes, null, DataProtectionScope.LocalMachine);

    return Convert.ToBase64String(protectedBytes);
}

string UnprotectPassword(string protectedPassword)
{
    if (string.IsNullOrWhiteSpace(protectedPassword))
        return "";

    byte[] protectedBytes = Convert.FromBase64String(protectedPassword);
    byte[] clearBytes = ProtectedData.Unprotect(
        protectedBytes, null, DataProtectionScope.LocalMachine);

    return Encoding.UTF8.GetString(clearBytes);
}

void LoadPersistentSettings()
{
    if (!File.Exists(settingsFilePath))
        return;

    string json = File.ReadAllText(settingsFilePath, Encoding.UTF8);

    using JsonDocument document = JsonDocument.Parse(json);
    JsonElement root = document.RootElement;

    if (root.TryGetProperty("Host", out JsonElement host))
        ipPowerHost = host.GetString();

    if (root.TryGetProperty("TelnetPort", out JsonElement telnetPort) &&
        telnetPort.TryGetInt32(out int p) &&
        p >= 1 && p <= 65535)
    {
        ipPowerPort = p;
    }

    if (root.TryGetProperty("Username", out JsonElement username))
        ipPowerUsername = username.GetString();

    bool legacyPlainTextPasswordLoaded = false;

    if (root.TryGetProperty("PasswordProtected", out JsonElement passwordProtected))
    {
        string? protectedValue = passwordProtected.GetString();
        if (!string.IsNullOrWhiteSpace(protectedValue))
            ipPowerPassword = UnprotectPassword(protectedValue);
    }
    else if (root.TryGetProperty("Password", out JsonElement password))
    {
        ipPowerPassword = password.GetString();
        legacyPlainTextPasswordLoaded = true;
    }

    if (root.TryGetProperty("AlpacaPort", out JsonElement alpacaPort) &&
        alpacaPort.TryGetInt32(out int ap) &&
        ap >= 1 && ap <= 65535)
    {
        alpacaHttpPort = ap;
    }

    if (root.TryGetProperty("SwitchNames", out JsonElement names) &&
        names.ValueKind == JsonValueKind.Array)
    {
        int i = 0;
        foreach (JsonElement name in names.EnumerateArray())
        {
            if (i >= 4)
                break;

            string? value = name.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                switchNames[i] = value;

            i++;
        }
    }

    if (legacyPlainTextPasswordLoaded)
        SavePersistentSettings();
}

void SavePersistentSettings()
{
    EnsureSettingsDirectory();

    object settings = new
    {
        Host = ipPowerHost ?? "",
        TelnetPort = ipPowerPort,
        Username = ipPowerUsername ?? "",
        PasswordProtected = ProtectPassword(ipPowerPassword ?? ""),
        AlpacaPort = alpacaHttpPort,
        SwitchNames = switchNames
    };

    string json = JsonSerializer.Serialize(
        settings,
        new JsonSerializerOptions
        {
            WriteIndented = true
        });

    string temporaryPath = settingsFilePath + ".tmp";
    File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
    File.Move(temporaryPath, settingsFilePath, true);
}

try
{
    MigrateLegacySettingsFile();
    LoadPersistentSettings();
}
catch (Exception ex)
{
    Console.Error.WriteLine(
        $"Impossible de migrer ou charger {settingsFilePath}: {ex.Message}");
}

// Le port HTTP utilisé par Kestrel et le port annoncé par la découverte Alpaca
// proviennent désormais de la même valeur.
builder.WebHost.UseUrls($"http://0.0.0.0:{alpacaHttpPort}");

var app = builder.Build();

// ============================================================================
// SÉCURITÉ DE LA PAGE /setup
// ============================================================================
//
// La page de configuration contient des paramètres sensibles. Elle est donc
// accessible uniquement depuis la machine qui exécute le serveur.
//
// L'API Alpaca, les routes /management et la découverte UDP restent
// accessibles sur le réseau local comme auparavant.
// ============================================================================

app.Use(async (context, next) =>
{
    string path = context.Request.Path.Value ?? "";

    if (path.TrimEnd('/').Equals("/setup", StringComparison.OrdinalIgnoreCase) ||
        path.TrimEnd('/').Equals("/setup/v1/switch/0/setup", StringComparison.OrdinalIgnoreCase))
    {
        IPAddress? remoteAddress = context.Connection.RemoteIpAddress;

        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(
                "La page /setup est accessible uniquement depuis ce PC.");
            return;
        }
    }

    await next();
});

// Alpaca API paths are case-sensitive and must be lower-case.
// ASP.NET Core routing is normally case-insensitive, so reject any /api/
// request whose path contains upper-case characters before endpoint routing.
app.Use(async (context, next) =>
{
    string path = context.Request.Path.Value ?? "";

    if (path.StartsWith("/api/", StringComparison.Ordinal) &&
        path.Any(char.IsUpper))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Alpaca API paths must be lower-case.");
        return;
    }

    await next();
});

// ============================================================================
// IDENTITÉ DU SERVEUR / DRIVER
// ============================================================================

const string ServerName =
    "ASCOM Alpaca IP Power 9258/9258xx";

const string Manufacturer =
    "ASCOM.IPPower9258 Project";

const string ManufacturerVersion = "0.14";

const string Location = "Local network";

const string DeviceName =
    "IP Power 9258/9258xx";

const string DeviceDescription =
    "Aviosys IP Power 9258/9258xx network power switch";

const string UniqueId =
    "a7b52e8d-9258-4a1c-8f31-925800000001";

const string SwitchBase =
    "/api/v1/switch/0";

// ============================================================================
// CODES D'ERREUR ASCOM
// ============================================================================

const int NotImplementedError = 0x400;       // 1024
const int InvalidValueError = 0x401;         // 1025
const int NotConnectedError = 0x407;         // 1031

// Plage réservée aux erreurs spécifiques du pilote.
const int HardwareCommunicationError = 0x500;

// ============================================================================
// ÉTAT GLOBAL
// ============================================================================

long serverTransactionId = 0;

// Etat logique ASCOM.
// Le 9258 n'utilise pas une session Telnet permanente.
bool deviceConnected = false;

// Etat d'une opération Connect()/Disconnect().
bool deviceConnecting = false;

var connectionStateLock = new object();

// Sérialise tous les accès matériels.
var hardwareLock = new SemaphoreSlim(1, 1);

// ============================================================================
// OUTILS ALPACA
// ============================================================================

uint GetClientTransactionIdFromQuery(HttpRequest request)
{
    if (uint.TryParse(
        request.Query["ClientTransactionID"],
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out uint value))
    {
        return value;
    }

    return 0;
}


uint GetClientTransactionId(HttpRequest request, IFormCollection? form = null)
{
    string raw = "";

    if (form is not null)
    {
        raw = form.Keys.Any(k => k == "ClientTransactionID")
            ? form["ClientTransactionID"].ToString()
            : "";
    }

    if (string.IsNullOrWhiteSpace(raw))
    {
        raw = request.Query.Keys.Any(k => k == "ClientTransactionID")
            ? request.Query["ClientTransactionID"].ToString()
            : "";
    }

    return uint.TryParse(
        raw,
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out uint value)
        ? value
        : 0;
}

bool HasWrongCaseFormKey(IFormCollection form, string exactName)
{
    return form.Keys.Any(k =>
        string.Equals(k, exactName, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(k, exactName, StringComparison.Ordinal));
}


IResult ParameterCaseBadRequest(uint clientId, string parameterName)
{
    return Results.Json(
        AlpacaMethodResponse(
            clientId,
            InvalidValueError,
            $"Parameter {parameterName} has incorrect casing."),
        statusCode: StatusCodes.Status400BadRequest);
}

IResult AlpacaBadRequest(
    object response)
{
    return Results.Json(
        response,
        statusCode: StatusCodes.Status400BadRequest);
}

long NextServerTransactionId()
{
    return Interlocked.Increment(ref serverTransactionId);
}

object AlpacaResponse(
    object? value,
    uint clientTransactionId,
    int errorNumber = 0,
    string errorMessage = "")
{
    return new
    {
        Value = value,
        ClientTransactionID = clientTransactionId,
        ServerTransactionID = NextServerTransactionId(),
        ErrorNumber = errorNumber,
        ErrorMessage = errorMessage
    };
}

object AlpacaMethodResponse(
    uint clientTransactionId,
    int errorNumber = 0,
    string errorMessage = "")
{
    return new
    {
        ClientTransactionID = clientTransactionId,
        ServerTransactionID = NextServerTransactionId(),
        ErrorNumber = errorNumber,
        ErrorMessage = errorMessage
    };
}

bool IsValidSwitchId(int id)
{
    return id >= 0 && id < 4;
}

bool IsConnected()
{
    lock (connectionStateLock)
    {
        return deviceConnected;
    }
}

bool IsConnecting()
{
    lock (connectionStateLock)
    {
        return deviceConnecting;
    }
}

void SetConnectedState(bool value)
{
    lock (connectionStateLock)
    {
        deviceConnected = value;
    }
}

void SetConnectingState(bool value)
{
    lock (connectionStateLock)
    {
        deviceConnecting = value;
    }
}

// ============================================================================
// COMMUNICATION TELNET
// ============================================================================

async Task<string> ReadUntilAsync(
    NetworkStream stream,
    string expectedText,
    CancellationToken cancellationToken)
{
    byte[] buffer = new byte[1024];
    var received = new StringBuilder();

    while (true)
    {
        int count = await stream.ReadAsync(
            buffer.AsMemory(0, buffer.Length),
            cancellationToken);

        if (count == 0)
        {
            throw new IOException(
                "Le 9258 a fermé la connexion Telnet.");
        }

        string part =
            Encoding.ASCII.GetString(buffer, 0, count);

        received.Append(part);

        if (received.ToString().Contains(
            expectedText,
            StringComparison.OrdinalIgnoreCase))
        {
            return received.ToString();
        }

        if (received.Length > 16384)
        {
            throw new IOException(
                "Réponse Telnet anormalement longue.");
        }
    }
}

async Task SendLineAsync(
    NetworkStream stream,
    string command,
    CancellationToken cancellationToken)
{
    byte[] bytes =
        Encoding.ASCII.GetBytes(command + "\r\n");

    await stream.WriteAsync(
        bytes.AsMemory(0, bytes.Length),
        cancellationToken);

    await stream.FlushAsync(cancellationToken);
}

// ============================================================================
// DÉCODAGE DU POWER STATUS
// ============================================================================
//
// Firmware V4.01 réellement testé :
//
// Power Status : B4 B3 B2 B1
//
// Retour interne :
//
// states[0] = prise 1
// states[1] = prise 2
// states[2] = prise 3
// states[3] = prise 4
//
// ============================================================================

bool[] ParsePowerStatus(string response)
{
    Match match = Regex.Match(
        response,
        @"Power\s+Status\s*:\s*([01])\s+([01])\s+([01])\s+([01])",
        RegexOptions.IgnoreCase);

    if (!match.Success)
    {
        throw new IOException(
            "La réponse du 9258 ne contient pas un Power Status valide.");
    }

    bool b4 = match.Groups[1].Value == "1";
    bool b3 = match.Groups[2].Value == "1";
    bool b2 = match.Groups[3].Value == "1";
    bool b1 = match.Groups[4].Value == "1";

    return new[]
    {
        b1,
        b2,
        b3,
        b4
    };
}

// ============================================================================
// CONSTRUCTION setpower
// ============================================================================

string BuildSetPowerCommand(bool[] states)
{
    if (states.Length != 4)
    {
        throw new ArgumentException(
            "Quatre états de prise sont requis.");
    }

    char b4 = states[3] ? '1' : '0';
    char b3 = states[2] ? '1' : '0';
    char b2 = states[1] ? '1' : '0';
    char b1 = states[0] ? '1' : '0';

    return $"setpower p6={b4}{b3}{b2}{b1}";
}

// ============================================================================
// OUVERTURE SESSION TELNET AUTHENTIFIÉE
// ============================================================================

async Task<(TcpClient Client, NetworkStream Stream)>
    OpenAuthenticatedSessionWithConfigAsync(
        string? host,
        int port,
        string? username,
        string? password,
        CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(host))
        throw new InvalidOperationException(
            "Le paramètre IPPower9258:Host est absent.");

    if (string.IsNullOrWhiteSpace(username))
        throw new InvalidOperationException(
            "Le paramètre IPPower9258:Username est absent.");

    if (string.IsNullOrWhiteSpace(password))
        throw new InvalidOperationException(
            "Le paramètre IPPower9258:Password est absent.");

    var client = new TcpClient();

    try
    {
        await client.ConnectAsync(
            host,
            port,
            cancellationToken);

        NetworkStream stream = client.GetStream();

        await ReadUntilAsync(
            stream,
            "9258Telnet->",
            cancellationToken);

        await SendLineAsync(
            stream,
            username + ":" + password,
            cancellationToken);

        string authenticationResponse =
            await ReadUntilAsync(
                stream,
                "9258Telnet->",
                cancellationToken);

        if (!authenticationResponse.Contains(
            "Username and password is ok!",
            StringComparison.OrdinalIgnoreCase))
        {
            stream.Dispose();
            client.Dispose();

            throw new UnauthorizedAccessException(
                "Authentification Telnet refusée par l'IP Power 9258.");
        }

        return (client, stream);
    }
    catch
    {
        client.Dispose();
        throw;
    }
}

async Task<(TcpClient Client, NetworkStream Stream)>
    OpenAuthenticatedSessionAsync(
        CancellationToken cancellationToken)
{
    string? host;
    string? username;
    string? password;
    int port;

    lock (configurationLock)
    {
        host = ipPowerHost;
        username = ipPowerUsername;
        password = ipPowerPassword;
        port = ipPowerPort;
    }

    return await OpenAuthenticatedSessionWithConfigAsync(
        host,
        port,
        username,
        password,
        cancellationToken);
}

// ============================================================================
// LECTURE MATÉRIELLE
// ============================================================================

async Task<bool[]> Read9258PowerStatusAsync()
{
    await hardwareLock.WaitAsync();

    try
    {
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(5));

        var session =
            await OpenAuthenticatedSessionAsync(
                timeout.Token);

        using TcpClient client = session.Client;
        using NetworkStream stream = session.Stream;

        await SendLineAsync(
            stream,
            "getpower",
            timeout.Token);

        string response =
            await ReadUntilAsync(
                stream,
                "9258Telnet->",
                timeout.Token);

        return ParsePowerStatus(response);
    }
    catch (OperationCanceledException)
    {
        throw new IOException(
            "Délai d'attente dépassé lors de la communication avec l'IP Power 9258.");
    }
    finally
    {
        hardwareLock.Release();
    }
}

// ============================================================================
// TEST DE CONNEXION MATÉRIELLE
// ============================================================================

async Task TestHardwareConnectionAsync()
{
    // Une vraie lecture getpower valide :
    // TCP + Telnet + authentification + protocole + réponse.
    await Read9258PowerStatusAsync();
}

async Task<bool[]> TestSetupConnectionAsync(
    string host,
    int port,
    string username,
    string password)
{
    await hardwareLock.WaitAsync();

    try
    {
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(5));

        var session =
            await OpenAuthenticatedSessionWithConfigAsync(
                host,
                port,
                username,
                password,
                timeout.Token);

        using TcpClient client = session.Client;
        using NetworkStream stream = session.Stream;

        await SendLineAsync(
            stream,
            "getpower",
            timeout.Token);

        string response =
            await ReadUntilAsync(
                stream,
                "9258Telnet->",
                timeout.Token);

        return ParsePowerStatus(response);
    }
    catch (OperationCanceledException)
    {
        throw new IOException(
            "Délai d'attente dépassé lors du test de connexion avec l'IP Power 9258.");
    }
    finally
    {
        hardwareLock.Release();
    }
}

// ============================================================================
// ÉCRITURE MATÉRIELLE SÉCURISÉE
// ============================================================================

async Task Set9258SwitchAsync(
    int id,
    bool requestedState)
{
    await hardwareLock.WaitAsync();

    try
    {
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(8));

        var session =
            await OpenAuthenticatedSessionAsync(
                timeout.Token);

        using TcpClient client = session.Client;
        using NetworkStream stream = session.Stream;

        // 1. Lecture préalable.
        await SendLineAsync(
            stream,
            "getpower",
            timeout.Token);

        string initialResponse =
            await ReadUntilAsync(
                stream,
                "9258Telnet->",
                timeout.Token);

        bool[] states =
            ParsePowerStatus(initialResponse);

        // 2. Aucun setpower si l'état est déjà correct.
        if (states[id] == requestedState)
            return;

        // 3. Un seul bit est modifié.
        states[id] = requestedState;

        string command =
            BuildSetPowerCommand(states);

        // 4. Ecriture.
        await SendLineAsync(
            stream,
            command,
            timeout.Token);

        string setResponse =
            await ReadUntilAsync(
                stream,
                "9258Telnet->",
                timeout.Token);

        bool[] returnedStates =
            ParsePowerStatus(setResponse);

        // 5. Vérification immédiate.
        for (int i = 0; i < 4; i++)
        {
            if (returnedStates[i] != states[i])
            {
                throw new IOException(
                    "Le 9258 n'a pas confirmé l'état demandé après setpower.");
            }
        }

        // 6. Nouvelle lecture indépendante.
        await SendLineAsync(
            stream,
            "getpower",
            timeout.Token);

        string verificationResponse =
            await ReadUntilAsync(
                stream,
                "9258Telnet->",
                timeout.Token);

        bool[] verifiedStates =
            ParsePowerStatus(verificationResponse);

        for (int i = 0; i < 4; i++)
        {
            if (verifiedStates[i] != states[i])
            {
                throw new IOException(
                    "La vérification getpower ne correspond pas à l'état demandé.");
            }
        }
    }
    catch (OperationCanceledException)
    {
        throw new IOException(
            "Délai d'attente dépassé lors de l'écriture vers l'IP Power 9258.");
    }
    finally
    {
        hardwareLock.Release();
    }
}

// ============================================================================
// PAGE DE CONFIGURATION v0.14
// ============================================================================

string Html(string? value) =>
    HtmlEncoder.Default.Encode(value ?? "");

string BuildSetupPage(
    string message = "",
    bool success = true,
    string? formHost = null,
    int? formTelnetPort = null,
    string? formUsername = null,
    int? formAlpacaPort = null,
    string[]? formSwitchNames = null,
    bool useSubmittedValues = false)
{
    string banner = string.IsNullOrWhiteSpace(message)
        ? ""
        : $"<div class=\"message {(success ? "ok" : "error")}\">{Html(message)}</div>";

    string displayedHost =
        useSubmittedValues ? formHost ?? "" : ipPowerHost ?? "";

    int displayedTelnetPort =
        useSubmittedValues ? formTelnetPort ?? ipPowerPort : ipPowerPort;

    string displayedUsername =
        useSubmittedValues ? formUsername ?? "" : ipPowerUsername ?? "";

    int displayedAlpacaPort =
        useSubmittedValues ? formAlpacaPort ?? alpacaHttpPort : alpacaHttpPort;

    string[] displayedSwitchNames =
        useSubmittedValues && formSwitchNames is { Length: 4 }
            ? formSwitchNames
            : switchNames;

    return $$"""
<!doctype html>
<html lang="fr">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>ASCOM Alpaca IP Power 9258 - Configuration</title>
<style>
body{font-family:Segoe UI,Arial,sans-serif;background:#f4f6f8;margin:0;color:#222}
main{max-width:760px;margin:35px auto;background:white;padding:28px 34px;border-radius:10px;box-shadow:0 2px 12px #0002}
h1{font-size:24px;margin-top:0} h2{font-size:18px;margin-top:28px}
.grid{display:grid;grid-template-columns:190px 1fr;gap:12px 16px;align-items:center}
input{box-sizing:border-box;width:100%;padding:8px;border:1px solid #aaa;border-radius:5px}
input[type=number]{max-width:180px}
.buttons{margin-top:24px;display:flex;gap:12px;flex-wrap:wrap}
button{padding:9px 18px;border:0;border-radius:5px;cursor:pointer;font-weight:600}
.save{background:#1769aa;color:white}.test{background:#e5e7eb;color:#111}
.note{font-size:13px;color:#555;margin-top:8px}
.message{padding:12px;margin:18px 0;border-radius:5px;white-space:pre-line}
.ok{background:#e7f6e7;border:1px solid #75b875}.error{background:#fdeaea;border:1px solid #d88}
.state{font-family:Consolas,monospace}
</style>
</head>
<body>
<main>
<h1>ASCOM Alpaca IP Power 9258/9258xx</h1>
<p>Configuration v0.14</p>
{{banner}}
<form method="post">
<h2>IP Power 9258</h2>
<div class="grid">
<label for="Host">Adresse IP / nom :</label>
<input id="Host" name="Host" value="{{Html(displayedHost)}}" required>

<label for="TelnetPort">Port Telnet :</label>
<input id="TelnetPort" name="TelnetPort" type="number" min="1" max="65535" value="{{displayedTelnetPort}}" required>

<label for="Username">Utilisateur :</label>
<input id="Username" name="Username" value="{{Html(displayedUsername)}}" autocomplete="username" required>

<label for="Password">Mot de passe :</label>
<input id="Password" name="Password" type="password" value="" autocomplete="current-password"
       placeholder="Laisser vide pour conserver le mot de passe actuel">
</div>

<h2>Noms des prises</h2>
<div class="grid">
<label for="Switch1">Prise 1 :</label><input id="Switch1" name="Switch1" value="{{Html(displayedSwitchNames[0])}}" required>
<label for="Switch2">Prise 2 :</label><input id="Switch2" name="Switch2" value="{{Html(displayedSwitchNames[1])}}" required>
<label for="Switch3">Prise 3 :</label><input id="Switch3" name="Switch3" value="{{Html(displayedSwitchNames[2])}}" required>
<label for="Switch4">Prise 4 :</label><input id="Switch4" name="Switch4" value="{{Html(displayedSwitchNames[3])}}" required>
</div>

<h2>ASCOM Alpaca</h2>
<div class="grid">
<label for="AlpacaPort">Port HTTP Alpaca :</label>
<input id="AlpacaPort" name="AlpacaPort" type="number" min="1" max="65535" value="{{displayedAlpacaPort}}" required>
</div>
<p class="note">Un changement du port Alpaca est enregistré mais ne prend effet qu'au prochain démarrage du serveur.</p>

<div class="buttons">
<button class="test" type="submit" name="Action" value="test">Tester la connexion</button>
<button class="save" type="submit" name="Action" value="save">Enregistrer</button>
</div>
</form>
</main>
</body>
</html>
""";
}

bool TryReadSetupForm(
    IFormCollection form,
    out string host,
    out int telnetPort,
    out string username,
    out string password,
    out int requestedAlpacaPort,
    out string[] names,
    out string error)
{
    host = form["Host"].ToString().Trim();
    username = form["Username"].ToString().Trim();
    password = form["Password"].ToString();
    names = new[]
    {
        form["Switch1"].ToString().Trim(),
        form["Switch2"].ToString().Trim(),
        form["Switch3"].ToString().Trim(),
        form["Switch4"].ToString().Trim()
    };

    error = "";

    if (string.IsNullOrWhiteSpace(host))
    {
        telnetPort = 0;
        requestedAlpacaPort = 0;
        error = "L'adresse IP / le nom du 9258 est obligatoire.";
        return false;
    }

    if (string.IsNullOrWhiteSpace(username))
    {
        telnetPort = 0;
        requestedAlpacaPort = 0;
        error = "Le nom utilisateur est obligatoire.";
        return false;
    }

    if (!int.TryParse(form["TelnetPort"], out telnetPort) ||
        telnetPort < 1 || telnetPort > 65535)
    {
        requestedAlpacaPort = 0;
        error = "Le port Telnet doit être compris entre 1 et 65535.";
        return false;
    }

    if (!int.TryParse(form["AlpacaPort"], out requestedAlpacaPort) ||
        requestedAlpacaPort < 1 || requestedAlpacaPort > 65535)
    {
        error = "Le port Alpaca doit être compris entre 1 et 65535.";
        return false;
    }

    if (names.Any(string.IsNullOrWhiteSpace))
    {
        error = "Les quatre noms de prise doivent être renseignés.";
        return false;
    }

    return true;
}


// Mot de passe validé par "Tester la connexion", conservé uniquement en mémoire.
// Il n'est jamais réinjecté dans le HTML et n'est réutilisé que pour
// l'enregistrement correspondant au même hôte / port / utilisateur.
var setupTestPasswordLock = new object();
string? setupTestPassword = null;
string? setupTestHost = null;
int setupTestTelnetPort = 0;
string? setupTestUsername = null;

void ClearSetupTestPassword()
{
    lock (setupTestPasswordLock)
    {
        setupTestPassword = null;
        setupTestHost = null;
        setupTestTelnetPort = 0;
        setupTestUsername = null;
    }
}

void RememberSetupTestPassword(
    string host,
    int telnetPort,
    string username,
    string password)
{
    lock (setupTestPasswordLock)
    {
        setupTestHost = host;
        setupTestTelnetPort = telnetPort;
        setupTestUsername = username;
        setupTestPassword = password;
    }
}

string? TakeSetupTestPasswordIfMatching(
    string host,
    int telnetPort,
    string username)
{
    lock (setupTestPasswordLock)
    {
        bool matches =
            setupTestPassword is not null &&
            string.Equals(setupTestHost, host, StringComparison.Ordinal) &&
            setupTestTelnetPort == telnetPort &&
            string.Equals(setupTestUsername, username, StringComparison.Ordinal);

        string? value = matches ? setupTestPassword : null;

        setupTestPassword = null;
        setupTestHost = null;
        setupTestTelnetPort = 0;
        setupTestUsername = null;

        return value;
    }
}

// Les deux URL utilisent la même page et le même traitement du formulaire.
// Le formulaire sans attribut action est envoyé à l'URL actuellement ouverte.
app.MapGet("/setup", () =>
    Results.Content(BuildSetupPage(), "text/html; charset=utf-8"));

app.MapGet("/setup/v1/switch/0/setup", () =>
    Results.Content(BuildSetupPage(), "text/html; charset=utf-8"));

app.MapPost("/setup", HandleSetupPostAsync);
app.MapPost("/setup/v1/switch/0/setup", HandleSetupPostAsync);

async Task<IResult> HandleSetupPostAsync(HttpRequest request)
{
    IFormCollection form = await request.ReadFormAsync();

    if (!TryReadSetupForm(
        form,
        out string host,
        out int telnetPort,
        out string username,
        out string password,
        out int requestedAlpacaPort,
        out string[] names,
        out string error))
    {
        return Results.Content(
            BuildSetupPage(error, false),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status400BadRequest);
    }

    string action = form["Action"].ToString();

    if (action == "test")
    {
        string testPassword;

        lock (configurationLock)
        {
            // Un champ vide signifie : utiliser le mot de passe actif,
            // sans modifier la configuration globale.
            testPassword = string.IsNullOrEmpty(password)
                ? ipPowerPassword ?? ""
                : password;
        }

        ClearSetupTestPassword();

        try
        {
            bool[] states =
                await TestSetupConnectionAsync(
                    host,
                    telnetPort,
                    username,
                    testPassword);

            RememberSetupTestPassword(
                host,
                telnetPort,
                username,
                testPassword);

            string stateText =
                $"Connexion réussie.\n" +
                $"Prise 1 : {(states[0] ? "ON" : "OFF")}\n" +
                $"Prise 2 : {(states[1] ? "ON" : "OFF")}\n" +
                $"Prise 3 : {(states[2] ? "ON" : "OFF")}\n" +
                $"Prise 4 : {(states[3] ? "ON" : "OFF")}";

            return Results.Content(
                BuildSetupPage(
                    stateText,
                    true,
                    host,
                    telnetPort,
                    username,
                    requestedAlpacaPort,
                    names,
                    true),
                "text/html; charset=utf-8");
        }
        catch (Exception ex)
        {
            return Results.Content(
                BuildSetupPage(
                    "Échec du test : " + ex.Message,
                    false,
                    host,
                    telnetPort,
                    username,
                    requestedAlpacaPort,
                    names,
                    true),
                "text/html; charset=utf-8",
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    if (action != "save")
    {
        return Results.Content(
            BuildSetupPage("Action inconnue.", false),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status400BadRequest);
    }

    try
    {
        lock (configurationLock)
        {
            ipPowerHost = host;
            ipPowerUsername = username;
            ipPowerPort = telnetPort;

            string? testedPassword =
                TakeSetupTestPasswordIfMatching(
                    host,
                    telnetPort,
                    username);

            // Un mot de passe explicitement saisi est prioritaire.
            // Sinon, utilise le secret validé par le dernier test correspondant.
            // À défaut, conserve le mot de passe déjà chargé.
            if (!string.IsNullOrEmpty(password))
                ipPowerPassword = password;
            else if (testedPassword is not null)
                ipPowerPassword = testedPassword;

            alpacaHttpPort = requestedAlpacaPort;

            for (int i = 0; i < 4; i++)
                switchNames[i] = names[i];

            SavePersistentSettings();
        }

        SetConnectedState(false);
        SetConnectingState(false);

        string message = "Configuration enregistrée.";

        message +=
            $"\nFichier : {settingsFilePath}" +
            "\nSi le port Alpaca a été modifié, redémarrez le serveur.";

        return Results.Content(
            BuildSetupPage(message, true),
            "text/html; charset=utf-8");
    }
    catch (Exception ex)
    {
        return Results.Content(
            BuildSetupPage(
                "Impossible d'enregistrer la configuration : " + ex.Message,
                false),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status500InternalServerError);
    }
}

// ============================================================================
// PAGE D'ACCUEIL
// ============================================================================

app.MapGet("/", () =>
    Results.Text(
        "ASCOM Alpaca IP Power 9258/9258xx - Platform 7 v0.14"));

// ============================================================================
// MANAGEMENT API
// ============================================================================

app.MapGet("/management/apiversions", (HttpRequest request) =>
{
    return Results.Json(
        AlpacaResponse(
            new[] { 1 },
            GetClientTransactionIdFromQuery(request)));
});

app.MapGet("/management/v1/description", (HttpRequest request) =>
{
    return Results.Json(
        AlpacaResponse(
            new
            {
                ServerName,
                Manufacturer,
                ManufacturerVersion,
                Location
            },
            GetClientTransactionIdFromQuery(request)));
});

app.MapGet("/management/v1/configureddevices", (HttpRequest request) =>
{
    var devices = new[]
    {
        new
        {
            DeviceName,
            DeviceType = "Switch",
            DeviceNumber = 0,
            UniqueID = UniqueId
        }
    };

    return Results.Json(
        AlpacaResponse(
            devices,
            GetClientTransactionIdFromQuery(request)));
});

// ============================================================================
// PROPRIÉTÉS COMMUNES
// ============================================================================

app.MapGet(SwitchBase + "/name", (HttpRequest request) =>
    Results.Json(
        AlpacaResponse(
            DeviceName,
            GetClientTransactionIdFromQuery(request))));

app.MapGet(SwitchBase + "/description", (HttpRequest request) =>
    Results.Json(
        AlpacaResponse(
            DeviceDescription,
            GetClientTransactionIdFromQuery(request))));

app.MapGet(SwitchBase + "/driverinfo", (HttpRequest request) =>
    Results.Json(
        AlpacaResponse(
            "ASCOM Alpaca driver for Aviosys IP Power 9258/9258xx",
            GetClientTransactionIdFromQuery(request))));

app.MapGet(SwitchBase + "/driverversion", (HttpRequest request) =>
    Results.Json(
        AlpacaResponse(
            ManufacturerVersion,
            GetClientTransactionIdFromQuery(request))));

app.MapGet(SwitchBase + "/interfaceversion", (HttpRequest request) =>
    Results.Json(
        AlpacaResponse(
            3,
            GetClientTransactionIdFromQuery(request))));

app.MapGet(SwitchBase + "/supportedactions", (HttpRequest request) =>
    Results.Json(
        AlpacaResponse(
            Array.Empty<string>(),
            GetClientTransactionIdFromQuery(request))));

// ============================================================================
// CONNECTED GET
// ============================================================================

app.MapGet(SwitchBase + "/connected", (HttpRequest request) =>
{
    return Results.Json(
        AlpacaResponse(
            IsConnected(),
            GetClientTransactionIdFromQuery(request)));
});

// ============================================================================
// CONNECTED PUT
// ============================================================================

app.MapPut(SwitchBase + "/connected", async (HttpRequest request) =>
{
    IFormCollection form = await request.ReadFormAsync();

    uint clientId =
        GetClientTransactionId(request, form);

    if (HasWrongCaseFormKey(form, "Connected"))
        return ParameterCaseBadRequest(clientId, "Connected");


    if (!bool.TryParse(
        form["Connected"],
        out bool requestedState))
    {
        return AlpacaBadRequest(
            AlpacaMethodResponse(
                clientId,
                InvalidValueError,
                "Connected must be true or false."));
    }

    if (!requestedState)
    {
        SetConnectedState(false);

        return Results.Json(
            AlpacaMethodResponse(clientId));
    }

    // Idempotent.
    if (IsConnected())
    {
        return Results.Json(
            AlpacaMethodResponse(clientId));
    }

    try
    {
        SetConnectingState(true);

        await TestHardwareConnectionAsync();

        SetConnectedState(true);

        return Results.Json(
            AlpacaMethodResponse(clientId));
    }
    catch (Exception ex)
    {
        SetConnectedState(false);

        return Results.Json(
            AlpacaMethodResponse(
                clientId,
                HardwareCommunicationError,
                ex.Message));
    }
    finally
    {
        SetConnectingState(false);
    }
});

// ============================================================================
// CONNECTING
// ============================================================================

app.MapGet(SwitchBase + "/connecting", (HttpRequest request) =>
{
    return Results.Json(
        AlpacaResponse(
            IsConnecting(),
            GetClientTransactionIdFromQuery(request)));
});

// ============================================================================
// CONNECT
// ============================================================================
//
// Platform 7 : déclenche la connexion.
// Cette implémentation lance le test matériel en arrière-plan et retourne
// immédiatement.
// ============================================================================

app.MapPut(SwitchBase + "/connect", async (HttpRequest request) =>
{
    IFormCollection? form = null;

    if (request.HasFormContentType)
        form = await request.ReadFormAsync();

    uint clientId = GetClientTransactionId(request, form);


    if (IsConnected() || IsConnecting())
    {
        return Results.Json(
            AlpacaMethodResponse(clientId));
    }

    SetConnectingState(true);

    _ = Task.Run(async () =>
    {
        try
        {
            await TestHardwareConnectionAsync();
            SetConnectedState(true);
        }
        catch
        {
            SetConnectedState(false);
        }
        finally
        {
            SetConnectingState(false);
        }
    });

    return Results.Json(
        AlpacaMethodResponse(clientId));
});

// ============================================================================
// DISCONNECT
// ============================================================================

app.MapPut(SwitchBase + "/disconnect", async (HttpRequest request) =>
{
    IFormCollection? form = null;

    if (request.HasFormContentType)
        form = await request.ReadFormAsync();

    uint clientId = GetClientTransactionId(request, form);


    SetConnectedState(false);
    SetConnectingState(false);

    return Results.Json(
        AlpacaMethodResponse(clientId));
});

// ============================================================================
// DEVICESTATE
// ============================================================================

app.MapGet(SwitchBase + "/devicestate", async (HttpRequest request) =>
{
    uint clientId =
        GetClientTransactionIdFromQuery(request);

    if (!IsConnected())
    {
        return Results.Json(
            AlpacaResponse(
                null,
                clientId,
                NotConnectedError,
                "The IP Power 9258 is not connected."));
    }

    try
    {
        bool[] states =
            await Read9258PowerStatusAsync();

        var deviceState = new object[]
        {
            new { Name = "GetSwitch0", Value = (object)states[0] },
            new { Name = "GetSwitch1", Value = (object)states[1] },
            new { Name = "GetSwitch2", Value = (object)states[2] },
            new { Name = "GetSwitch3", Value = (object)states[3] },
            new { Name = "GetSwitchValue0", Value = (object)(states[0] ? 1.0 : 0.0) },
            new { Name = "GetSwitchValue1", Value = (object)(states[1] ? 1.0 : 0.0) },
            new { Name = "GetSwitchValue2", Value = (object)(states[2] ? 1.0 : 0.0) },
            new { Name = "GetSwitchValue3", Value = (object)(states[3] ? 1.0 : 0.0) }
        };

        return Results.Json(
            AlpacaResponse(
                deviceState,
                clientId));
    }
    catch (Exception ex)
    {
        return Results.Json(
            AlpacaResponse(
                null,
                clientId,
                HardwareCommunicationError,
                ex.Message));
    }
});

// ============================================================================
// MAXSWITCH
// ============================================================================

app.MapGet(SwitchBase + "/maxswitch", (HttpRequest request) =>
{
    uint clientId =
        GetClientTransactionIdFromQuery(request);

    if (!IsConnected())
    {
        return Results.Json(
            AlpacaResponse(
                null,
                clientId,
                NotConnectedError,
                "The IP Power 9258 is not connected."));
    }

    return Results.Json(
        AlpacaResponse(4, clientId));
});

// ============================================================================
// GETSWITCHNAME
// ============================================================================

app.MapGet(SwitchBase + "/getswitchname", (HttpRequest request) =>
{
    uint clientId =
        GetClientTransactionIdFromQuery(request);

    if (!int.TryParse(request.Query["Id"], out int id) ||
        !IsValidSwitchId(id))
    {
        return Results.Json(
            AlpacaResponse(
                null,
                clientId,
                InvalidValueError,
                "Switch Id must be between 0 and 3."));
    }

    return Results.Json(
        AlpacaResponse(
            switchNames[id],
            clientId));
});

// ============================================================================
// SETSWITCHNAME
// ============================================================================

app.MapPut(SwitchBase + "/setswitchname", async (HttpRequest request) =>
{
    IFormCollection form = await request.ReadFormAsync();

    uint clientId =
        GetClientTransactionId(request, form);

    if (HasWrongCaseFormKey(form, "Id"))
        return ParameterCaseBadRequest(clientId, "Id");

    if (HasWrongCaseFormKey(form, "Name"))
        return ParameterCaseBadRequest(clientId, "Name");


    if (!int.TryParse(form["Id"], out int id) ||
        !IsValidSwitchId(id))
    {
        return Results.Json(
            AlpacaMethodResponse(
                clientId,
                InvalidValueError,
                "Switch Id must be between 0 and 3."));
    }

    string newName = form["Name"].ToString();

    if (string.IsNullOrWhiteSpace(newName))
    {
        return Results.Json(
            AlpacaMethodResponse(
                clientId,
                InvalidValueError,
                "Switch name cannot be empty."));
    }

    switchNames[id] = newName;

    return Results.Json(
        AlpacaMethodResponse(clientId));
});

// ============================================================================
// GETSWITCHDESCRIPTION
// ============================================================================

app.MapGet(SwitchBase + "/getswitchdescription", (HttpRequest request) =>
{
    uint clientId =
        GetClientTransactionIdFromQuery(request);

    if (!int.TryParse(request.Query["Id"], out int id) ||
        !IsValidSwitchId(id))
    {
        return Results.Json(
            AlpacaResponse(
                null,
                clientId,
                InvalidValueError,
                "Switch Id must be between 0 and 3."));
    }

    return Results.Json(
        AlpacaResponse(
            $"IP Power 9258 physical outlet {id + 1}",
            clientId));
});

// ============================================================================
// CANWRITE
// ============================================================================

app.MapGet(SwitchBase + "/canwrite", (HttpRequest request) =>
{
    uint clientId =
        GetClientTransactionIdFromQuery(request);

    if (!int.TryParse(request.Query["Id"], out int id) ||
        !IsValidSwitchId(id))
    {
        return Results.Json(
            AlpacaResponse(
                null,
                clientId,
                InvalidValueError,
                "Switch Id must be between 0 and 3."));
    }

    if (!IsConnected())
    {
        return Results.Json(
            AlpacaResponse(
                null,
                clientId,
                NotConnectedError,
                "The IP Power 9258 is not connected."));
    }

    return Results.Json(
        AlpacaResponse(true, clientId));
});

// ============================================================================
// CANASYNC
// ============================================================================

app.MapGet(SwitchBase + "/canasync", (HttpRequest request) =>
{
    uint clientId =
        GetClientTransactionIdFromQuery(request);

    if (!int.TryParse(request.Query["Id"], out int id) ||
        !IsValidSwitchId(id))
    {
        return Results.Json(
            AlpacaResponse(
                null,
                clientId,
                InvalidValueError,
                "Switch Id must be between 0 and 3."));
    }

    if (!IsConnected())
    {
        return Results.Json(
            AlpacaResponse(
                null,
                clientId,
                NotConnectedError,
                "The IP Power 9258 is not connected."));
    }

    return Results.Json(
        AlpacaResponse(false, clientId));
});

// ============================================================================
// GETSWITCH
// ============================================================================

app.MapGet(SwitchBase + "/getswitch", async (HttpRequest request) =>
{
    uint clientId =
        GetClientTransactionIdFromQuery(request);

    if (!int.TryParse(request.Query["Id"], out int id) ||
        !IsValidSwitchId(id))
    {
        return Results.Json(
            AlpacaResponse(
                null,
                clientId,
                InvalidValueError,
                "Switch Id must be between 0 and 3."));
    }

    if (!IsConnected())
    {
        return Results.Json(
            AlpacaResponse(
                null,
                clientId,
                NotConnectedError,
                "The IP Power 9258 is not connected."));
    }

    try
    {
        bool[] states =
            await Read9258PowerStatusAsync();

        return Results.Json(
            AlpacaResponse(
                states[id],
                clientId));
    }
    catch (Exception ex)
    {
        return Results.Json(
            AlpacaResponse(
                null,
                clientId,
                HardwareCommunicationError,
                ex.Message));
    }
});

// ============================================================================
// GETSWITCHVALUE
// ============================================================================

app.MapGet(SwitchBase + "/getswitchvalue", async (HttpRequest request) =>
{
    uint clientId =
        GetClientTransactionIdFromQuery(request);

    if (!int.TryParse(request.Query["Id"], out int id) ||
        !IsValidSwitchId(id))
    {
        return Results.Json(
            AlpacaResponse(
                null,
                clientId,
                InvalidValueError,
                "Switch Id must be between 0 and 3."));
    }

    if (!IsConnected())
    {
        return Results.Json(
            AlpacaResponse(
                null,
                clientId,
                NotConnectedError,
                "The IP Power 9258 is not connected."));
    }

    try
    {
        bool[] states =
            await Read9258PowerStatusAsync();

        return Results.Json(
            AlpacaResponse(
                states[id] ? 1.0 : 0.0,
                clientId));
    }
    catch (Exception ex)
    {
        return Results.Json(
            AlpacaResponse(
                null,
                clientId,
                HardwareCommunicationError,
                ex.Message));
    }
});

// ============================================================================
// MIN / MAX / STEP
// ============================================================================

app.MapGet(SwitchBase + "/minswitchvalue", (HttpRequest request) =>
{
    uint clientId = GetClientTransactionIdFromQuery(request);

    if (!int.TryParse(request.Query["Id"], out int id) ||
        !IsValidSwitchId(id))
        return Results.Json(
            AlpacaResponse(null, clientId, InvalidValueError,
                "Switch Id must be between 0 and 3."));

    if (!IsConnected())
        return Results.Json(
            AlpacaResponse(null, clientId, NotConnectedError,
                "The IP Power 9258 is not connected."));

    return Results.Json(
        AlpacaResponse(0.0, clientId));
});

app.MapGet(SwitchBase + "/maxswitchvalue", (HttpRequest request) =>
{
    uint clientId = GetClientTransactionIdFromQuery(request);

    if (!int.TryParse(request.Query["Id"], out int id) ||
        !IsValidSwitchId(id))
        return Results.Json(
            AlpacaResponse(null, clientId, InvalidValueError,
                "Switch Id must be between 0 and 3."));

    if (!IsConnected())
        return Results.Json(
            AlpacaResponse(null, clientId, NotConnectedError,
                "The IP Power 9258 is not connected."));

    return Results.Json(
        AlpacaResponse(1.0, clientId));
});

app.MapGet(SwitchBase + "/switchstep", (HttpRequest request) =>
{
    uint clientId = GetClientTransactionIdFromQuery(request);

    if (!int.TryParse(request.Query["Id"], out int id) ||
        !IsValidSwitchId(id))
        return Results.Json(
            AlpacaResponse(null, clientId, InvalidValueError,
                "Switch Id must be between 0 and 3."));

    if (!IsConnected())
        return Results.Json(
            AlpacaResponse(null, clientId, NotConnectedError,
                "The IP Power 9258 is not connected."));

    return Results.Json(
        AlpacaResponse(1.0, clientId));
});

// ============================================================================
// SETSWITCH
// ============================================================================

app.MapPut(SwitchBase + "/setswitch", async (HttpRequest request) =>
{
    IFormCollection form = await request.ReadFormAsync();

    uint clientId =
        GetClientTransactionId(request, form);

    if (HasWrongCaseFormKey(form, "Id"))
        return ParameterCaseBadRequest(clientId, "Id");

    if (HasWrongCaseFormKey(form, "State"))
        return ParameterCaseBadRequest(clientId, "State");


    if (!int.TryParse(form["Id"], out int id) ||
        !IsValidSwitchId(id))
        return Results.Json(
            AlpacaMethodResponse(
                clientId,
                InvalidValueError,
                "Switch Id must be between 0 and 3."));

    if (!bool.TryParse(form["State"], out bool state))
        return Results.Json(
            AlpacaMethodResponse(
                clientId,
                InvalidValueError,
                "State must be true or false."));

    if (!IsConnected())
        return Results.Json(
            AlpacaMethodResponse(
                clientId,
                NotConnectedError,
                "The IP Power 9258 is not connected."));

    try
    {
        await Set9258SwitchAsync(id, state);

        return Results.Json(
            AlpacaMethodResponse(clientId));
    }
    catch (Exception ex)
    {
        return Results.Json(
            AlpacaMethodResponse(
                clientId,
                HardwareCommunicationError,
                ex.Message));
    }
});

// ============================================================================
// SETSWITCHVALUE
// ============================================================================

app.MapPut(SwitchBase + "/setswitchvalue", async (HttpRequest request) =>
{
    IFormCollection form = await request.ReadFormAsync();

    uint clientId =
        GetClientTransactionId(request, form);

    if (HasWrongCaseFormKey(form, "Id"))
        return ParameterCaseBadRequest(clientId, "Id");

    if (HasWrongCaseFormKey(form, "Value"))
        return ParameterCaseBadRequest(clientId, "Value");


    if (!int.TryParse(form["Id"], out int id) ||
        !IsValidSwitchId(id))
        return Results.Json(
            AlpacaMethodResponse(
                clientId,
                InvalidValueError,
                "Switch Id must be between 0 and 3."));

    if (!double.TryParse(
        form["Value"],
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
        out double value))
        return Results.Json(
            AlpacaMethodResponse(
                clientId,
                InvalidValueError,
                "Value must be 0.0 or 1.0."));

    if (value < 0.0 || value > 1.0)
        return Results.Json(
            AlpacaMethodResponse(
                clientId,
                InvalidValueError,
                "Value must be between 0.0 and 1.0."));

    if (!IsConnected())
        return Results.Json(
            AlpacaMethodResponse(
                clientId,
                NotConnectedError,
                "The IP Power 9258 is not connected."));

    try
    {
        // ASCOM Switch: values between legal steps are rounded to the
        // nearest supported step. For this binary switch, 0.0..0.499...
        // maps to OFF and 0.5..1.0 maps to ON.
        bool requestedState = value >= 0.5;

        await Set9258SwitchAsync(
            id,
            requestedState);

        return Results.Json(
            AlpacaMethodResponse(clientId));
    }
    catch (Exception ex)
    {
        return Results.Json(
            AlpacaMethodResponse(
                clientId,
                HardwareCommunicationError,
                ex.Message));
    }
});

// ============================================================================
// SWITCH ASYNCHRONE PLATFORM 7
//
// Le 9258 n'en a pas besoin : CanAsync = false.
// ============================================================================

app.MapPut(SwitchBase + "/setasync", async (HttpRequest request) =>
{
    IFormCollection form = await request.ReadFormAsync();
    uint clientId = GetClientTransactionId(request, form);

    if (HasWrongCaseFormKey(form, "Id"))
        return ParameterCaseBadRequest(clientId, "Id");

    if (HasWrongCaseFormKey(form, "State"))
        return ParameterCaseBadRequest(clientId, "State");


    if (!int.TryParse(form["Id"], out int id) ||
        !IsValidSwitchId(id))
        return Results.Json(
            AlpacaMethodResponse(
                clientId,
                InvalidValueError,
                "Switch Id must be between 0 and 3."));

    return Results.Json(
        AlpacaMethodResponse(
            clientId,
            NotImplementedError,
            "Asynchronous switch operations are not supported."));
});

app.MapPut(SwitchBase + "/setasyncvalue", async (HttpRequest request) =>
{
    IFormCollection form = await request.ReadFormAsync();
    uint clientId = GetClientTransactionId(request, form);

    if (HasWrongCaseFormKey(form, "Id"))
        return ParameterCaseBadRequest(clientId, "Id");

    if (HasWrongCaseFormKey(form, "Value"))
        return ParameterCaseBadRequest(clientId, "Value");


    if (!int.TryParse(form["Id"], out int id) ||
        !IsValidSwitchId(id))
        return Results.Json(
            AlpacaMethodResponse(
                clientId,
                InvalidValueError,
                "Switch Id must be between 0 and 3."));

    return Results.Json(
        AlpacaMethodResponse(
            clientId,
            NotImplementedError,
            "Asynchronous switch operations are not supported."));
});

app.MapGet(SwitchBase + "/statechangecomplete", (HttpRequest request) =>
{
    uint clientId =
        GetClientTransactionIdFromQuery(request);

    if (!int.TryParse(request.Query["Id"], out int id) ||
        !IsValidSwitchId(id))
        return Results.Json(
            AlpacaResponse(
                null,
                clientId,
                InvalidValueError,
                "Switch Id must be between 0 and 3."));

    return Results.Json(
        AlpacaResponse(
            null,
            clientId,
            NotImplementedError,
            "Asynchronous switch operations are not supported."));
});

app.MapPut(SwitchBase + "/cancelasync", async (HttpRequest request) =>
{
    IFormCollection form = await request.ReadFormAsync();
    uint clientId = GetClientTransactionId(request, form);

    if (HasWrongCaseFormKey(form, "Id"))
        return ParameterCaseBadRequest(clientId, "Id");


    if (!int.TryParse(form["Id"], out int id) ||
        !IsValidSwitchId(id))
        return Results.Json(
            AlpacaMethodResponse(
                clientId,
                InvalidValueError,
                "Switch Id must be between 0 and 3."));

    return Results.Json(
        AlpacaMethodResponse(
            clientId,
            NotImplementedError,
            "Asynchronous switch operations are not supported."));
});

// ============================================================================
// DÉMARRAGE
// ============================================================================


// ============================================================================
// DÉCOUVERTE ALPACA IPv4
// ============================================================================
//
// ASCOM Alpaca API Reference v12:
// - écoute UDP sur le port 32227 ;
// - requête exacte : "alpacadiscovery1" ;
// - réponse unicast JSON annonçant le port HTTP configuré ;
// - socket partageable avec SO_REUSEADDR sous Windows.
//
// Cette couche est indépendante du protocole Telnet et de l'état Connected.
// ============================================================================

const int AlpacaDiscoveryPort = 32227;
const string AlpacaDiscoveryMessage = "alpacadiscovery1";

var discoveryCancellation = new CancellationTokenSource();

app.Lifetime.ApplicationStopping.Register(() =>
{
    discoveryCancellation.Cancel();
});

_ = Task.Run(async () =>
{
    using var udp = new UdpClient(AddressFamily.InterNetwork);

    try
    {
        udp.Client.ExclusiveAddressUse = false;
        udp.Client.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.ReuseAddress,
            true);

        udp.Client.Bind(
            new IPEndPoint(
                IPAddress.Any,
                AlpacaDiscoveryPort));

        byte[] responseBytes =
            Encoding.ASCII.GetBytes(
                $"{{\"AlpacaPort\":{alpacaHttpPort}}}");

        while (!discoveryCancellation.IsCancellationRequested)
        {
            UdpReceiveResult received;

            try
            {
                received =
                    await udp.ReceiveAsync(
                        discoveryCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            string requestText =
                Encoding.ASCII.GetString(
                    received.Buffer);

            if (!string.Equals(
                requestText,
                AlpacaDiscoveryMessage,
                StringComparison.Ordinal))
            {
                continue;
            }

            await udp.SendAsync(
                responseBytes,
                received.RemoteEndPoint);
        }
    }
    catch (OperationCanceledException)
    {
        // Arrêt normal.
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "Alpaca IPv4 discovery service failed.");
    }
});

app.Run();