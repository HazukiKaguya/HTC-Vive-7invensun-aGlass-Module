"""Independent OSC encoder and timed gaze/blink source, using only stdlib."""
import socket
import struct
import sys
import time


def osc_string(value):
    encoded = value.encode('ascii') + b'\0'
    return encoded + b'\0' * (-len(encoded) % 4)


def packet(active=True, values=(.5, -.25, 0., -.5, .25, 1.), address='/aglass/v1/eyes'):
    return osc_string(address) + osc_string(',Tffffff' if active else ',Fffffff') + struct.pack('>6f', *values)


if __name__ == '__main__':
    port = int(sys.argv[1])
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
        # Hold every stage for many Update() calls, including on slower CI hosts.
        for openness in (1., 0., 1.):
            deadline = time.monotonic() + .4
            while time.monotonic() < deadline:
                sender.sendto(packet(values=(.4, -.2, openness, -.3, .2, openness)), ('127.0.0.1', port))
                time.sleep(.01)
