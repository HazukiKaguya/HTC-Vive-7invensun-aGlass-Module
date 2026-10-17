using System.Buffers.Binary;
using System.Numerics;

namespace AGlassVRCFT;

public readonly record struct RuntimeSample(int Eye, long Stamp, bool Valid, bool EyeValid, float X, float Y);

public static class RuntimeProtocol
{
    // zlib.crc32(packet, 0xf8e7f8e7): reflected CRC32 with complemented seed.
    public static uint Crc(ReadOnlySpan<byte> packet)
    {
        uint value = 0x07180718;
        foreach (byte b in packet)
        {
            value ^= b;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~value;
    }

    public static byte[] Request(int command, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 4080) throw new ArgumentOutOfRangeException(nameof(payload));
        byte[] packet = new byte[4096];
        BinaryPrimitives.WriteInt32LittleEndian(packet, command);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), payload.Length);
        payload.CopyTo(packet.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4092), Crc(packet.AsSpan(0, 4092)));
        return packet;
    }

    public static bool TryDecode(ReadOnlySpan<byte> packet, int mode, out RuntimeSample sample)
    {
        sample = default;
        if (packet.Length != 1024 || mode is < 1 or > 3) return false;
        int prefix = BinaryPrimitives.ReadInt32LittleEndian(packet);
        long stamp = BinaryPrimitives.ReadInt64LittleEndian(packet[4..]);
        int pupil = BinaryPrimitives.ReadInt32LittleEndian(packet[12..]);
        int gaze = BinaryPrimitives.ReadInt32LittleEndian(packet[168..]);
        if (stamp <= 0 || pupil is not (0 or 1 or 2 or 4) || gaze is < 0 or > 2) return false;
        int eye = gaze != 0 ? gaze : prefix == -1 ? 1 : prefix == -2 ? 2 : mode < 3 ? mode : 0;
        if (eye == 0 || (mode < 3 && mode != eye)) return false;
        float x = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(packet[172..]));
        float y = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(packet[176..]));
        bool eyeValid = pupil == eye || pupil == 4;
        sample = new(eye, stamp, gaze != 0 && eyeValid && float.IsFinite(x) && float.IsFinite(y), eyeValid, x, y);
        return true;
    }
}

// Independently timed per-eye state: duplicates cannot keep old data alive.
public sealed class RuntimeEyeMapper
{
    private sealed class Eye
    {
        public RuntimeSample Sample;
        public double Received, LastValid = double.NegativeInfinity;
        public double? InvalidSince;
        public bool Seen, HasGaze, HasSmooth;
        public Vector3 Ray;
        public float X, Y;
    }
    private readonly ModuleSettings settings;
    private readonly Eye[] eyes = { new(), new() };
    public int Mode { get; }
    public RuntimeEyeMapper(ModuleSettings settings, int mode) { this.settings = settings; Mode = mode; }
    public void Clear() { for (int i = 0; i < eyes.Length; i++) eyes[i] = new Eye(); }

    public bool Accept(RuntimeSample sample, double now)
    {
        if (sample.Eye is < 1 or > 2 || (Mode < 3 && sample.Eye != Mode)) return false;
        Eye e = eyes[sample.Eye - 1];
        if (e.Seen && sample.Stamp <= e.Sample.Stamp) return false;
        if (sample.Valid)
        {
            float x = Math.Clamp((sample.X - settings.CenterX) * settings.GainX * (settings.InvertX ? -1 : 1) + .5f, 0, 1);
            float y = Math.Clamp((sample.Y - settings.CenterY) * settings.GainY * (settings.InvertY ? -1 : 1) + .5f, 0, 1);
            var ray = Vector3.Normalize(new Vector3((2*x-1) * MathF.Tan(settings.FovX*MathF.PI/360),
                (1-2*y) * MathF.Tan(settings.FovY*MathF.PI/360), 1));
            double dt = now - e.Received;
            if (e.HasSmooth && dt > 0 && dt <= settings.StaleMs / 1000.0 && settings.SmoothingMs > 0)
                ray = Vector3.Normalize(Vector3.Lerp(e.Ray, ray, (float)(1-Math.Exp(-dt*1000/settings.SmoothingMs))));
            e.Ray = ray;
            e.HasGaze = e.HasSmooth = true;
            e.X = Math.Clamp(ray.X/ray.Z, -1, 1);
            e.Y = Math.Clamp(ray.Y/ray.Z, -1, 1);
            e.LastValid = now;
            e.InvalidSince = null;
        }
        else
        {
            e.HasSmooth = false;
            if (settings.EstimateBlink && !sample.EyeValid && e.HasGaze && now-e.LastValid <= settings.BlinkMaxMs/1000.0)
                e.InvalidSince ??= now;
            else e.InvalidSince = null;
        }
        e.Sample = sample;
        e.Received = now;
        e.Seen = true;
        return true;
    }

    public EyeFrame Frame(double now)
    {
        (float X, float Y, float Open)? State(Eye e)
        {
            if (!e.Seen || now < e.Received || now-e.Received > settings.StaleMs/1000.0) return null;
            if (e.Sample.Valid) return (e.X, e.Y, 1);
            if (settings.EstimateBlink && !e.Sample.EyeValid && e.InvalidSince.HasValue && now-e.LastValid <= settings.BlinkMaxMs/1000.0)
                return (e.X, e.Y, now-e.InvalidSince.Value >= settings.BlinkDelayMs/1000.0 ? 0 : 1);
            return null;
        }
        var left = State(eyes[0]);
        var right = State(eyes[1]);
        if (left is null && right is null) return EyeFrame.Neutral;
        var fallback = (left ?? right)!.Value;
        var l = left ?? (fallback.X, fallback.Y, eyes[0].HasGaze ? 1 : fallback.Open);
        var r = right ?? (fallback.X, fallback.Y, eyes[1].HasGaze ? 1 : fallback.Open);
        return new(true, l.Item1, l.Item2, l.Item3, r.Item1, r.Item2, r.Item3);
    }
}
