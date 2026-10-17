using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AGlassVRCFT;
using VRCFaceTracking;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Params.Expressions;

if (args.Length == 0 || (args[0] == "--runtime-probe" ? args.Length != 4 : args.Length != 3))
{
    Console.Error.WriteLine("Run python tools/build.py for the automated suite. Hardware probe (opt-in): --runtime-probe PORT SECONDS REPORT.json");
    Environment.ExitCode = 2;
    return;
}

if (args[0] == "--runtime-probe")
{
    // Explicit opt-in hardware probe: samples only, never starts OSC or VRCFT.
    var settings = new ModuleSettings { Source = "runtime", RuntimePort = int.Parse(args[1]), CommandTimeoutMs = 3000 };
    var warnings = new List<string>();
    using var direct = new RuntimeReceiver(settings, warnings.Add);
    var probeTimer = Stopwatch.StartNew();
    int activeFrames = 0;
    EyeFrame frame = EyeFrame.Neutral;
    while (probeTimer.Elapsed.TotalSeconds < double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture))
    {
        frame = direct.Poll();
        if (frame.Active) activeFrames++;
        Thread.Sleep(5);
    }
    direct.Dispose();
    string report = JsonSerializer.Serialize(new { direct.Mode, direct.ReceivedPackets, direct.RejectedPackets, activeFrames, frame, warnings });
    File.WriteAllText(args[3], report);
    Console.WriteLine(report);
    return;
}

if (args[0] == "--runtime-tests")
{
    RuntimeTests.Run(args[1], args[2]);
    return;
}

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}

string fixtures = args[0];
byte[] activePacket = File.ReadAllBytes(Path.Combine(fixtures, "active.osc"));
Check(BridgeReceiver.TryDecode(activePacket, out var active), "Python OSC packet must decode");
Check(active == new EyeFrame(true, .5f, -.25f, 0, -.5f, .25f, 1), "Float endian, eyes and signs");
Check(BridgeReceiver.TryDecode(File.ReadAllBytes(Path.Combine(fixtures, "inactive.osc")), out var inactive)
      && inactive == EyeFrame.Neutral, "OSC false must neutralize");
foreach (string file in Directory.GetFiles(fixtures, "invalid-*.osc"))
    Check(!BridgeReceiver.TryDecode(File.ReadAllBytes(file), out _), "Reject " + Path.GetFileName(file));

Check(typeof(AGlassModule).BaseType == typeof(ExtTrackingModule), "Real VRCFT SDK base type identity");
using var receiver = new BridgeReceiver(0, 80);
using var sender = new UdpClient();
sender.Send(activePacket, activePacket.Length, new IPEndPoint(IPAddress.Loopback, receiver.Port));
Thread.Sleep(15);
Check(receiver.Poll() == active, "Real UDP decoder");
Thread.Sleep(100);
sender.Send(new byte[] { 0, 1, 2 }, 3, new IPEndPoint(IPAddress.Loopback, receiver.Port));
Check(receiver.Poll() == EyeFrame.Neutral, "Malformed packets cannot refresh timeout");
Check(receiver.AcceptedPackets == 1, "Only complete valid packets accepted");

// Exercise the actual module lifecycle against the real Core, in an isolated test directory.
using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
reservation.Close();
string config = Path.Combine(AppContext.BaseDirectory, "aGlassVRCFT.json");
File.WriteAllText(config, JsonSerializer.Serialize(new ModuleSettings { Port = port, TimeoutMs = 80 }));
var module = new AGlassModule();
Check(module.Initialize(false, true) == (false, false), "Do not take an occupied eye slot");
Check(module.Initialize(true, true) == (true, false), "Claim eye tracking only");
Check(float.IsFinite(UnifiedTracking.Data.Eye.Combined().PupilDiameter_MM), "Unmeasured pupil normalization must not produce NaN");
var conflict = new AGlassModule();
Check(conflict.Initialize(true, false) == (false, false), "Bind conflict fails gracefully");
UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.JawOpen].Weight = .73f;
UnifiedTracking.Data.Eye.Left.PupilDiameter_MM = 4.2f;
module.Status = ModuleState.Active;
sender.Send(activePacket, activePacket.Length, new IPEndPoint(IPAddress.Loopback, port));
Thread.Sleep(15);
module.Update();
Check(UnifiedTracking.Data.Eye.Left.Gaze.x == .5f && UnifiedTracking.Data.Eye.Right.Gaze.x == -.5f, "SDK gaze mapping");
Check(UnifiedTracking.Data.Eye.Left.Openness == 0 && UnifiedTracking.Data.Eye.Right.Openness == 1, "SDK per-eye lids");
Check(UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.JawOpen].Weight == .73f, "Other face module data preserved");
Check(UnifiedTracking.Data.Eye.Left.PupilDiameter_MM == 4.2f, "No fabricated pupil values");
Thread.Sleep(100);
module.Update();
Check(UnifiedTracking.Data.Eye.Left.Openness == 1 && UnifiedTracking.Data.Eye.Left.Gaze.x == 0, "Timeout neutralizes SDK state");
module.Status = ModuleState.Idle;
sender.Send(activePacket, activePacket.Length, new IPEndPoint(IPAddress.Loopback, port));
Thread.Sleep(15);
module.Update();
Check(UnifiedTracking.Data.Eye.Left.Openness == 1, "Manual pause prevents data writes");
module.Teardown();
Check(module.Initialize(true, false) == (true, false), "Clean teardown permits rebind");
module.Status = ModuleState.Active;

// Independent standard-library OSC simulator -> UDP -> actual VRCFT fields.
var start = new ProcessStartInfo(args[1]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (string arg in new[] { args[2], port.ToString() })
    start.ArgumentList.Add(arg);
using var python = Process.Start(start)!;
Task<string> stdout = python.StandardOutput.ReadToEndAsync(), stderr = python.StandardError.ReadToEndAsync();
bool moved = false, blinked = false, reopened = false;
var timer = Stopwatch.StartNew();
try
{
    while (!python.HasExited && timer.Elapsed.TotalSeconds < 10)
    {
        module.Update();
        moved |= Math.Abs(UnifiedTracking.Data.Eye.Left.Gaze.x) > .1;
        blinked |= UnifiedTracking.Data.Eye.Left.Openness == 0 && UnifiedTracking.Data.Eye.Right.Openness == 0;
        reopened |= blinked && UnifiedTracking.Data.Eye.Left.Openness == 1;
    }
    Check(python.HasExited, "Python simulation must finish");
    Check(python.ExitCode == 0, "Python failed: " + await stderr);
    Check(moved && blinked && reopened, "Python gaze/blink/reopen must reach real VRCFT data");
    Thread.Sleep(120);
    module.Update();
    Check(UnifiedTracking.Data.Eye.Left.Gaze.x == 0 && UnifiedTracking.Data.Eye.Left.Openness == 1, "Python exit returns neutral");
}
finally
{
    if (!python.HasExited) python.Kill();
    module.Teardown();
}
File.WriteAllText(config, "{\"Port\":0}");
Check(new AGlassModule().Initialize(true, false) == (false, false), "Invalid settings fail gracefully");
File.Delete(config);
Console.WriteLine($"PASS: {checks} checks; real VRCFT Core {typeof(ExtTrackingModule).Assembly.GetName().Version}; Python -> UDP -> module -> UnifiedTracking.Data.");
