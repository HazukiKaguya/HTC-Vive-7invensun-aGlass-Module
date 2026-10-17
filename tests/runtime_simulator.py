"""Independent loopback Runtime 1.1 simulator. Never accesses hardware."""
import socket
import struct
import threading
import time
import zlib


def gaze(eye, stamp, valid=True, pupil=True, x=.25, y=.75):
    packet = bytearray(1024)
    struct.pack_into("<iqi", packet, 0, eye if valid else -eye, stamp, eye if pupil else 0)
    struct.pack_into("<iff", packet, 168, eye if valid else 0, x, y)
    # Installed Runtime leaves the UDP CRC trailer zero.
    return packet


class Server:
    def __init__(self, mode=3, fail=None, corrupt=False, hang=False):
        self.mode, self.fail, self.corrupt, self.hang = mode, fail, corrupt, hang
        self.commands, self.errors = [], []
        self.stop = threading.Event()
        self.listening = socket.socket()
        self.listening.bind(("127.0.0.1", 0))
        self.listening.listen()
        self.listening.settimeout(15)
        self.port = self.listening.getsockname()[1]
        self.udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.connection = None
        self.thread = threading.Thread(target=self.serve, daemon=True)
        self.thread.start()

    def serve(self):
        try:
            self.connection, _ = self.listening.accept()
            with self.connection as connection:
                connection.settimeout(2)
                while True:
                    packet = bytearray()
                    while len(packet) < 4096:
                        part = connection.recv(4096 - len(packet))
                        if not part:
                            return
                        packet.extend(part)
                    code, size, extra = struct.unpack_from("<iii", packet)
                    assert extra == 0 and size == (8 if code == 7001 else 0)
                    assert struct.unpack_from("<I", packet, 4092)[0] == zlib.crc32(packet[:4092], 0xf8e7f8e7)
                    self.commands.append(code)
                    if code == 7001:
                        self.event_port, self.data_port = struct.unpack_from("<ii", packet, 12)
                    if self.hang:
                        self.stop.wait(2)
                        return
                    response = bytearray(4096)
                    struct.pack_into("<iii", response, 0, code, 16 if code == 7001 else 4, -123456)
                    struct.pack_into("<i", response, 12, -101 if code == self.fail else 0)
                    if code == 7001:
                        struct.pack_into("<iff", response, 16, self.mode, 100., 100.)
                    crc = zlib.crc32(response[:4092], 0xf8e7f8e7)
                    struct.pack_into("<I", response, 4092, crc ^ int(self.corrupt))
                    # Force response fragmentation instead of relying on recv(4096).
                    connection.sendall(response[:17])
                    time.sleep(.002)
                    connection.sendall(response[17:])
                    if code == 7007 and self.fail != code:
                        self.send(gaze(1, 10))
                        self.send(gaze(2, 10, x=.8, y=.3))
        except (ConnectionResetError, BrokenPipeError):
            pass
        except Exception as exc:
            if not self.stop.is_set():
                self.errors.append(repr(exc))

    def send(self, packet, event=False):
        self.udp.sendto(packet, ("127.0.0.1", self.event_port if event else self.data_port))

    def close(self):
        self.stop.set()
        self.listening.close()
        if self.connection is not None:
            self.connection.close()
        self.thread.join(2.5)
        self.udp.close()
        assert not self.thread.is_alive()
        assert not self.errors, self.errors

