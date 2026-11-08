using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VSDirector.Data;

namespace VSDirector.Recording;

internal sealed class DirectorObsWebSocketClient : IDisposable
{
    private readonly Uri endpoint;
    private readonly string password;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, bool> desktopInputMuteStates =
        new(StringComparer.OrdinalIgnoreCase);
    private ClientWebSocket? socket;
    private bool disposed;

    public DirectorObsWebSocketClient(string url, string password)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException("OBS WebSocket URL must use ws:// or wss://.", nameof(url));
        }

        endpoint = parsed;
        this.password = password ?? string.Empty;
    }

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectedCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task StartRecordingAsync(CancellationToken cancellationToken = default)
        => SendRequestLockedAsync("StartRecord", cancellationToken);

    public async Task SuspendDesktopAudioAsync(CancellationToken cancellationToken = default)
        => await SuspendOtherAudioInputsAsync(null, cancellationToken).ConfigureAwait(false);

    public async Task SuspendOtherAudioInputsAsync(
        string? excludedInputName,
        CancellationToken cancellationToken = default)
    {
        if (desktopInputMuteStates.Count > 0)
        {
            return;
        }

        JsonElement response = await SendRequestLockedWithDataAsync(
            "GetInputList",
            cancellationToken).ConfigureAwait(false);
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("inputs", out JsonElement inputs)
            || inputs.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement input in inputs.EnumerateArray())
        {
            if (!input.TryGetProperty("inputName", out JsonElement nameElement)
                || nameElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                continue;
            }

            string inputName = nameElement.GetString()!;
            if (string.Equals(inputName, excludedInputName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                JsonElement muteResponse = await SendRequestLockedWithDataAsync(
                    "GetInputMute",
                    new { inputName },
                    cancellationToken).ConfigureAwait(false);
                bool wasMuted = muteResponse.ValueKind == JsonValueKind.Object
                    && muteResponse.TryGetProperty("inputMuted", out JsonElement muted)
                    && muted.GetBoolean();
                desktopInputMuteStates[inputName] = wasMuted;
                if (!wasMuted)
                {
                    _ = await SendRequestLockedWithDataAsync(
                        "SetInputMute",
                        new { inputName, inputMuted = true },
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // Some OBS sources do not expose an audio mute property. They
                // must not prevent the dedicated VS Director source from recording.
            }
        }
    }

    public async Task RestoreDesktopAudioAsync(CancellationToken cancellationToken = default)
    {
        foreach ((string inputName, bool wasMuted) in desktopInputMuteStates.ToArray())
        {
            _ = await SendRequestLockedWithDataAsync(
                "SetInputMute",
                new { inputName, inputMuted = wasMuted },
                cancellationToken).ConfigureAwait(false);
        }
        desktopInputMuteStates.Clear();
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string?> FindInputByKindAsync(
        string inputKind,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputKind))
        {
            throw new ArgumentException("OBS input kind is required.", nameof(inputKind));
        }

        JsonElement response = await SendRequestLockedWithDataAsync(
            "GetInputList",
            new { inputKind = inputKind.Trim() },
            cancellationToken).ConfigureAwait(false);
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("inputs", out JsonElement inputs)
            || inputs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement input in inputs.EnumerateArray())
        {
            if (input.TryGetProperty("inputKind", out JsonElement kind)
                && string.Equals(kind.GetString(), inputKind, StringComparison.Ordinal)
                && input.TryGetProperty("inputName", out JsonElement name))
            {
                return name.GetString();
            }
        }
        return null;
    }

    public async Task<bool> IsInputKindAvailableAsync(
        string inputKind,
        CancellationToken cancellationToken = default)
    {
        JsonElement response = await SendRequestLockedWithDataAsync(
            "GetInputKindList",
            new { unversioned = false },
            cancellationToken).ConfigureAwait(false);
        return response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("inputKinds", out JsonElement kinds)
            && kinds.ValueKind == JsonValueKind.Array
            && kinds.EnumerateArray().Any(kind => string.Equals(
                kind.GetString(),
                inputKind,
                StringComparison.Ordinal));
    }

    public async Task<string> EnsureInputInProgramSceneAsync(
        string inputKind,
        string preferredName,
        CancellationToken cancellationToken = default)
    {
        string? existing = await FindInputByKindAsync(inputKind, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        JsonElement sceneResponse = await SendRequestLockedWithDataAsync(
            "GetCurrentProgramScene",
            cancellationToken).ConfigureAwait(false);
        string sceneName = sceneResponse.ValueKind == JsonValueKind.Object
            && sceneResponse.TryGetProperty("currentProgramSceneName", out JsonElement scene)
                ? scene.GetString() ?? string.Empty
                : string.Empty;
        if (sceneName.Length == 0)
        {
            throw new InvalidOperationException("OBS did not return a current Program scene.");
        }

        JsonElement inputsResponse = await SendRequestLockedWithDataAsync(
            "GetInputList",
            cancellationToken).ConfigureAwait(false);
        HashSet<string> inputNames = inputsResponse.ValueKind == JsonValueKind.Object
            && inputsResponse.TryGetProperty("inputs", out JsonElement inputs)
            && inputs.ValueKind == JsonValueKind.Array
                ? inputs.EnumerateArray()
                    .Select(input => input.TryGetProperty("inputName", out JsonElement name)
                        ? name.GetString() ?? string.Empty
                        : string.Empty)
                    .Where(name => name.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string baseName = string.IsNullOrWhiteSpace(preferredName)
            ? "VS Director Offscreen"
            : preferredName.Trim();
        string inputName = baseName;
        for (int suffix = 2; inputNames.Contains(inputName); suffix++)
        {
            inputName = baseName + " " + suffix.ToString(CultureInfo.InvariantCulture);
        }

        _ = await SendRequestLockedWithDataAsync(
            "CreateInput",
            new
            {
                sceneName,
                inputName,
                inputKind,
                inputSettings = new { },
                sceneItemEnabled = true
            },
            cancellationToken).ConfigureAwait(false);
        return inputName;
    }

    public async Task<string> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        JsonElement response = await SendRequestLockedWithDataAsync("StopRecord", cancellationToken)
            .ConfigureAwait(false);
        return response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("outputPath", out JsonElement outputPath)
            ? outputPath.GetString() ?? string.Empty
            : string.Empty;
    }

    private async Task SendRequestLockedAsync(string requestType, CancellationToken cancellationToken)
    {
        _ = await SendRequestLockedWithDataAsync(requestType, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> SendRequestLockedWithDataAsync(
        string requestType,
        CancellationToken cancellationToken)
        => await SendRequestLockedWithDataAsync(
            requestType,
            new { },
            cancellationToken).ConfigureAwait(false);

    private async Task<JsonElement> SendRequestLockedWithDataAsync(
        string requestType,
        object requestData,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectedCoreAsync(cancellationToken).ConfigureAwait(false);
            ClientWebSocket activeSocket = socket!;
            string requestId = Guid.NewGuid().ToString("N");
            await SendJsonAsync(activeSocket, new
            {
                op = 6,
                d = new
                {
                    requestType,
                    requestId,
                    requestData
                }
            }, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                using JsonDocument message = await ReceiveJsonAsync(activeSocket, cancellationToken)
                    .ConfigureAwait(false);
                JsonElement root = message.RootElement;
                if (!root.TryGetProperty("op", out JsonElement op) || op.GetInt32() != 7
                    || !root.TryGetProperty("d", out JsonElement data)
                    || !data.TryGetProperty("requestId", out JsonElement responseId)
                    || !string.Equals(responseId.GetString(), requestId, StringComparison.Ordinal))
                {
                    continue;
                }

                JsonElement status = data.GetProperty("requestStatus");
                if (!status.GetProperty("result").GetBoolean())
                {
                    int code = status.TryGetProperty("code", out JsonElement codeElement)
                        ? codeElement.GetInt32()
                        : 0;
                    string comment = status.TryGetProperty("comment", out JsonElement commentElement)
                        ? commentElement.GetString() ?? string.Empty
                        : string.Empty;
                    throw new InvalidOperationException($"OBS request {requestType} failed ({code}): {comment}");
                }

                return data.TryGetProperty("responseData", out JsonElement responseData)
                    ? responseData.Clone()
                    : default;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EnsureConnectedCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (socket is { State: WebSocketState.Open })
        {
            return;
        }

        socket?.Dispose();
        ClientWebSocket created = new();
        created.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await created.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            using JsonDocument hello = await ReceiveJsonAsync(created, cancellationToken).ConfigureAwait(false);
            JsonElement root = hello.RootElement;
            if (!root.TryGetProperty("op", out JsonElement helloOp) || helloOp.GetInt32() != 0)
            {
                throw new InvalidDataException("OBS WebSocket did not send a Hello message.");
            }

            string? authentication = null;
            JsonElement helloData = root.GetProperty("d");
            if (helloData.TryGetProperty("authentication", out JsonElement authData))
            {
                string challenge = authData.GetProperty("challenge").GetString() ?? string.Empty;
                string salt = authData.GetProperty("salt").GetString() ?? string.Empty;
                authentication = DirectorObsAuthentication.Compute(password, salt, challenge);
            }

            await SendJsonAsync(created, authentication is null
                ? new { op = 1, d = new { rpcVersion = 1, eventSubscriptions = 0 } }
                : new { op = 1, d = new { rpcVersion = 1, eventSubscriptions = 0, authentication } }, cancellationToken)
                .ConfigureAwait(false);

            while (true)
            {
                using JsonDocument identified = await ReceiveJsonAsync(created, cancellationToken)
                    .ConfigureAwait(false);
                if (identified.RootElement.TryGetProperty("op", out JsonElement identifiedOp)
                    && identifiedOp.GetInt32() == 2)
                {
                    break;
                }
            }

            socket = created;
        }
        catch
        {
            created.Abort();
            created.Dispose();
            throw;
        }
    }

    private async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        ClientWebSocket? active = socket;
        socket = null;
        if (active is null)
        {
            return;
        }

        try
        {
            if (active.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await active.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "VS Director command complete",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            active.Abort();
        }
        finally
        {
            active.Dispose();
        }
    }

    private static async Task SendJsonAsync(
        ClientWebSocket activeSocket,
        object value,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        await activeSocket.SendAsync(
            new ArraySegment<byte>(payload),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(
        ClientWebSocket activeSocket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        using MemoryStream payload = new();
        WebSocketReceiveResult result;
        do
        {
            result = await activeSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new IOException("OBS WebSocket closed the connection.");
            }

            payload.Write(buffer, 0, result.Count);
            if (payload.Length > 4 * 1024 * 1024)
            {
                throw new InvalidDataException("OBS WebSocket message exceeded 4 MiB.");
            }
        }
        while (!result.EndOfMessage);

        return JsonDocument.Parse(payload.ToArray());
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(750));
            DisconnectCoreAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch
        {
            socket?.Abort();
            socket?.Dispose();
            socket = null;
        }
    }
}
