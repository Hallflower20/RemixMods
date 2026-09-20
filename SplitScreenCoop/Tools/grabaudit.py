import re, UnityPy, collections
from UnityPy.export.ShaderConverter import ShaderProgram
from UnityPy.helpers import CompressionHelper
from UnityPy.streams import EndianBinaryReader
path = r"C:\Program Files (x86)\Steam\steamapps\common\Rain World\RainWorld_Data\resources.assets"
env = UnityPy.load(path)
def get_entry(arr, i):
    v = arr[i]
    return v[0] if isinstance(v, list) else v
declared_unnamed = set(); declared_named = collections.defaultdict(set)
consumers = collections.defaultdict(lambda: collections.defaultdict(set))  # texname -> shader -> {"vs"/"ps"}
shaders = 0
for obj in env.objects:
    if obj.type.name != "Shader": continue
    sh = obj.read(); pf = sh.m_ParsedForm
    name = pf.m_Name
    if not name.startswith("Futile/"): continue
    shaders += 1
    for ss in pf.m_SubShaders:
        for p in ss.m_Passes:
            if p.m_Type == 2:
                if p.m_TextureName: declared_named[p.m_TextureName].add(name)
                else: declared_unnamed.add(name)
    try:
        blob = bytes(sh.compressedBlob)
        cs, ds, off = get_entry(sh.compressedLengths, 0), get_entry(sh.decompressedLengths, 0), get_entry(sh.offsets, 0)
        dec = CompressionHelper.decompress_lz4(blob[off:off+cs], ds)
    except Exception as e:
        print("!! blob fail", name, e); continue
    reader = EndianBinaryReader(dec, endian="<")
    count = reader.read_int()
    offsets = []
    for i in range(count):
        reader.Position = 4 + i * 12
        offsets.append(reader.read_int())
    offsets.append(len(dec))
    for i in range(count):
        o = offsets[i]; nxt = offsets[i + 1]
        reader.Position = o
        version = reader.read_int(); ptype = reader.read_int(); reader.Position += 12
        if version >= 201608170: reader.Position += 4
        for _ in range(reader.read_int()): reader.read_aligned_string()
        if 201806140 <= version < 202012090:
            for _ in range(reader.read_int()): reader.read_aligned_string()
        code = reader.read_byte_array(); reader.align_stream()
        tail = dec[reader.Position:nxt]
        names = set(m.group().decode() for m in re.finditer(rb"_[A-Za-z][A-Za-z0-9_]{2,}", tail))
        kind = "ps" if ptype in (17, 18, 9, 10, 13, 14) else "vs"  # 17/18 dx11 pixel, 9/10 dx9 pixel?
        for n in names:
            if "Grab" in n or n == "_GrabTexture" or n.endswith("GrabPass") or n in ("_DynamicLevelElements", "_SlopedTerrainMask", "_RippleMask", "_GameplayRippleMask"):
                consumers[n][name].add(kind)
print("shaders:", shaders)
print("\n== unnamed GrabPass declared by:", sorted(s.replace("Futile/", "") for s in declared_unnamed))
print("\n== named GrabPass declared:", {k: sorted(s.replace("Futile/", "") for s in v) for k, v in declared_named.items()})
print("\n== consumers by grab name (shader:kinds):")
for n in sorted(consumers):
    cons = consumers[n]
    decl = declared_named.get(n, set()) if n != "_GrabTexture" else declared_unnamed
    rows = []
    for s in sorted(cons):
        flag = "" if s in decl else "  <-- reads without declaring"
        rows.append(s.replace("Futile/", "") + "(" + "/".join(sorted(cons[s])) + ")" + flag)
    print("  %s [%d consumers, %d declarers]:" % (n, len(cons), len(decl)))
    for r in rows: print("      " + r)
