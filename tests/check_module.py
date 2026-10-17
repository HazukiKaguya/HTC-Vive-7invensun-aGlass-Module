"""Independent protocol/lifecycle regression suite; Python standard library + .NET 8."""
from pathlib import Path
import json
import struct
import subprocess
import sys
import zlib

from osc_simulator import packet


def main():
    root = Path(__file__).resolve().parents[1]
    fixtures = root / ".build" / "fixtures"
    fixtures.mkdir(parents=True, exist_ok=True)
    good = packet()
    cases = {"active": good, "inactive": packet(False), "invalid-truncated": good[:-1],
             "invalid-trailing": good + b"\0", "invalid-address": packet(address="/aglass/v2/eyes"),
             "invalid-tags": good.replace(b",Tffffff", b",fffffff"),
             "invalid-nan": good[:-4] + struct.pack(">f", float("nan")),
             "invalid-infinity": good[:-4] + struct.pack(">f", float("inf")),
             "invalid-lid-range": good[:-4] + struct.pack(">f", -1.),
             "invalid-gaze-range": packet(values=(2., 0., 1., 0., 0., 1.))}
    for name, data in cases.items():
        (fixtures / (name + ".osc")).write_bytes(data)
    harness = root / "tests" / "bin" / "Release" / "net8.0" / "ModuleTests.dll"
    subprocess.run(["dotnet", str(harness), str(fixtures), sys.executable, str(root / "tests/osc_simulator.py")], check=True, timeout=20)
    # Independent Python implementation supplies protocol frames and fragments
    # TCP replies. The C# direct module never starts a Python bridge itself.
    from runtime_simulator import Server, gaze
    request = bytearray(4096)
    struct.pack_into("<iii", request, 0, 7001, 8, 0)
    request[12:20] = bytes((1, 2, 0, 0, 3, 4, 0, 0))
    struct.pack_into("<I", request, 4092, zlib.crc32(request[:4092], 0xf8e7f8e7))
    runtime_cases = {
        "request": request, "left": gaze(1, 10), "right": gaze(2, 10, x=.8, y=.3),
        "invalid-left": gaze(1, 20, valid=False, pupil=False),
        "invalid-right": gaze(2, 20, valid=False, pupil=False),
        "visible-invalid": gaze(1, 20, valid=False, pupil=True),
        "nan": gaze(1, 30, x=float("nan")),
    }
    for name, content in runtime_cases.items():
        (fixtures / ("runtime-" + name + ".bin")).write_bytes(content)
    servers = {}
    try:
        for name, kwargs in (("normal", {}), ("failure", {"fail": 7007}),
                             ("corrupt", {"corrupt": True}), ("unknown_mode", {"mode": 4}),
                             ("hang", {"hang": True}), ("cleanup_failure", {"fail": 7008})):
            servers[name] = Server(**kwargs)
        scenarios = fixtures / "runtime-scenarios.json"
        scenarios.write_text(json.dumps({name: server.port for name, server in servers.items()}), "utf-8")
        subprocess.run(["dotnet", str(harness), "--runtime-tests", str(fixtures), str(scenarios)], check=True, timeout=15)
        expected = {"normal": [7001, 7003, 7007, 7008, 7004, 7002],
                    "failure": [7001, 7003, 7007, 7004, 7002], "corrupt": [7001],
                    "unknown_mode": [7001, 7002], "hang": [7001],
                    "cleanup_failure": [7001, 7003, 7007, 7008, 7004, 7002]}
        for name, commands in expected.items():
            assert servers[name].commands == commands, (name, servers[name].commands)
        print("PASS: all 6 Runtime command/cleanup sequences verified by independent server")
    finally:
        for server in servers.values():
            server.close()


if __name__ == "__main__":
    main()
