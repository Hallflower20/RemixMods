import sys, UnityPy
from UnityPy.helpers import CompressionHelper
from UnityPy.streams import EndianBinaryReader
path = r"C:\Program Files (x86)\Steam\steamapps\common\Rain World\RainWorld_Data\resources.assets"
targets = ["Futile/" + s for s in sys.argv[1:]]
env = UnityPy.load(path)
def get_entry(arr, i):
    v = arr[i]
    return v[0] if isinstance(v, list) else v
def parse_sub(reader):
    version = reader.read_int(); ptype = reader.read_int(); reader.Position += 12
    if version >= 201608170: reader.Position += 4
    kws = [reader.read_aligned_string() for _ in range(reader.read_int())]
    lkws = [reader.read_aligned_string() for _ in range(reader.read_int())] if 201806140 <= version < 202012090 else []
    code = reader.read_byte_array(); reader.align_stream()
    info = {"version": version, "type": ptype, "kw": kws, "lkw": lkws, "codelen": len(code)}
    try:
        source_map = reader.read_int()
        bind_count = reader.read_int()
        channels = [(reader.read_int(), reader.read_int()) for _ in range(bind_count)]
        cb_count = reader.read_int()
        cbs = []
        for _ in range(cb_count):
            name = reader.read_aligned_string(); used = reader.read_int(); pcount = reader.read_int()
            params = []
            for _ in range(pcount):
                pname = reader.read_aligned_string(); ptype2 = reader.read_int(); rows = reader.read_int(); cols = reader.read_int()
                ismat = reader.read_int(); arr = reader.read_int(); idx = reader.read_int()
                params.append((pname, "rows%d cols%d mat%d arr%d" % (rows, cols, ismat, arr), "reg %d" % (idx // 16)))
            # struct params (2017.3+)
            scount = reader.read_int()
            structs = []
            for _ in range(scount):
                sname = reader.read_aligned_string(); sidx = reader.read_int(); sarr = reader.read_int(); ssize = reader.read_int()
                vcount = reader.read_int()
                for _ in range(vcount):
                    reader.read_aligned_string(); reader.read_int(); reader.read_int(); reader.read_int(); reader.read_int(); reader.read_int(); reader.read_int()
                mcount = reader.read_int()
                for _ in range(mcount):
                    reader.read_aligned_string(); reader.read_int(); reader.read_int(); reader.read_int(); reader.read_int(); reader.read_int(); reader.read_int()
                structs.append(sname)
            cbs.append((name, used, params, structs))
        other_count = reader.read_int()
        others = []
        for _ in range(other_count):
            oname = reader.read_aligned_string(); otype = reader.read_int(); oidx = reader.read_int(); extra = reader.read_int()
            others.append((oname, {0: "tex", 1: "cbuf", 2: "buffer", 3: "uav", 4: "sampler"}.get(otype, str(otype)), oidx, extra))
        info["cbs"] = cbs; info["others"] = others; info["channels"] = channels
    except Exception as e:
        info["err"] = repr(e)
    return info
for obj in env.objects:
    if obj.type.name != "Shader": continue
    sh = obj.read(); pf = sh.m_ParsedForm
    if pf.m_Name not in targets: continue
    print("#" * 15, pf.m_Name)
    for si, ss in enumerate(pf.m_SubShaders):
        print("  subshader", si, "passes:", [(p.m_Type, p.m_TextureName, getattr(p.m_State, 'm_Name', '')) for p in ss.m_Passes])
    blob = bytes(sh.compressedBlob)
    cs, ds, off = get_entry(sh.compressedLengths, 0), get_entry(sh.decompressedLengths, 0), get_entry(sh.offsets, 0)
    dec = CompressionHelper.decompress_lz4(blob[off:off+cs], ds)
    reader = EndianBinaryReader(dec, endian="<")
    count = reader.read_int()
    offsets = []
    for i in range(count):
        reader.Position = 4 + i * 12
        offsets.append(reader.read_int())
    seen = set()
    for i, o in enumerate(offsets):
        reader.Position = o
        info = parse_sub(reader)
        if info["type"] not in (17, 18): continue  # DX11 pixel SM40/SM50
        key = (tuple(sorted(info["kw"])), tuple(sorted(info["lkw"])))
        if key in seen: continue
        seen.add(key)
        if info["lkw"]: continue
        print("  fragment kw=%s: textures=%s" % (list(key[0]), [(n, "t%d" % idx, "s%d" % extra) for (n, t, idx, extra) in info.get("others", []) if t == "tex"]))
        print("     cbuffers:", [(c[0], [(p[0], p[2]) for p in c[2]]) for c in info.get("cbs", [])], "err" in info and info["err"] or "")
        print("     other bindings:", [x for x in info.get("others", []) if x[1] != "tex"])
