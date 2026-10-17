using System.Diagnostics;
using System.Text.Json;
using AGlassVRCFT;
using VRCFaceTracking;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Params.Expressions;

public static class RuntimeTests
{
    public static void Run(string fixtures, string scenariosFile)
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        byte[] Packet(string name) => File.ReadAllBytes(Path.Combine(fixtures, name));
        Check(RuntimeProtocol.Request(7001, new byte[] { 1, 2, 0, 0, 3, 4, 0, 0 })
            .SequenceEqual(Packet("runtime-request.bin")), "Control framing and CRC match Python zlib exactly");
        RuntimeSample Decode(string name)
        {
            Check(RuntimeProtocol.TryDecode(Packet(name), 3, out var sample), "Decode " + name);
            return sample;
        }
        var left = Decode("runtime-left.bin");
        var right = Decode("runtime-right.bin");
        var invalidLeft = Decode("runtime-invalid-left.bin");
        var invalidRight = Decode("runtime-invalid-right.bin");
        var visibleInvalid = Decode("runtime-visible-invalid.bin");
        var nan = Decode("runtime-nan.bin");
        Check(left.Eye == 1 && right.Eye == 2 && left.Valid && right.Valid, "Correct eye IDs and shared coordinate offsets");
        Check(!nan.Valid && nan.EyeValid, "Nonfinite gaze must not become a blink");
        Check(!RuntimeProtocol.TryDecode(Packet("runtime-right.bin"), 1, out _), "Single-eye mode rejects the other eye");
        Check(!RuntimeProtocol.TryDecode(new byte[1000], 3, out _), "Reject malformed UDP size");
        var settings = new ModuleSettings { Source = "runtime", FovX = 90, FovY = 90, SmoothingMs = 0 };
        var mapper = new RuntimeEyeMapper(settings, 3);
        mapper.Accept(left, 1);
        mapper.Accept(right, 1);
        EyeFrame frame = mapper.Frame(1);
        Check(frame.Active && Math.Abs(frame.LX+.5f)<.0001 && Math.Abs(frame.LY+.5f)<.0001
              && Math.Abs(frame.RX-.6f)<.0001 && Math.Abs(frame.RY-.4f)<.0001, "FOV mapping matches Python slopes and Y sign");
        Check(!mapper.Accept(left, 1.2) && !mapper.Frame(1.3).Active, "Duplicate timestamps do not extend freshness");
        Check(!mapper.Accept(left with { Stamp = 1 }, 1.3), "Reject backwards stamps");
        mapper.Clear();
        Check(!mapper.Frame(2).Active, "Disconnect events clear gaze/blink history");
        mapper.Accept(invalidLeft, 2);
        Check(!mapper.Frame(2.05).Active, "Never tracked invalid eyes are not closed eyes");
        mapper.Clear();
        mapper.Accept(left, 3);
        mapper.Accept(right, 3);
        mapper.Accept(invalidLeft, 3.01);
        Check(mapper.Frame(3.02).LO == 1 && mapper.Frame(3.06).LO == 0 && mapper.Frame(3.06).RO == 1, "Per-eye blink debounce");
        mapper.Accept(invalidRight, 3.07);
        Check(mapper.Frame(3.11).LO == 0 && mapper.Frame(3.11).RO == 0, "Independent bilateral blinking");
        mapper.Accept(invalidLeft with { Stamp = 40 }, 3.65);
        mapper.Accept(invalidRight with { Stamp = 40 }, 3.65);
        Check(mapper.Frame(3.66) == EyeFrame.Neutral, "Long tracking loss restores neutral open eyes");
        mapper.Clear();
        mapper.Accept(left, 4);
        mapper.Accept(right, 4);
        mapper.Accept(visibleInvalid, 4.01);
        Check(mapper.Frame(4.1).LO == 1, "Visible eye with invalid gaze is not a blink");
        mapper.Accept(left with { Stamp = 50 }, 4.3);
        Check(mapper.Frame(4.31).RO == 1 && mapper.Frame(4.31).RX == mapper.Frame(4.31).LX, "Stale previously tracked eye borrows gaze but stays open");
        var single = new RuntimeEyeMapper(settings, 1);
        single.Accept(left, 5);
        single.Accept(invalidLeft, 5.01);
        Check(single.Frame(5.06).LO == 0 && single.Frame(5.06).RO == 0, "Single-eye hardware drives both eyes");
        settings.EstimateBlink = false;
        Check(single.Frame(5.06) == EyeFrame.Neutral, "Blink estimation can be disabled");
        settings.EstimateBlink = true;

        var scenarios = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(scenariosFile))!;
        settings.RuntimePort = scenarios["normal"];
        settings.CommandTimeoutMs = 400;
        settings.StopTimeoutMs = 200;
        string config = Path.Combine(AppContext.BaseDirectory, "aGlassVRCFT.json");
        File.WriteAllText(config, JsonSerializer.Serialize(settings));
        var module = new AGlassModule();
        try
        {
            UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.JawOpen].Weight = .73f;
            Check(module.Initialize(true, true) == (true, false), "Runtime -> module initialization claims only eyes");
            module.Status = ModuleState.Active;
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 400)
            {
                module.Update();
                if (UnifiedTracking.Data.Eye.Left.Gaze.x < 0 && UnifiedTracking.Data.Eye.Right.Gaze.x > 0) break;
            }
            Check(UnifiedTracking.Data.Eye.Left.Gaze.x < 0 && UnifiedTracking.Data.Eye.Right.Gaze.x > 0, "Runtime TCP/UDP -> actual VRCFT eye fields without Python bridge");
            Check(UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.JawOpen].Weight == .73f, "Direct mode preserves mouth data");
            Thread.Sleep(270);
            module.Update();
            Check(UnifiedTracking.Data.Eye.Left.Gaze.x == 0 && UnifiedTracking.Data.Eye.Left.Openness == 1, "Direct stream timeout restores neutral");
        }
        finally { module.Teardown(); File.Delete(config); }
        foreach (string scenario in new[] { "failure", "corrupt", "unknown_mode", "hang" })
        {
            settings.RuntimePort = scenarios[scenario];
            settings.CommandTimeoutMs = 150;
            var timer = Stopwatch.StartNew();
            bool failed = false;
            try { using var connection = new RuntimeReceiver(settings); }
            catch (Exception e) when (e is IOException or InvalidDataException or TimeoutException) { failed = true; }
            Check(failed && timer.Elapsed.TotalSeconds < 1.5, "Bounded failure and cleanup: " + scenario);
        }
        settings.RuntimePort = scenarios["cleanup_failure"];
        using (var connection = new RuntimeReceiver(settings)) { Check(connection.Mode == 3, "Cleanup error fixture started"); }
        Console.WriteLine($"PASS: {checks} direct Runtime checks; independent Python TCP/UDP simulator -> C# module -> real VRCFT Core.");
    }
}
