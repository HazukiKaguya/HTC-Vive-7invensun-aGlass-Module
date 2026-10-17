using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32;

namespace AGlassVRCFT;

public sealed class RuntimeReceiver : IEyeReceiver
{
    private readonly ModuleSettings settings;
    private readonly Action<string>? warning;
    private readonly Stack<int> cleanup = new();
    private readonly object gate = new();
    private Socket? tcp, events, data;
    private RuntimeEyeMapper? mapper;
    private bool synchronized = true, disposed;
    private readonly byte[] udpBuffer = new byte[2048];
    public int RuntimePort { get; }
    public int EventPort => ((IPEndPoint)events!.LocalEndPoint!).Port;
    public int DataPort => ((IPEndPoint)data!.LocalEndPoint!).Port;
    public int Mode { get; private set; }
    public long ReceivedPackets { get; private set; }
    public long RejectedPackets { get; private set; }
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public RuntimeReceiver(ModuleSettings settings, Action<string>? warning = null)
    {
        settings.Validate();
        this.settings = settings;
        this.warning = warning;
        RuntimePort = ResolvePort(settings.RuntimePort);
        try
        {
            tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            tcp.ConnectAsync(IPAddress.Loopback, RuntimePort).WaitAsync(TimeSpan.FromMilliseconds(settings.CommandTimeoutMs)).GetAwaiter().GetResult();
            tcp.Blocking = false;
            tcp.NoDelay = true;
            events = BindUdp(settings.EventPort);
            data = BindUdp(settings.DataPort);
            byte[] ports = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(ports, EventPort);
            BinaryPrimitives.WriteInt32LittleEndian(ports.AsSpan(4), DataPort);
            byte[] result = Command(7001, ports, Now + settings.CommandTimeoutMs/1000.0);
            cleanup.Push(7002);
            Mode = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(4));
            if (Mode is < 1 or > 3) throw new InvalidDataException($"Unsupported Runtime eye mode {Mode}.");
            mapper = new RuntimeEyeMapper(settings, Mode);
            Command(7003, Array.Empty<byte>(), Now + settings.CommandTimeoutMs/1000.0);
            cleanup.Push(7004);
            // Verified Runtime 1.1 client does not send 7005/7006.
            Command(7007, Array.Empty<byte>(), Now + settings.CommandTimeoutMs/1000.0);
            cleanup.Push(7008);
        }
        catch { Dispose(); throw; }
    }

    public static int ResolvePort(int port)
    {
        if (port is >= 1 and <= 65535) return port;
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("RuntimePort must be specified outside Windows.");
        var processes = Process.GetProcessesByName("aGlassRuntime");
        try
        {
            if (processes.Length != 1) throw new InvalidOperationException("Start one aGlassRuntime.exe, or explicitly set RuntimePort. The module never starts Runtime.");
        }
        finally { foreach (var process in processes) process.Dispose(); }
        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\aGlassRuntime\Service");
        string? value = key?.GetValue("ListenPort")?.ToString()?.Split('\0')[0].Trim();
        if (!int.TryParse(value, out port) || port is < 1 or > 65535)
            throw new InvalidOperationException("Cannot read Runtime ListenPort. Set RuntimePort in aGlassVRCFT.json.");
        return port;
    }

    private static Socket BindUdp(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            socket.Blocking = false;
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    private void Wait(SelectMode mode, double deadline)
    {
        while (true)
        {
            double remaining = deadline-Now;
            if (remaining <= 0) throw new TimeoutException("aGlass Runtime command timed out.");
            if (tcp!.Poll((int)(Math.Min(.05, remaining)*1000000), mode)) return;
        }
    }

    private byte[] Command(int code, byte[] payload, double deadline)
    {
        byte[] request = RuntimeProtocol.Request(code, payload), response = new byte[4096];
        synchronized = false;
        for (int offset = 0; offset < request.Length;)
        {
            Wait(SelectMode.SelectWrite, deadline);
            int count = tcp!.Send(request, offset, request.Length-offset, SocketFlags.None);
            if (count == 0) throw new IOException("Runtime closed the control connection.");
            offset += count;
        }
        for (int offset = 0; offset < response.Length;)
        {
            Wait(SelectMode.SelectRead, deadline);
            int count = tcp!.Receive(response, offset, response.Length-offset, SocketFlags.None);
            if (count == 0) throw new IOException("Runtime closed the control connection.");
            offset += count;
        }
        int returned = BinaryPrimitives.ReadInt32LittleEndian(response);
        int length = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(4));
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(4092));
        if (returned != code || length < (code == 7001 ? 16 : 4) || length > 4080 || crc != RuntimeProtocol.Crc(response.AsSpan(0, 4092)))
            throw new InvalidDataException("Invalid Runtime command/length/CRC; only verified Runtime 1.1 is supported.");
        // The installed Runtime's unused extraLen field is uninitialized.
        synchronized = true;
        int result = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(12));
        if (result != 0) throw new IOException($"Runtime command {code} returned {result}. Check devices, authorization and calibration.");
        return response.AsSpan(12, length).ToArray();
    }

    public EyeFrame Poll()
    {
        lock (gate)
        {
            if (disposed) return EyeFrame.Neutral;
            if (tcp!.Poll(0, SelectMode.SelectRead))
            {
                synchronized = false;
                int count = tcp.Receive(udpBuffer, 0, 1, SocketFlags.Peek);
                throw new IOException(count == 0 ? "Runtime disconnected. Reload module to reconnect." : "Unexpected Runtime control data.");
            }
            Drain(events!, true);
            Drain(data!, false);
            return mapper!.Frame(Now);
        }
    }

    private void Drain(Socket socket, bool isEvent)
    {
        for (int i = 0; i < 256; i++)
        {
            EndPoint source = new IPEndPoint(IPAddress.Any, 0);
            int count;
            try { count = socket.ReceiveFrom(udpBuffer, ref source); }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock) { break; }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.MessageSize) { RejectedPackets++; continue; }
            if (!((IPEndPoint)source).Address.Equals(IPAddress.Loopback)) continue;
            var packet = udpBuffer.AsSpan(0, count);
            if (isEvent)
            {
                if (count == 1024 && BinaryPrimitives.ReadInt32LittleEndian(packet) == 8000
                    && BinaryPrimitives.ReadInt32LittleEndian(packet[4..]) is 0 or 2) mapper!.Clear();
                continue;
            }
            if (!RuntimeProtocol.TryDecode(packet, Mode, out var sample))
            {
                if (++RejectedPackets == 1) warning?.Invoke("Ignoring data that does not match Runtime 1.1's eye packet format.");
                continue;
            }
            ReceivedPackets++;
            mapper!.Accept(sample, Now);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            double deadline = Now + settings.StopTimeoutMs/1000.0;
            while (cleanup.Count > 0 && tcp is not null && synchronized && Now < deadline)
            {
                int code = cleanup.Pop();
                try { Command(code, Array.Empty<byte>(), deadline); }
                catch (Exception e) when (e is IOException or InvalidDataException or SocketException or TimeoutException)
                { warning?.Invoke($"Runtime session cleanup: {e.Message}"); }
            }
            cleanup.Clear();
            events?.Dispose(); data?.Dispose(); tcp?.Dispose();
            events = data = tcp = null;
            mapper?.Clear();
        }
    }
}
