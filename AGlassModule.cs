using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VRCFaceTracking;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Types;

namespace AGlassVRCFT;

public sealed class ModuleSettings
{
    // Old configs without Source keep their Python bridge behaviour.
    public string Source { get; set; } = "bridge";
    public int Port { get; set; } = 9015;
    public int TimeoutMs { get; set; } = 500;
    public int RuntimePort { get; set; } = 0;
    public int EventPort { get; set; } = 0;
    public int DataPort { get; set; } = 0;
    public int CommandTimeoutMs { get; set; } = 5000;
    public int StopTimeoutMs { get; set; } = 1500;
    public int StaleMs { get; set; } = 250;
    public float FovX { get; set; } = 100;
    public float FovY { get; set; } = 100;
    public float CenterX { get; set; } = .5f;
    public float CenterY { get; set; } = .5f;
    public float GainX { get; set; } = 1;
    public float GainY { get; set; } = 1;
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }
    public int SmoothingMs { get; set; } = 25;
    public bool EstimateBlink { get; set; } = true;
    public int BlinkDelayMs { get; set; } = 30;
    public int BlinkMaxMs { get; set; } = 600;

    public void Validate()
    {
        if (Source is not ("bridge" or "runtime")) throw new InvalidDataException("Source must be runtime or bridge.");
        if (Source == "bridge")
        {
            if (Port is < 1 or > 65535 || TimeoutMs is < 50 or > 5000)
                throw new InvalidDataException("Port must be 1..65535; TimeoutMs must be 50..5000.");
            return;
        }
        if (RuntimePort is < 0 or > 65535 || EventPort is < 0 or > 65535 || DataPort is < 0 or > 65535
            || (EventPort != 0 && EventPort == DataPort)) throw new InvalidDataException("Invalid Runtime/UDP ports; 0 selects automatic allocation.");
        if (CommandTimeoutMs is < 100 or > 15000 || StopTimeoutMs is < 100 or > 5000
            || StaleMs is < 20 or > 2000 || SmoothingMs is < 0 or > 1000
            || BlinkDelayMs < 0 || BlinkMaxMs < BlinkDelayMs || BlinkMaxMs > 2000)
            throw new InvalidDataException("Invalid timeout, smoothing or blink settings.");
        if (!float.IsFinite(FovX) || !float.IsFinite(FovY) || FovX is <= 0 or >= 179 || FovY is <= 0 or >= 179
            || !float.IsFinite(CenterX) || !float.IsFinite(CenterY) || !float.IsFinite(GainX) || !float.IsFinite(GainY)
            || CenterX is < 0 or > 1 || CenterY is < 0 or > 1 || GainX is <= 0 or > 10 || GainY is <= 0 or > 10)
            throw new InvalidDataException("Invalid FOV, center or gain settings.");
    }
}

public sealed class AGlassModule : ExtTrackingModule
{
    private IEyeReceiver? receiver;
    private bool? wasTracking;
    public override (bool SupportsEye, bool SupportsExpression) Supported => (true, false);

    public override (bool eyeSuccess, bool expressionSuccess) Initialize(bool eyeAvailable, bool expressionAvailable)
    {
        ModuleInformation.Name = "HTC Vive 7invensun aGlass Module";
        if (!eyeAvailable) return (false, false);
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location)!, "aGlassVRCFT.json");
            ModuleSettings settings = File.Exists(path)
                ? JsonSerializer.Deserialize<ModuleSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? throw new InvalidDataException("Empty aGlassVRCFT.json")
                : new ModuleSettings();
            settings.Validate();
            receiver = settings.Source == "runtime"
                ? new RuntimeReceiver(settings, message => Logger?.LogWarning("{Message}", message))
                : new BridgeReceiver(settings.Port, settings.TimeoutMs);
            Apply(EyeFrame.Neutral);
            // This SDK's Combined() divides by the dilation range even when no pupil
            // diameter is available. Keep that denominator finite, without inventing
            // a pupil diameter or touching any expression / mouth shapes.
            var eye = UnifiedTracking.Data.Eye;
            if (!(eye._maxDilation > eye._minDilation))
            {
                eye._minDilation = 0;
                eye._maxDilation = 1;
            }
            if (receiver is RuntimeReceiver direct)
                Logger?.LogInformation("aGlass Runtime direct: TCP {Port}, mode {Mode}, UDP {EventPort}/{DataPort}. No Python or vendor DLL. FOV {FovX}/{FovY}; blink estimation {Blink}.", direct.RuntimePort, direct.Mode, direct.EventPort, direct.DataPort, settings.FovX, settings.FovY, settings.EstimateBlink);
            else
                Logger?.LogInformation("aGlass bridge listening on 127.0.0.1:{Port}; timeout {Timeout} ms. Start Python with --target vrcft-module.", settings.Port, settings.TimeoutMs);
            return (true, false);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or SocketException or JsonException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            receiver?.Dispose();
            receiver = null;
            Logger?.LogError(e, "Could not start aGlass bridge.");
            return (false, false);
        }
    }

    public static void Apply(EyeFrame frame)
    {
        var eye = UnifiedTracking.Data.Eye;
        eye.Left.Gaze = new Vector2(frame.LX, frame.LY);
        eye.Right.Gaze = new Vector2(frame.RX, frame.RY);
        eye.Left.Openness = frame.LO;
        eye.Right.Openness = frame.RO;
    }

    public override void Update()
    {
        try
        {
            EyeFrame frame = receiver?.Poll() ?? EyeFrame.Neutral;
            // Respect VRCFT's manual module toggle. Keep draining UDP while paused.
            if (Status == ModuleState.Active)
            {
                Apply(frame);
                if (frame.Active != wasTracking)
                {
                    Logger?.LogInformation(frame.Active ? "aGlass eye data received." : "aGlass has no fresh tracking data; gaze centered and eyelids opened.");
                    wasTracking = frame.Active;
                }
            }
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
        {
            if (Status == ModuleState.Active) Apply(EyeFrame.Neutral);
            Logger?.LogError(e, "aGlass receiver stopped. Check Runtime, then reload the module to reconnect.");
            receiver?.Dispose();
            receiver = null;
        }
        Thread.Sleep(5);
    }

    public override void Teardown()
    {
        if (receiver is not null) Apply(EyeFrame.Neutral);
        receiver?.Dispose();
        receiver = null;
        wasTracking = null;
    }
}
