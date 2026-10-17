using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AGlassVRCFT;

public readonly record struct EyeFrame(bool Active, float LX, float LY, float LO, float RX, float RY, float RO)
{
    public static EyeFrame Neutral => new(false, 0, 0, 1, 0, 0, 1);
}

public interface IEyeReceiver : IDisposable
{
    EyeFrame Poll();
}

public sealed class BridgeReceiver : IEyeReceiver
{
    // Strict, single OSC message: /aglass/v1/eyes ,Tffffff (or ,Fffffff).
    // One datagram is one complete frame, so gaze and eyelids cannot mix frames.
    private static readonly byte[] Address = PaddedString("/aglass/v1/eyes");
    private static readonly byte[] TrueTags = PaddedString(",Tffffff");
    private static readonly byte[] FalseTags = PaddedString(",Fffffff");
    private readonly Socket socket;
    private readonly byte[] buffer = new byte[512];
    private readonly double timeoutSeconds;
    private long received;
    private EyeFrame latest = EyeFrame.Neutral;
    public int Port => ((IPEndPoint)socket.LocalEndPoint!).Port;
    public long AcceptedPackets { get; private set; }

    public BridgeReceiver(int port, int timeoutMs)
    {
        timeoutSeconds = timeoutMs / 1000.0;
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            socket.Blocking = false;
        }
        catch { socket.Dispose(); throw; }
    }

    private static byte[] PaddedString(string value)
    {
        byte[] result = new byte[(Encoding.ASCII.GetByteCount(value) + 4) & ~3];
        Encoding.ASCII.GetBytes(value).CopyTo(result, 0);
        return result;
    }

    public static bool TryDecode(ReadOnlySpan<byte> packet, out EyeFrame frame)
    {
        frame = EyeFrame.Neutral;
        int offset = Address.Length + TrueTags.Length;
        if (packet.Length != offset + 24 || !packet[..Address.Length].SequenceEqual(Address)) return false;
        var tags = packet.Slice(Address.Length, TrueTags.Length);
        bool active = tags.SequenceEqual(TrueTags);
        if (!active && !tags.SequenceEqual(FalseTags)) return false;
        Span<float> v = stackalloc float[6];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(packet.Slice(offset + 4 * i, 4)));
            if (!float.IsFinite(v[i])) return false;
            if (i is 2 or 5 ? v[i] < 0 || v[i] > 1 : v[i] < -1 || v[i] > 1) return false;
        }
        frame = active ? new EyeFrame(true, v[0], v[1], v[2], v[3], v[4], v[5]) : EyeFrame.Neutral;
        return true;
    }

    public EyeFrame Poll()
    {
        // Bound each update even if a sender is flooding or queued while VRCFT pauses.
        for (int i = 0; i < 64; i++)
        {
            int count;
            try { count = socket.Receive(buffer); }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock) { break; }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.MessageSize) { continue; }
            if (!TryDecode(buffer.AsSpan(0, count), out EyeFrame frame)) continue;
            latest = frame;
            received = Stopwatch.GetTimestamp();
            AcceptedPackets++;
        }
        if (received == 0 || Stopwatch.GetElapsedTime(received).TotalSeconds > timeoutSeconds)
            return EyeFrame.Neutral;
        return latest;
    }

    public void Dispose() => socket.Dispose();
}
