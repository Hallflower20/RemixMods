import sys, ctypes, UnityPy
from UnityPy.export.ShaderConverter import ShaderProgram
from UnityPy.helpers import CompressionHelper
from UnityPy.streams import EndianBinaryReader
path = r"C:\Program Files (x86)\Steam\steamapps\common\Rain World\RainWorld_Data\resources.assets"
name = "Futile/" + sys.argv[1]
variants = [set(v.split("+")) if v else set() for v in sys.argv[2].split(",")] if len(sys.argv) > 2 else None
maxlines = int(sys.argv[3]) if len(sys.argv) > 3 else 400
env = UnityPy.load(path)
d3d = ctypes.windll.LoadLibrary("d3dcompiler_47.dll")
def disasm(blob):
    pblob = ctypes.c_void_p()
    hr = d3d.D3DDisassemble(blob, len(blob), 0, None, ctypes.byref(pblob))
    if hr != 0: return "D3DDisassemble failed hr=%x" % (hr & 0xffffffff)
    vt = ctypes.cast(ctypes.cast(pblob, ctypes.POINTER(ctypes.c_void_p))[0], ctypes.POINTER(ctypes.c_void_p))
    getptr = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(vt[3])
    getsize = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(vt[4])
    return ctypes.string_at(getptr(pblob), getsize(pblob)).decode(errors="replace")
def get_entry(arr, i):
    v = arr[i]
    return v[0] if isinstance(v, list) else v
for obj in env.objects:
    if obj.type.name != "Shader": continue
    sh = obj.read()
    pf = sh.m_ParsedForm
    if pf.m_Name != name: continue
    blob = bytes(sh.compressedBlob)
    cs, ds, off = get_entry(sh.compressedLengths, 0), get_entry(sh.decompressedLengths, 0), get_entry(sh.offsets, 0)
    prog = ShaderProgram(EndianBinaryReader(CompressionHelper.decompress_lz4(blob[off:off+cs], ds), endian="<"), sh.object_reader.version)
    # parsed-form binding tables (2020 layout): pass.m_NameIndices maps names to indices
    p = pf.m_SubShaders[0].m_Passes[0]
    attrs = [a for a in dir(p) if a.startswith("m_")]
    print("pass attrs:", attrs)
    ni = getattr(p, "m_NameIndices", None)
    names = {}
    if ni:
        try:
            for k, v in (ni.items() if hasattr(ni, "items") else ni): names[v] = k
        except Exception as e: print("nameindices?", type(ni), e)
    fp = p.progFragment
    for sp in fp.m_SubPrograms:
        sattrs = [a for a in dir(sp) if a.startswith("m_")]
        sub = prog.m_SubPrograms[sp.m_BlobIndex]
        kws = set(sub.m_Keywords) | set(sub.m_LocalKeywords or [])
        if variants is not None and kws not in variants: continue
        print("=" * 10, name, "fragment keywords", sorted(kws), "blob", sp.m_BlobIndex, "attrs", sattrs)
        for a in ("m_TextureParams", "m_ConstantBuffers", "m_ConstantBufferBindings", "m_Samplers"):
            v = getattr(sp, a, None)
            if v is None and hasattr(sp, "m_Parameters"): v = getattr(sp.m_Parameters, a, None)
            if v is None: continue
            rows = []
            for t in v:
                d = {x: getattr(t, x) for x in dir(t) if x.startswith("m_")}
                if "m_NameIndex" in d: d["name"] = names.get(d["m_NameIndex"], "?")
                if "m_VectorParams" in d: d["m_VectorParams"] = [(names.get(q.m_NameIndex, "?"), q.m_Index, q.m_ArraySize) for q in d["m_VectorParams"]]
                if "m_MatrixParams" in d: d["m_MatrixParams"] = [(names.get(q.m_NameIndex, "?"), q.m_Index) for q in d["m_MatrixParams"]]
                rows.append(d)
            print(a, rows)
        code = bytes(sub.m_ProgramCode)
        k = code.find(b"DXBC")
        text = disasm(code[k:]) if k >= 0 else "no DXBC"
        lines = [l for l in text.splitlines() if l.strip() and not l.startswith("//")]
        print("\n".join(lines[:maxlines]))
        if len(lines) > maxlines: print("... (%d more lines)" % (len(lines) - maxlines))
