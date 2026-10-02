#!/usr/bin/env python3
"""
Generates the Python-derived expectation files used by the xUnit tests (TestData/*.json) by running the
*reference* ddt4all implementation. The tests do not need Python; re-run this only to regenerate vectors.

  python3 tools/gen_vectors.py [path-to-ddt4all-checkout]      (run from tests/Ddt4All.Core.Tests)

The reference needs a few third-party modules (Qt, serial, platformdirs); they are stubbed here because only the
pure-logic modules core/ecu/* and core/parameters/helpers.py are exercised.
"""
import ast, io, json, math, os, random, sys, types, contextlib

REF = sys.argv[1] if len(sys.argv) > 1 else os.environ.get(
    "DDT4ALL_REF",
    "/tmp/claude-1000/-home-arc-claude-rc/87fd8699-fc28-59a3-8cff-1f345e2918d4/scratchpad/ddt4all")
sys.path.insert(0, os.path.join(REF, "src"))
HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "..", "TestData")


def stub(name, **attrs):
    m = types.ModuleType(name)
    m.__dict__.update(attrs)
    sys.modules[name] = m
    return m


opts = stub("ddt4all.options", translator=lambda d: (lambda s: s), simulation_mode=True, debug=False,
            ecus_dir="./ecus", elm=None)
import ddt4all  # noqa
ddt4all.options = opts
stub("ddt4all.file_manager", get_dir=lambda x: ".", get_config_dir=lambda: ".", get_logs_dir=lambda: ".")
import ddt4all.core  # noqa
elm = stub("ddt4all.core.elm.elm")
ddt4all.core.elm = sys.modules.get("ddt4all.core.elm") or stub("ddt4all.core.elm")
ddt4all.core.elm.elm = elm

# ui.utils pulls in Qt: execute only the pure helper functions of it
src = open(os.path.join(REF, "src/ddt4all/ui/utils.py")).read()
tree = ast.parse(src)
wanted = {"colorConvert", "getRectangleXML", "getFontColor", "getFontXML", "getChildNodesByName"}
ns = {}
for node in tree.body:
    if isinstance(node, ast.FunctionDef) and node.name in wanted:
        exec(compile(ast.Module([node], []), "utils", "exec"), ns)
ui = stub("ddt4all.ui")
uu = stub("ddt4all.ui.utils", **{k: ns[k] for k in wanted})

from ddt4all.core.ecu.ecu_data import EcuData  # noqa
from ddt4all.core.ecu.data_item import DataItem  # noqa
from ddt4all.core.ecu.ecu_file import EcuFile  # noqa
from ddt4all.core.parameters import helpers  # noqa

rnd = random.Random(20240601)


@contextlib.contextmanager
def quiet():
    with contextlib.redirect_stdout(io.StringIO()):
        yield


def hexframe(b):
    return " ".join("%02X" % x for x in b)


def make_data():
    bits = rnd.choice([1, 2, 3, 4, 5, 6, 7, 8, 8, 8, 9, 10, 12, 13, 15, 16, 16, 16, 17, 20, 24, 24, 28, 32, 32, 40, 48, 64])
    d = {"bitscount": bits, "bytescount": (bits + 7) // 8}
    if rnd.random() < 0.06:
        d["bytescount"] = rnd.choice([1, 2, 3, 4])
    if rnd.random() < 0.45:
        d["scaled"] = True
        d["step"] = rnd.choice([1, 1, 0.5, 0.25, 2, 10, 0.1, 100, 0.01, 1.5, 3])
        d["offset"] = rnd.choice([0, 0, -40, 40, -273.15, 10, 0.5, 100, -1])
        d["divideby"] = rnd.choice([1, 1, 2, 10, 4, 100, 3, 0.5, 8])
        d["format"] = rnd.choice(["", "", "0", "0.0", "0.00", "0.000", "#.##", "0.", "0.00.1", "%"])
        d["unit"] = "u"
    if rnd.random() < 0.35:
        d["signed"] = True
    if bits % 8 == 0 and bits >= 8 and not d.get("scaled") and rnd.random() < 0.12:
        d["bytesascii"] = True
    if rnd.random() < 0.15 and not d.get("scaled"):
        n = rnd.randint(1, 4)
        d["lists"] = {str(rnd.randint(-3, 300)): "item%d" % i for i in range(n)}
        d["lists"].update({str(rnd.choice([0, 1, 2, 255, 65535])): "known"})
    return d


def make_item(bits):
    it = {"firstbyte": rnd.choice([1, 1, 2, 2, 3, 4, 5]), "bitoffset": rnd.choice([0, 0, 0, 1, 2, 3, 4, 5, 6, 7])}
    e = rnd.random()
    if e < 0.15:
        it["endian"] = "Little"
    elif e < 0.3:
        it["endian"] = "Big"
    if rnd.random() < 0.1:
        # bit offsets >= 8 are only meaningful (and only supported by the port) for big-endian items;
        # the reference produces garbage (over-long hex) for little-endian ones
        it["bitoffset"] = rnd.randint(8, 12)
        it["endian"] = "Big"
    return it


def run(f):
    try:
        with quiet():
            return f()
    except Exception as ex:  # reference crashes on this input
        return "EXC"


cases = []
for n in range(6000):
    d = make_data()
    it = make_item(d["bitscount"])
    ecu_endian = rnd.choice(["", "Little", "Big", "Little"])
    need = it["firstbyte"] - 1 + (d["bitscount"] + it["bitoffset"] + 7) // 8
    flen = max(1, need + rnd.choice([-1, 0, 0, 0, 1, 2, 5]))
    frame = bytes(rnd.randrange(256) for _ in range(flen))
    if rnd.random() < 0.1:
        frame = bytes([0xFF] * flen)
    ed = EcuData(d, "x")
    di = DataItem(it, ecu_endian, "x")
    fs = hexframe(frame)
    c = {"data": d, "item": it, "ecu_endian": ecu_endian, "frame": fs}
    c["hex"] = run(lambda: ed.getHexValue(fs, di, ecu_endian))
    c["int"] = run(lambda: ed.getIntValue(fs, di, ecu_endian))
    c["display"] = run(lambda: ed.getDisplayValue(fs, di, ecu_endian))
    cases.append(c)

# set cases
sets = []
for n in range(5000):
    d = make_data()
    it = make_item(d["bitscount"])
    ecu_endian = rnd.choice(["", "Little", "Big"])
    bits = d["bitscount"]
    need = it["firstbyte"] - 1 + (bits + it["bitoffset"] + 7) // 8
    flen = max(1, need + rnd.choice([0, 0, 1, 3]))
    if rnd.random() < 0.03:
        flen = max(1, need - 1)
    frame = bytes(rnd.randrange(256) for _ in range(flen))
    if rnd.random() < 0.3:
        frame = bytes([0xFF] * flen)
    elif rnd.random() < 0.2:
        frame = bytes(flen)
    ed = EcuData(d, "x")
    di = DataItem(it, ecu_endian, "x")
    bl = ["%02X" % b for b in frame]
    if ed.bytesascii:
        value = "".join(rnd.choice("ABCxyz 019-_") for _ in range(rnd.randint(0, ed.bytescount + 3)))
    elif ed.scaled:
        raw = rnd.randrange(0, 1 << min(bits, 32))
        value = repr((raw * d["step"] + d["offset"]) / d["divideby"])
        if rnd.random() < 0.05:
            value = rnd.choice(["abc", "", "1e2", " 5 ", "-0"])
    else:
        raw = rnd.randrange(0, 1 << bits)
        value = ("%0" + str(((bits + 3) // 4) + rnd.choice([0, 0, 2])) + "x") % raw
        if rnd.random() < 0.5:
            value = value.upper()
        if rnd.random() < 0.04:
            value = rnd.choice(["zz", "", "12G"])
    res = run(lambda: ed.setValue(value, list(bl), di, ecu_endian))
    overflow = False
    if ed.scaled:
        try:
            iv = int((float(value) * float(ed.divideby) - float(ed.offset)) / float(ed.step))
            overflow = not (0 <= iv < (1 << bits))
        except Exception:
            pass
    sets.append({"data": d, "item": it, "ecu_endian": ecu_endian, "frame": hexframe(frame), "value": value, "result": res,
                 "overflow": overflow})

# overflow behaviours: documented divergence, reference output kept for information only
json.dump({"get": cases, "set": sets}, open(os.path.join(DATA, "codec_vectors.json"), "w"), separators=(",", ":"))
print("codec vectors:", len(cases), len(sets), "ref exceptions get/set:",
      sum(1 for c in cases if c["display"] == "EXC"), sum(1 for c in sets if c["result"] == "EXC"))

# ------------------------------------------------------------------ whole-file expectations
files = ["rich_ecu.xml", "dummy_ecu_can.xml", "dummy_ecu_can_ext.xml", "dummy_ecu_iso8.xml", "dummy_ecu_kwp.xml", "dummy_ecu_can.json"]
for fn in files:
    path = os.path.join(DATA, fn)
    with quiet():
        f = EcuFile(path, True)
    out = f.dumpJson()
    open(os.path.join(DATA, fn + ".expected.json"), "w").write(out)
    if fn.endswith(".xml"):
        with quiet():
            lay = helpers.dumpXML(path)
        if lay:
            open(os.path.join(DATA, fn + ".layout.expected.json"), "w").write(lay)
    ids = f.dump_idents()
    open(os.path.join(DATA, fn + ".idents.expected.json"), "w").write(json.dumps(ids))

# request build/decode expectations on the rich ECU
f = EcuFile(os.path.join(DATA, "rich_ecu.xml"), True)
req = {"build": [], "decode": []}
builds = [
    ("WriteConfig", {}),
    ("WriteConfig", {"Mode": "ON"}),
    ("WriteConfig", {"Mode": "OFF", "Offset": "12.5"}),
    ("WriteConfig", {"Mode": "02", "Offset": "-3", "Bits": "5"}),
    ("WriteConfig", {"Offset": "100", "Bits": "7"}),
    ("WriteConfig", {"Mode": "1"}),
    ("Reset", {}),
]
for name, inputs in builds:
    r = f.requests[name]
    try:
        with quiet():
            stream = r.build_data_stream(dict(inputs))
        req["build"].append({"request": name, "inputs": inputs, "stream": "".join(stream)})
    except Exception as ex:
        req["build"].append({"request": name, "inputs": inputs, "stream": "EXC"})
frames = [
    "61 01 A0 12 34 01 05 3C 80 FF 56 46 31 32 33 34 35 36 37 38 39 30 31 32 33 34 35 36 37",
    "61 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00",
    "61 FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF",
    "61 3C 20 00 FF 18 FF 38 00 41 42 43 44 45 46 47 48 49 4A 4B 4C 4D 4E 4F 50 51",
    "61 3C 20 00 01 FF 80 00 00",
    "61 3C",
]
for fr in frames:
    r = f.requests["ReadData"]
    with quiet():
        vals = r.get_values_from_stream(fr)
    req["decode"].append({"request": "ReadData", "frame": fr, "values": vals})
json.dump(req, open(os.path.join(DATA, "rich_ecu.requests.expected.json"), "w"), indent=1)
print("done")

# ------------------------------------------------------------------ auto identification vectors
from ddt4all.core.ecu.ecu_ident import EcuIdent  # noqa

rnd = random.Random(777)
suppliers = ["BOSCH", "CONTI", "SIEMENS", "DELPHI", "VALEO", "A", ""]
softs = ["1000", "1001", "2000", "X1", ""]
versions = ["0001", "0005", "000A", "00FF", "A001", "ZZ01", "0", "1234", ""]
diags = ["10", "11", "0A", "01", "FF", "13", ""]
protos = ["CAN", "CAN", "KWP2000", "ISO8", "DOIP", "CAN-FD", "FOO"]
db = {}
for e in range(70):
    n = rnd.choice([0, 1, 1, 2, 3, 5])
    db["ECU%02d.json" % e] = {
        "address": "%02X" % rnd.randrange(256), "group": rnd.choice(["UCH", "INJ", "ABS"]), "protocol": rnd.choice(protos),
        "projects": rnd.sample(["x95", "X85", "L38", "B90"], rnd.randint(0, 3)), "ecuname": "Name %d" % e,
        "autoidents": [{"diagnostic_version": rnd.choice(diags), "supplier_code": rnd.choice(suppliers),
                        "soft_version": rnd.choice(softs), "version": rnd.choice(versions)} for _ in range(n)]}
targets = []  # (href, ai_index, EcuIdent)
for href, v in db.items():
    if len(v["autoidents"]) == 0:
        targets.append((href, -1, EcuIdent("", "", "", "", v["ecuname"], v["group"], href, v["protocol"], v["projects"], v["address"], True)))
    else:
        for i, t in enumerate(v["autoidents"]):
            targets.append((href, i, EcuIdent(t["diagnostic_version"], t["supplier_code"], t["soft_version"], t["version"],
                                              v["ecuname"], v["group"], href, v["protocol"], v["projects"], v["address"], True)))


def check_ecu2(diagversion, supplier, soft, version, protocol):
    """Selection logic of EcuScanner.check_ecu2 (UI/logging removed)."""
    approximate = []
    for href, ai, target in targets:
        if target.protocol == "CAN" and protocol != "CAN":
            continue
        if target.protocol.startswith("KWP") and protocol != "KWP":
            continue
        if target.checkWith(diagversion, supplier, soft, version, "00"):
            return ["exact", href, ai]
        elif target.checkApproximate(diagversion, supplier, soft, "00"):
            approximate.append((href, ai, target))
    best = None
    mind = 0xFFFFFF
    for href, ai, tgt in approximate:
        ep = "CAN"
        if tgt.protocol.startswith("KWP"):
            ep = "KWP"
        if tgt.protocol.startswith("ISO8"):
            ep = "KWP"
        if ep != protocol:
            continue
        try:
            iv = int("0x" + version, 16)
            it = int("0x" + tgt.version, 16)
        except ValueError:
            continue
        delta = abs(it - iv)
        if delta < mind:
            mind = delta
            best = [href, ai]
    if best:
        return ["approx"] + best
    return ["none"]


queries = []
for q in range(600):
    if rnd.random() < 0.5 and targets:
        _, _, t = rnd.choice(targets)
        d, s, so, v = t.diagversion, t.supplier, t.soft, t.version
        r = rnd.random()
        if r < 0.3:
            v = rnd.choice(versions)
        elif r < 0.5:
            s = s + "  extra"
        elif r < 0.6:
            s = " " + s + " "
        elif r < 0.7:
            d = rnd.choice(["10", "0a", "A", "010", "13"])
    else:
        d, s, so, v = rnd.choice(diags[:-1]), rnd.choice(suppliers), rnd.choice(softs), rnd.choice(versions)
    proto = rnd.choice(["CAN", "KWP", "CAN"])
    try:
        res = check_ecu2(d, s, so, v, proto)
    except ValueError:
        continue
    queries.append({"diag": d, "supplier": s, "soft": so, "version": v, "protocol": proto, "result": res})
json.dump({"db": db, "queries": queries}, open(os.path.join(DATA, "match_vectors.json"), "w"))
from collections import Counter
print("match vectors:", Counter(q["result"][0] for q in queries))
