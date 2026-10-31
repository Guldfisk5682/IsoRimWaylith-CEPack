# IsoRim Waylith - Combat Extended Pack: generic ammo models (batch "ammo", 2026-10-30, Blender preview round 1).
# Low-poly models for CE's generic ammo looks (AmmoSetDef.similarTo -> generic set, picked per ammo def by ammoClass, as
# CE's AmmoGeneralizer does). One shape per model and stack sprite (a = 1, b = more, c = full stack, as CE's
# Graphic_StackCount and IsoRim's ModelDef.stackModels), one colour set per generic texture folder.
# Analysis: scratchpad ce_ammo/ce_ammo_analysis.md. Shapes and sizes follow CE's item art read as a TOP view (1 cell, no
# drawSize); boxes, cans and racks drawn oblique in the art get a real height. Decals, stencils, hazard icons, mag ribs are
# left out. Colours are sampled from CE's textures, lifted ~1.08x for the 3D shading.
# Colour per ammo type: IsoRim reads Kd per OBJ from the .mtl files the OBJ names (per file, no global material table) and a
# ModelDef has no colour field for fixed colours, so every colour set gets its own .mtl (same material names) and its own
# copy of the shared geometry: ce_ammo_<model>_<type>_<a|b|c>.obj + ce_ammo_<model>_<type>.mtl.
# Game axes while building: x east, y up, z north, one cell per unit, origin = cell centre, nothing below y = 0. The OBJ
# stores z flipped (file z = -game z); every part is its own closed shell, triangulated.
# usage: <Blender>\5.2\python\bin\python.exe -I make_ce_ammo.py <abs Models dir> [<abs report.json>]
import json, math, os, sys
from collections import defaultdict

OUT = sys.argv[1]
REPORT = sys.argv[2] if len(sys.argv) > 2 else None
os.makedirs(OUT, exist_ok=True)
LIFT = 1.08

# ---------------------------------------------------------------- geometry
class Mesh:
    def __init__(self):
        self.parts = []
    def add(self, mat, vt):
        v, t = vt
        self.parts.append((mat, [tuple(map(float, p)) for p in v], list(t)))

def ear_clip(poly):
    idx = list(range(len(poly)))
    def a2(a, b, c):
        return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
    s = sum(poly[i][0] * poly[(i + 1) % len(poly)][1] - poly[(i + 1) % len(poly)][0] * poly[i][1] for i in range(len(poly)))
    if s < 0:
        idx.reverse()
    tris, guard = [], 0
    while len(idx) > 3 and guard < 1000:
        guard += 1
        n = len(idx)
        for k in range(n):
            a, b, c = idx[(k - 1) % n], idx[k], idx[(k + 1) % n]
            if a2(poly[a], poly[b], poly[c]) <= 1e-12:
                continue
            if any(m not in (a, b, c) and a2(poly[a], poly[b], poly[m]) >= 0 and a2(poly[b], poly[c], poly[m]) >= 0
                   and a2(poly[c], poly[a], poly[m]) >= 0 for m in idx):
                continue
            tris.append((a, b, c)); idx.pop(k); break
    tris.append(tuple(idx))
    return tris

def prism(poly, y0, y1):
    n = len(poly)
    v = [(x, y0, z) for x, z in poly] + [(x, y1, z) for x, z in poly]
    t = list(ear_clip(poly)) + [(a + n, b + n, c + n) for a, b, c in ear_clip(poly)]
    for i in range(n):
        j = (i + 1) % n
        t += [(i, j, j + n), (i, j + n, i + n)]
    return v, t

def box(cx, cz, w, d, y0, h, yaw=0.0):
    """Box centred (cx, cz), w along x, d along z, from y0 up h; yaw degrees clockwise seen from above."""
    pts = [(-w / 2, -d / 2), (w / 2, -d / 2), (w / 2, d / 2), (-w / 2, d / 2)]
    return place(prism(pts, y0, y0 + h), yaw=yaw, at=(cx, 0, cz))

def lathe(profile, n):
    """profile [(r, t)] along the local +y axis from one end to the other; r = 0 makes a pole. Closed solid."""
    v, rings = [], []
    for r, h in profile:
        if r < 1e-9:
            rings.append([len(v)]); v.append((0.0, h, 0.0))
        else:
            ring = []
            for i in range(n):
                a = 2 * math.pi * (i + 0.5) / n
                ring.append(len(v)); v.append((r * math.cos(a), h, r * math.sin(a)))
            rings.append(ring)
    t = []
    if len(rings[0]) > 1:   # flat start cap
        t += [(rings[0][0], rings[0][i + 1], rings[0][i]) for i in range(1, n - 1)]
    if len(rings[-1]) > 1:
        t += [(rings[-1][0], rings[-1][i], rings[-1][i + 1]) for i in range(1, n - 1)]
    for A, B in zip(rings, rings[1:]):
        if len(A) == 1:
            t += [(A[0], B[(i + 1) % n], B[i]) for i in range(n)]
        elif len(B) == 1:
            t += [(A[i], A[(i + 1) % n], B[0]) for i in range(n)]
        else:
            for i in range(n):
                j = (i + 1) % n
                t += [(A[i], A[j], B[j]), (A[i], B[j], B[i])]
    return v, t

def place(vt, axis='y', yaw=0.0, tilt=0.0, at=(0.0, 0.0, 0.0)):
    """Local part (axis = +y) laid along 'z' (north) or 'x' (east) or kept upright 'y'; tilt (deg) tips +y toward -z
    before laying; then yaw (deg, clockwise from above) and move."""
    v, t = vt
    out = []
    ct, st = math.cos(math.radians(tilt)), math.sin(math.radians(tilt))
    cy, sy = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    for x, y, z in v:
        y, z = y * ct + z * st, -y * st + z * ct
        if axis == 'z':
            x, y, z = x, z, y
        elif axis == 'x':
            x, y, z = y, x, z
        x, z = x * cy + z * sy, -x * sy + z * cy
        out.append((x + at[0], y + at[1], z + at[2]))
    return out, t

def seg(m, mat, r0, r1, t0, t1, n, axis, cx, cy, cz, yaw=0.0, pivot=None):
    """One coloured section of a round body: frustum r0 -> r1 from t0 to t1 along the axis through (cx, cy, cz)."""
    prof = [(r0, t0), (r1, t1)] if r1 > 1e-9 else [(r0, t0), (0.0, t1)]
    if r0 < 1e-9:
        prof = [(0.0, t0), (r1, t1)]
    v, t = lathe(prof, n)
    if axis == 'z':
        v = [(x + cx, z + cy, y + cz) for x, y, z in v]   # local y -> north, local z -> up
    elif axis == 'x':
        v = [(y + cx, z + cy, x + cz) for x, y, z in v]
    else:
        v = [(x + cx, y + cy, z + cz) for x, y, z in v]
    if yaw:
        px, pz = pivot if pivot else (0.0, 0.0)
        c, s = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
        v = [((x - px) * c + (z - pz) * s + px, y, -(x - px) * s + (z - pz) * c + pz) for x, y, z in v]
    m.add(mat, (v, t))

def body(m, sections, n, axis, cx, cy, cz, yaw=0.0, pivot=None):
    """sections: [(mat, r0, r1, t0, t1)] along the axis (t in cells from (cx, cy, cz))."""
    start = len(m.parts)
    for mat, r0, r1, t0, t1 in sections:
        seg(m, mat, r0, r1, t0, t1, n, axis, cx, cy, cz, yaw, pivot)
    low = min(p[1] for _, v, _ in m.parts[start:] for p in v)
    if low < 0.0:   # a slightly wider band on a polygon corner would dip under the ground: lift the whole body
        m.parts[start:] = [(mat, [(x, y - low, z) for x, y, z in v], t) for mat, v, t in m.parts[start:]]

def ogive(r, t0, t1, k=3):
    """Rounded nose as k frustums: [(r_i, r_i+1, t_i, t_i+1)]."""
    out = []
    for i in range(k):
        a0, a1 = math.pi / 2 * i / k, math.pi / 2 * (i + 1) / k
        out.append((r * math.cos(a0), r * math.cos(a1) if i < k - 1 else 0.0, t0 + (t1 - t0) * math.sin(a0), t0 + (t1 - t0) * math.sin(a1)))
    return out

# ---------------------------------------------------------------- writer
def orient(verts, tris):
    key, weld = {}, []
    for v in verts:
        weld.append(key.setdefault((round(v[0], 5), round(v[1], 5), round(v[2], 5)), len(key)))
    T = [tuple(weld[i] for i in t) for t in tris]
    et = defaultdict(list)
    for ti, t in enumerate(T):
        for e in ((t[0], t[1]), (t[1], t[2]), (t[2], t[0])):
            et[frozenset(e)].append(ti)
    flip = [None] * len(T)
    pos = {weld[i]: verts[i] for i in range(len(verts))}
    for seed in range(len(T)):
        if flip[seed] is not None:
            continue
        flip[seed] = False
        comp, stack = [seed], [seed]
        while stack:
            ti = stack.pop()
            t = T[ti] if not flip[ti] else (T[ti][0], T[ti][2], T[ti][1])
            for a, b in ((t[0], t[1]), (t[1], t[2]), (t[2], t[0])):
                for nj in et[frozenset((a, b))]:
                    if nj == ti or flip[nj] is not None:
                        continue
                    u = T[nj]
                    flip[nj] = any((u[k], u[(k + 1) % 3]) == (a, b) for k in range(3))
                    comp.append(nj); stack.append(nj)
        vol = 0.0
        for ti in comp:
            t = T[ti] if not flip[ti] else (T[ti][0], T[ti][2], T[ti][1])
            p = [(pos[i][0], pos[i][1], -pos[i][2]) for i in t]
            vol += (p[0][0] * (p[1][1] * p[2][2] - p[1][2] * p[2][1]) - p[0][1] * (p[1][0] * p[2][2] - p[1][2] * p[2][0])
                    + p[0][2] * (p[1][0] * p[2][1] - p[1][1] * p[2][0])) / 6.0
        if vol < 0:
            for ti in comp:
                flip[ti] = not flip[ti]
    return [(t[0], t[2], t[1]) if flip[i] else t for i, t in enumerate(tris)]

def check(verts, tris):
    key, weld = {}, []
    for v in verts:
        weld.append(key.setdefault((round(v[0], 5), round(v[1], 5), round(v[2], 5)), len(key)))
    E = defaultdict(int)
    degen = 0
    for t in tris:
        a, b, c = (weld[i] for i in t)
        if len({a, b, c}) < 3:
            degen += 1
        for e in ((a, b), (b, c), (c, a)):
            E[e] += 1
    return sum(1 for (a, b), n in E.items() if E.get((b, a), 0) != n), degen

def write_obj(mesh, name, mtl, header):
    lines = ['# IsoRim model: %s' % name, '# ' + header,
             '# Generated by tools/make_ce_ammo.py. One unit = one map cell, +Y up, file -Z = game north.', 'mtllib ' + mtl]
    vb, rep, allv = 1, {'name': name, 'tris': 0, 'open_edges': 0, 'degenerate': 0}, []
    for pi, (mat, v, t) in enumerate(mesh.parts):
        t = orient(v, t)
        oe, dg = check(v, t)
        rep['tris'] += len(t); rep['open_edges'] += oe; rep['degenerate'] += dg
        lines += ['o %s_%d' % (name, pi), 'usemtl ' + mat]
        lines += ['v %.4f %.4f %.4f' % (x, y, -z) for x, y, z in v]
        lines += ['f %d %d %d' % (a + vb, b + vb, c + vb) for a, b, c in t]
        vb += len(v); allv += v
    rep['bounds'] = {k: [round(min(p[i] for p in allv), 3), round(max(p[i] for p in allv), 3)] for i, k in enumerate('xyz')}
    with open(os.path.join(OUT, name + '.obj'), 'w', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    return rep

SWATCH = {'can': 'Metal', 'canDark': 'Metal', 'canLight': 'Metal', 'mag': 'Metal', 'link': 'Metal', 'loader': 'Metal',
          'crate': 'Metal', 'crateDark': 'Metal', 'case': 'Metal', 'clip': 'Metal', 'drum': 'Metal', 'drumDark': 'Metal',
          'wood': 'Wood', 'woodDark': 'Wood', 'box': None, 'boxDark': None}
def write_mtl(model, typ, cols):
    lines = ['# IsoRim Waylith - CE Pack: ammo colours for %s / %s, sampled from Combat Extended\'s textures (x%.2f lift).' % (model, typ, LIFT)]
    for zone, hexc in cols.items():
        r, g, b = (min(1.0, int(hexc[i:i + 2], 16) / 255.0 * LIFT) for i in (1, 3, 5))
        lines += ['newmtl ceammo_' + zone, 'Kd %.3f %.3f %.3f' % (r, g, b)]
        if SWATCH.get(zone):
            lines.append('map_Kd IsoRim/Swatches/' + SWATCH[zone])
    name = 'ce_ammo_%s_%s.mtl' % (model, typ)
    with open(os.path.join(OUT, name), 'w', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    return name

# ---------------------------------------------------------------- clearance checks (SOP: parts that meet keep >= 0.01)
CHECKS = []
def gap(label, a_max, b_min, need=0.01):
    CHECKS.append({'check': label, 'gap': round(b_min - a_max, 4), 'ok': b_min - a_max >= need - 1e-9})

# ---------------------------------------------------------------- shared pieces
M = 'ceammo_'
def can(m, w=0.82, d=0.50, h=0.40, cz=0.08):
    m.add(M + 'can', box(0, cz, w, d, 0.0, h))
    m.add(M + 'canDark', box(0, cz, w - 0.05, d - 0.07, h, 0.04))                  # lid
    m.add(M + 'canDark', box(0, cz, 0.30, 0.07, h + 0.04, 0.035))                  # handle
    m.add(M + 'canLight', box(w / 2 + 0.02, cz, 0.04, 0.16, h * 0.42, 0.14))      # latch on the east face
    return cz - d / 2                                                              # south face z

def white_case(m, w=0.86, d=0.55, h=0.34, cz=0.08):
    m.add(M + 'case2', box(0, cz, w, d, 0.0, h))
    m.add(M + 'band', box(0, cz, 0.22, d + 0.012, 0.0, h + 0.006))                # type stripe over the lid
    m.add(M + 'case2Dark', box(0, cz, w + 0.012, d + 0.012, h * 0.62, 0.03))       # lid seam
    m.add(M + 'case2Dark', box(-w / 2 - 0.02, cz, 0.04, 0.18, h * 0.30, 0.10))     # side handles
    m.add(M + 'case2Dark', box(w / 2 + 0.02, cz, 0.04, 0.18, h * 0.30, 0.10))
    return cz - d / 2 - 0.006

def standing_round(m, x, z, r, hc, ht, case='case', tip='tip', n=6, y0=0.0):
    body(m, [(M + case, r, r, 0.0, hc), (M + tip, r * 0.85, 0.0, hc, hc + ht)], n, 'y', x, y0, z)

# ---------------------------------------------------------------- models
def roff(dx, dz, yaw):
    """An offset in a part's own frame turned by yaw (degrees, clockwise from above), as place() turns."""
    c, s = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    return dx * c + dz * s, -dx * s + dz * c

def flat_mag(m, x, z, yaw, w=0.17, d=0.62, h=0.08, band_dz=-0.06, band_d=0.15):
    m.add(M + 'mag', box(x, z, w, d, 0.0, h, yaw))
    ox, oz = roff(0.0, band_dz, yaw)
    m.add(M + 'band', box(x + ox, z + oz, w + 0.006, band_d, 0.0, h + 0.006, yaw))
    ox, oz = roff(0.0, d / 2 + 0.012, yaw)
    m.add(M + 'tip', box(x + ox, z + oz, 0.08, 0.035, 0.02, 0.045, yaw))

def pistol(stk):
    m = Mesh()
    if stk == 'a':
        body(m, [(M + 'case', 0.06, 0.06, -0.15, 0.05)] + [(M + 'tip', a, b, c, d) for a, b, c, d in ogive(0.055, 0.05, 0.15)], 8, 'z', 0, 0.06, 0)
    elif stk == 'b':
        flat_mag(m, -0.14, 0.03, 6.0)
        flat_mag(m, 0.11, -0.02, -5.0)
        for z in (-0.16, -0.06):
            standing_round(m, 0.36, z, 0.04, 0.09, 0.06)
        gap('pistol b: mag vs mag', -0.14 + 0.085 + 0.31 * math.sin(math.radians(6)), 0.11 - 0.085 - 0.31 * math.sin(math.radians(5)))
        gap('pistol b: mag vs loose rounds', 0.11 + 0.088 + 0.31 * math.sin(math.radians(5)), 0.36 - 0.04)
        gap('pistol b: loose rounds apart', -0.16 + 0.04, -0.06 - 0.04)
    else:
        s = can(m)
        flat_mag(m, -0.17, -0.33, 94.0, w=0.12, d=0.36, h=0.06, band_dz=-0.03, band_d=0.10)
        for x in (0.12, 0.22):
            standing_round(m, x, -0.33, 0.04, 0.09, 0.06)
        gap('pistol c: mag vs can', -0.33 + 0.06 + 0.18 * math.sin(math.radians(4)), s)
        gap('pistol c: mag vs rounds', -0.17 + 0.18 + 0.03, 0.12 - 0.04)
        gap('pistol c: rounds vs can', -0.33 + 0.04, s)
    return m

def revolver(stk):
    m = Mesh()
    if stk == 'a':
        body(m, [(M + 'case', 0.065, 0.065, -0.16, 0.04)] + [(M + 'tip', a, b, c, d) for a, b, c, d in ogive(0.06, 0.04, 0.16)], 8, 'z', 0, 0.065, 0)
    elif stk == 'b':
        cx, cz = -0.14, 0.10
        body(m, [(M + 'loader', 0.15, 0.15, 0.0, 0.07), (M + 'case', 0.13, 0.13, 0.07, 0.19)], 8, 'y', cx, 0, cz)
        for i in range(6):
            a = 2 * math.pi * i / 6
            body(m, [(M + 'tip', 0.045, 0.0, 0.19, 0.25)], 5, 'y', cx + 0.075 * math.cos(a), 0, cz + 0.075 * math.sin(a))
        # second loader lying on its side, rounds pointing east
        body(m, [(M + 'loader', 0.13, 0.13, -0.10, -0.04), (M + 'case', 0.12, 0.12, -0.04, 0.08)], 8, 'x', 0.16, 0.13, -0.14)
        for i in range(6):
            a = 2 * math.pi * i / 6
            body(m, [(M + 'tip', 0.04, 0.0, 0.08, 0.13)], 5, 'x', 0.16, 0.13 + 0.07 * math.sin(a), -0.14 + 0.07 * math.cos(a))
        standing_round(m, 0.38, 0.16, 0.045, 0.10, 0.05)
        gap('revolver b: standing loader vs lying loader (x)', -0.14 + 0.15, 0.16 - 0.10)
        gap('revolver b: lying loader vs round (x)', 0.16 + 0.13, 0.38 - 0.045)
    else:
        s = can(m)
        body(m, [(M + 'loader', 0.11, 0.11, 0.0, 0.06), (M + 'case', 0.095, 0.095, 0.06, 0.15)], 8, 'y', -0.25, 0, -0.33)
        for i in range(6):
            a = 2 * math.pi * i / 6
            body(m, [(M + 'tip', 0.032, 0.0, 0.15, 0.19)], 5, 'y', -0.25 + 0.055 * math.cos(a), 0, -0.33 + 0.055 * math.sin(a))
        gap('revolver c: loader vs can', -0.33 + 0.11, s)
    return m

def curved_mag(m, cx, cz, length=0.62, width=0.20, h=0.08, bend=0.07, y0=0.0, band_z=(-0.08, 0.08)):
    """A rifle mag lying flat, its south end bent east by 'bend' (slight curve, merged rifle + intermediate)."""
    def outline(z0, z1, grow=0.0):
        k = 6
        L, R = [], []
        for i in range(k + 1):
            z = z0 + (z1 - z0) * i / k
            u = (length / 2 - z) / length                     # 0 at the north end, 1 at the south end
            off = bend * u * u
            L.append((cx + off - width / 2 - grow, cz + z)); R.append((cx + off + width / 2 + grow, cz + z))
        return R + L[::-1]
    m.add(M + 'mag', prism(outline(-length / 2, length / 2), y0, y0 + h))
    m.add(M + 'band', prism(outline(band_z[0], band_z[1], 0.004), y0, y0 + h + 0.006))
    m.add(M + 'tip', box(cx, cz + length / 2 + 0.015, 0.09, 0.04, y0 + 0.015, 0.05))

def rifle(stk):
    m = Mesh()
    if stk == 'a':
        body(m, [(M + 'case', 0.07, 0.07, -0.28, 0.10), (M + 'case', 0.07, 0.045, 0.10, 0.15), (M + 'tip', 0.045, 0.0, 0.15, 0.28)], 8, 'z', 0, 0.07, 0)
    elif stk == 'b':
        curved_mag(m, -0.13, 0.03)
        curved_mag(m, 0.13, -0.03)
        gap('rifle b: mag vs mag', -0.13 + 0.10 + 0.07 * 0.0, 0.13 - 0.10)
    else:
        s = can(m, 0.85)
        for x in (-0.26, -0.04):
            m.add(M + 'mag', place(prism([(-0.085, -0.035), (0.085, -0.035), (0.085, 0.035), (-0.085, 0.035)], 0.0, 0.34), tilt=-12, at=(x, 0.008, -0.29)))
            m.add(M + 'band', place(prism([(-0.089, -0.039), (0.089, -0.039), (0.089, 0.039), (-0.089, 0.039)], 0.14, 0.22), tilt=-12, at=(x, 0.008, -0.29)))
        gap('rifle c: leaning mags vs can', -0.29 + 0.035 + 0.34 * math.sin(math.radians(12)), s)
        gap('rifle c: leaning mags apart', -0.26 + 0.089, -0.04 - 0.089)
    return m

def hc_round(m, x, z, r, L, n=6, y=None, axis='z', yaw=0.0, pivot=None):
    cy = r if y is None else y
    t0 = -L / 2
    # v1 self-review: tips enlarged (CE's belt reads by its coloured tips): shoulder at 0.58, tip from 0.68 with r x0.78
    body(m, [(M + 'case', r, r, t0, t0 + 0.58 * L), (M + 'case', r, r * 0.8, t0 + 0.58 * L, t0 + 0.62 * L),
             (M + 'band', r * 0.8, r * 0.78, t0 + 0.62 * L, t0 + 0.68 * L), (M + 'tip', r * 0.78, 0.0, t0 + 0.68 * L, L / 2)], n, axis, x, cy, z, yaw, pivot)

def highcal(stk):
    m = Mesh()
    if stk == 'a':
        hc_round(m, 0, 0, 0.08, 0.70, 8)
    elif stk == 'b':
        for i in range(5):
            hc_round(m, -0.30 + i * 0.15, -0.03, 0.055, 0.50)
        for i in range(4):
            hc_round(m, -0.225 + i * 0.15, 0.11, 0.055, 0.50, y=0.055 + 0.095)
        m.add(M + 'link', box(0, -0.18, 0.80, 0.05, 0.0, 0.125))   # the belt link wraps the bottom row's cases
        gap('highcal b: rounds side by side', -0.30 + 0.055, -0.15 - 0.055)
        gap('highcal b: top row vs bottom row (axis distance vs 2r)', 0.11, math.hypot(0.075, 0.095))
        gap('highcal b: link vs top row (z)', -0.18 + 0.025, 0.11 - 0.25)

    else:
        s = can(m, 0.85)
        for i in range(6):
            standing_round(m, -0.32 + i * 0.08, -0.32, 0.032, 0.17, 0.07)
        gap('highcal c: rounds side by side', -0.32 + 0.032, -0.24 - 0.032)
        gap('highcal c: rounds vs can', -0.32 + 0.032, s)
    return m

def gren(m, x, z, r, L, y=None, axis='z', n=8):
    cy = r if y is None else y
    t0 = -L / 2
    nose = [(M + 'nose', a, b, c, d) for a, b, c, d in ogive(r, t0 + 0.72 * L, L / 2, 3 if n > 6 else 2)]
    body(m, [(M + 'body', r, r, t0, t0 + 0.58 * L), (M + 'band', r * 1.005, r * 1.005, t0 + 0.58 * L, t0 + 0.72 * L)] + nose, n, axis, x, cy, z)

def grenade(stk):
    m = Mesh()
    if stk == 'a':
        gren(m, 0, 0, 0.13, 0.56)
    elif stk == 'b':
        for i in range(5):
            gren(m, -0.32 + i * 0.16, -0.04, 0.075, 0.36, n=6)
        for i in range(4):
            gren(m, -0.24 + i * 0.16, 0.10, 0.075, 0.36, y=0.075 + 0.14, n=6)
        m.add(M + 'link', box(0, -0.18, 0.86, 0.05, 0.0, 0.16))   # belt wraps the bottom row
        gap('grenade b: side by side', -0.32 + 0.075, -0.16 - 0.075)
        gap('grenade b: top row vs bottom row (axis distance vs 2r)', 0.15, math.hypot(0.08, 0.14))
        gap('grenade b: link vs top row (z)', -0.18 + 0.025, 0.10 - 0.18)
    else:
        s = can(m, 0.82, 0.52, 0.40, 0.06)
        for i in range(6):
            x = -0.30 + i * 0.12
            body(m, [(M + 'body', 0.05, 0.05, 0.44, 0.48)] + [(M + 'nose', a, b, c, d) for a, b, c, d in ogive(0.05, 0.48, 0.54, 2)], 6, 'y', x, 0, 0.22)
        gap('grenade c: noses side by side', -0.30 + 0.05, -0.18 - 0.05)
        gap('grenade c: noses vs handle (z)', 0.06 + 0.035, 0.22 - 0.05)
    return m

def shell_body(m, x, z, r, L, y=None, axis='z', n=8, yaw=0.0, heads_north=False):
    cy = r if y is None else y
    t0 = -L / 2
    secs = [(M + 'head', r * 1.05, r * 1.05, t0, t0 + 0.27 * L), (M + 'hull', r, r, t0 + 0.27 * L, t0 + 0.70 * L),
            (M + 'mark', r * 1.01, r * 1.01, t0 + 0.70 * L, t0 + 0.86 * L), (M + 'hull', r, r, t0 + 0.86 * L, L / 2)]
    if heads_north:
        secs = [(mat, r1, r0, -t1, -t0) for mat, r0, r1, t0, t1 in secs]
    body(m, secs, n, axis, x, cy, z, yaw)

def shotgun(stk, charged=False):
    m = Mesh()
    if stk == 'a':
        shell_body(m, 0, 0, 0.09, 0.42)
    elif stk == 'b':
        yaw = -12.0
        m.add(M + 'card', box(0, 0, 0.86, 0.44, 0.0, 0.03, yaw))
        for i in range(6):
            # heads north (as CE's bandolier art); the shells turn with the card (about the cell centre)
            shell_body(m, -0.325 + i * 0.13, -0.01, 0.055, 0.30, y=0.03 + 0.055, n=6, yaw=yaw, heads_north=True)
        gap('shotgun b: shells side by side', -0.325 + 0.0578, -0.195 - 0.0578)
    else:
        if charged:
            s = white_case(m)
            for i in range(5):
                x = -0.30 + i * 0.10
                body(m, [(M + 'head', 0.042, 0.042, 0.0, 0.06), (M + 'hull', 0.04, 0.04, 0.06, 0.22), (M + 'mark', 0.041, 0.041, 0.16, 0.20)], 6, 'y', x, 0, -0.33)
            gap('charged shotgun c: shells vs case', -0.33 + 0.042, s)
        else:
            m.add(M + 'box', box(-0.04, 0.0, 0.70, 0.50, 0.0, 0.26))
            for i in range(5):                       # shell heads in the open box, 5 x 3 (CE: a grid of brass heads)
                for j in range(3):
                    body(m, [(M + 'head', 0.055, 0.055, 0.26, 0.29)], 6, 'y', -0.32 + i * 0.14, 0, -0.14 + j * 0.14)
            gap('shotgun c: heads apart', -0.32 + 0.055, -0.18 - 0.055)
            m.add(M + 'boxDark', place(prism([(-0.35, -0.02), (0.35, -0.02), (0.35, 0.02), (-0.35, 0.02)], 0.0, 0.34), tilt=-18, at=(-0.04, 0.22, 0.275)))
            for x, z in ((0.38, -0.30), (0.38, -0.16)):
                body(m, [(M + 'head', 0.058, 0.058, 0.0, 0.08), (M + 'hull', 0.055, 0.055, 0.08, 0.30), (M + 'mark', 0.056, 0.056, 0.22, 0.27)], 6, 'y', x, 0, z)
            gap('shotgun c: standing shells vs box', -0.04 + 0.35, 0.38 - 0.058)
            gap('shotgun c: standing shells apart', -0.30 + 0.058, -0.16 - 0.058)
    return m

def tank_shell(m, x, z, y, L=0.98, r=0.10, yaw=45.0, axis="z", n=8):
    t0 = -L / 2
    secs = [(M + 'case', r, r, t0, t0 + 0.63 * L), (M + 'case', r, r * 0.85, t0 + 0.63 * L, t0 + 0.67 * L),
            (M + 'proj', r * 0.85, r * 0.85, t0 + 0.67 * L, t0 + 0.72 * L), (M + 'band', r * 0.87, r * 0.87, t0 + 0.72 * L, t0 + 0.75 * L),
            (M + 'proj', r * 0.85, r * 0.85, t0 + 0.75 * L, t0 + 0.81 * L)]
    og = ogive(r * 0.85, t0 + 0.81 * L, L / 2, 3 if n > 6 else 2)
    secs += [(M + ('proj' if i < 2 else 'nose'), a, b, c, d) for i, (a, b, c, d) in enumerate(og)]
    body(m, secs, n, axis, x, y, z, yaw, pivot=(x, z))

def heavy(stk):
    m = Mesh()
    if stk == 'a':
        tank_shell(m, 0, 0, 0.10)
    elif stk == 'b':
        for off in (-0.11, 0.11):
            tank_shell(m, off * math.cos(math.radians(45)), -off * math.sin(math.radians(45)), 0.10, L=0.92, r=0.095, n=6)
        tank_shell(m, 0.0, 0.0, 0.10 + 0.17, L=0.92, r=0.095, n=6)
        gap("heavy b: shells side by side", 0.095, 0.22 - 0.095)
        gap("heavy b: top vs bottom (axis distance vs 2r)", 0.19, math.hypot(0.11, 0.17))
    else:
        m.add(M + 'crate', box(0, 0.12, 0.90, 0.45, 0.0, 0.38))
        m.add(M + 'crateDark', box(0, 0.12, 0.86, 0.41, 0.38, 0.03))
        m.add(M + 'crateDark', box(0, 0.12, 0.906, 0.10, 0.10, 0.06))
        tank_shell(m, 0.0, -0.30, 0.075, L=0.80, r=0.075, yaw=90.0, axis='z', n=6)
        gap('heavy c: lying shell vs crate', -0.30 + 0.075, 0.12 - 0.225)
    return m

def bofors_round(m, x, z, L=0.82, r=0.08, y=None, yaw=0.0, axis="z", n=8):
    cy = r if y is None else y
    t0 = -L / 2
    body(m, [(M + 'case', r, r, t0, t0 + 0.62 * L), (M + 'case', r, r * 0.75, t0 + 0.62 * L, t0 + 0.67 * L),
             (M + 'band', r * 0.76, r * 0.76, t0 + 0.67 * L, t0 + 0.70 * L), (M + 'proj', r * 0.75, r * 0.6, t0 + 0.70 * L, t0 + 0.86 * L),
             (M + 'nose', r * 0.6, 0.0, t0 + 0.86 * L, L / 2)], n, axis, x, cy, z, yaw, pivot=(x, z))

def light(stk):
    m = Mesh()
    if stk == 'a':
        bofors_round(m, 0, 0)
    elif stk == 'b':
        for i in range(4):
            bofors_round(m, -0.225 + i * 0.15, 0.03, L=0.78, r=0.068, n=6)
        m.add(M + 'clip', box(0, -0.37, 0.60, 0.06, 0.0, 0.08))
        gap('light b: rounds side by side', -0.225 + 0.068, -0.075 - 0.068)
    else:
        s = can(m, 0.85)
        bofors_round(m, 0.0, -0.32, L=0.74, r=0.065, yaw=90.0, n=6)
        gap('light c: lying round vs can', -0.32 + 0.065, s)
    return m

def arty(m, x, z, r=0.11, L=0.89, y=None, axis="z", n=8):
    cy = r if y is None else y
    t0 = -L / 2
    og = ogive(r, t0 + 0.67 * L, L / 2, 3 if n > 6 else 2)
    body(m, [(M + 'body', r * 0.95, r * 0.95, t0, t0 + 0.05 * L), (M + 'drive', r * 1.05, r * 1.05, t0 + 0.05 * L, t0 + 0.11 * L),
             (M + 'body', r, r, t0 + 0.11 * L, t0 + 0.42 * L), (M + 'band', r * 1.01, r * 1.01, t0 + 0.42 * L, t0 + 0.49 * L),
             (M + 'body', r, r, t0 + 0.49 * L, t0 + 0.67 * L)] + [(M + 'nose', a, b, c, d) for a, b, c, d in og], n, axis, x, cy, z)

def artillery(stk):
    m = Mesh()
    if stk == 'a':
        arty(m, 0, 0)
    elif stk == 'b':
        arty(m, -0.125, -0.01, L=0.86, n=6); arty(m, 0.125, -0.01, L=0.86, n=6); arty(m, 0.0, 0.01, L=0.86, y=0.11 + 0.21, n=6)
        gap('artillery b: shells side by side', -0.125 + 0.1155, 0.125 - 0.1155)
        gap('artillery b: top vs bottom (axis distance vs 2r incl. driving band)', 0.231, math.hypot(0.125, 0.21))
    else:
        m.add(M + 'wood', box(0, 0, 0.86, 0.40, 0.0, 0.05))
        for zr in (-0.135, 0.135):
            m.add(M + 'wood', box(0, zr, 0.86, 0.07, 0.40, 0.04))   # top rails in front of and behind the shells
        for x in (-0.405, 0.405):
            for zr in (-0.135, 0.135):
                m.add(M + 'woodDark', box(x, zr, 0.05, 0.05, 0.05, 0.35))
        L = 0.58
        for i in range(4):
            arty(m, -0.2775 + i * 0.185, 0.0, r=0.082, L=L, y=0.05 + L / 2, axis='y', n=6)
        rd = 0.082 * 1.05
        gap('artillery c: shells apart', -0.2775 + rd, -0.0925 - rd)
        gap('artillery c: shell vs rail (z)', rd, 0.135 - 0.035)
        gap('artillery c: outer shell vs post (x)', 0.2775 + rd, 0.405 - 0.025)
    return m

def mortar_shell(m, cx, cz, y, s=1.0, yaw=45.0, axis="z", n=8):
    """Finned mortar bomb along +axis (nose), scaled by s; fins as two crossed plates at the tail."""
    r = 0.11 * s
    secs = [(M + 'tail', 0.035 * s, 0.035 * s, -0.48 * s, -0.06 * s), (M + 'charge', 0.055 * s, 0.055 * s, -0.30 * s, -0.16 * s),
            (M + 'body', 0.05 * s, r, -0.06 * s, 0.07 * s), (M + 'body', r, r, 0.07 * s, 0.13 * s), (M + 'ring', r * 1.01, r * 1.01, 0.13 * s, 0.17 * s),
            (M + 'body', r, r, 0.17 * s, 0.24 * s), (M + 'body', r, 0.06 * s, 0.24 * s, 0.38 * s), (M + 'body', 0.06 * s, 0.0, 0.38 * s, 0.48 * s)]
    body(m, secs, n, axis, cx, y, cz, yaw, pivot=(cx, cz))
    fin_len, fin_w = 0.13 * s, 0.10 * s
    for horiz in (True, False):
        w, h = (2 * fin_w, 0.022) if horiz else (0.022, 2 * fin_w)
        pts = [(-w / 2, -0.48 * s), (w / 2, -0.48 * s), (w / 2, -0.48 * s + fin_len), (-w / 2, -0.48 * s + fin_len)]
        v, t = prism(pts, y - h / 2, y + h / 2)     # in a north-pointing frame: x across, z along
        if axis == 'x':
            v = [(z, yy, -x) for x, yy, z in v]
        c, sn = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
        v = [(x * c + z * sn + cx, yy, -x * sn + z * c + cz) for x, yy, z in v]
        m.add(M + 'fin', (v, t))

def mortar(stk):
    m = Mesh()
    if stk == 'a':
        mortar_shell(m, 0, 0, 0.11)
    elif stk == 'b':
        for off in (-0.12, 0.12):
            mortar_shell(m, 0.03 + off * math.cos(math.radians(45)), 0.03 - off * math.sin(math.radians(45)), 0.10, s=0.9, n=6)
        mortar_shell(m, 0.03, 0.03, 0.10 + 0.19, s=0.9, n=6)
        gap('mortar b: bombs side by side', 0.099 * 1.01, 0.24 - 0.099 * 1.01)
        gap('mortar b: top vs bottom (axis distance vs 2r)', 0.2, math.hypot(0.12, 0.19))
    else:
        m.add(M + 'wood', box(0, 0, 0.92, 0.74, 0.0, 0.04))
        for x in (-0.435, 0.435):
            m.add(M + 'crate', box(x, 0, 0.05, 0.74, 0.04, 0.17))
        for z in (-0.345, 0.345):
            m.add(M + 'crate', box(0, z, 0.82, 0.05, 0.04, 0.17))
        s = 0.65
        for i in range(4):
            mortar_shell(m, 0.02, -0.2325 + i * 0.155, 0.04 + 0.11 * s, s=s, yaw=90.0, n=6)
        rr = 0.11 * s * 1.01
        gap('mortar c: bombs apart', -0.2325 + rr, -0.0775 - rr)
        gap('mortar c: outer bomb vs inner wall (z)', 0.2325 + rr, 0.345 - 0.025)
        gap('mortar c: nose vs inner wall (x)', 0.02 + 0.48 * s, 0.435 - 0.025)
        gap('mortar c: tail vs inner wall (x)', -0.435 + 0.025, 0.02 - 0.48 * s)
    return m

def chmag(stk):
    m = Mesh()
    if stk == 'a':
        body(m, [(M + 'cap', 0.05, 0.05, -0.19, -0.17), (M + 'cell', 0.07, 0.07, -0.17, -0.03), (M + 'band', 0.072, 0.072, -0.03, 0.01),
                 (M + 'cell', 0.07, 0.07, 0.01, 0.15), (M + 'cap', 0.045, 0.045, 0.15, 0.18)], 8, 'z', 0, 0.07, 0)
    elif stk == 'b':
        for x, z in ((-0.14, 0.02), (0.14, -0.03)):
            m.add(M + 'mag', box(x, z, 0.24, 0.56, 0.0, 0.10))
            m.add(M + 'band', box(x, z + 0.21, 0.246, 0.10, 0.0, 0.106))
            m.add(M + 'magDark', box(x, z - 0.06, 0.15, 0.28, 0.10, 0.02))
        gap('charged mag b: mags apart', -0.14 + 0.123, 0.14 - 0.123)
    else:
        s = white_case(m)
        for x in (-0.22, 0.02):
            m.add(M + 'mag', place(prism([(-0.10, -0.035), (0.10, -0.035), (0.10, 0.035), (-0.10, 0.035)], 0.0, 0.30), tilt=-10, at=(x, 0.007, -0.31)))
            m.add(M + 'band', place(prism([(-0.104, -0.039), (0.104, -0.039), (0.104, 0.039), (-0.104, 0.039)], 0.22, 0.29), tilt=-10, at=(x, 0.007, -0.31)))
        gap('charged mag c: leaning mags vs case', -0.31 + 0.039 + 0.29 * math.sin(math.radians(10)), s)
        gap('charged mag c: leaning mags apart', -0.22 + 0.104, 0.02 - 0.104)
    return m

def cell(m, x, z, r, L, y=None, axis='z', n=8):
    cy = r if y is None else y
    t0 = -L / 2
    body(m, [(M + 'cell', r, r, t0, t0 + 0.45 * L), (M + 'band', r * 1.02, r * 1.02, t0 + 0.45 * L, t0 + 0.55 * L),
             (M + 'cell', r, r, t0 + 0.55 * L, t0 + 0.84 * L), (M + 'cap', r, 0.0, t0 + 0.84 * L, L / 2)], n, axis, x, cy, z)

def chcells(stk, mech=False):
    m = Mesh()
    if stk == 'a':
        cell(m, 0, 0, 0.09, 0.60)
    elif stk == 'b':
        for i in range(5):
            cell(m, -0.30 + i * 0.15, -0.04, 0.068, 0.48, n=6)
        for i in range(4):
            cell(m, -0.225 + i * 0.15, 0.06, 0.068, 0.48, y=0.068 + 0.13, n=6)
        gap('charged cells b: side by side', -0.30 + 0.0694, -0.15 - 0.0694)
        gap('charged cells b: top vs bottom (axis distance vs 2r)', 0.1388, math.hypot(0.075, 0.13))
    else:
        if mech:
            # v2 (user review): CE's mech drum, a white drum lying E-W with a darker band near its west end, a grey round end cap
            # ring with a dark hub and a short stub on the east face, and a raised square panel on the top (CE: QR block,
            # here a light frame with a dark recessed square, no pattern); the small cells in front as v1.
            R = 0.28
            ap = R * math.cos(math.pi / 8)          # octagon apothem: flat bottom on the ground, flat top at 2 ap
            body(m, [(M + 'drum', R, R, -0.40, 0.30)], 8, 'x', 0, ap, 0.10)
            body(m, [(M + 'drumBand', R * 1.015, R * 1.015, -0.33, -0.27)], 8, 'x', 0, ap, 0.10)
            body(m, [(M + 'capRing', 0.25, 0.25, 0.30, 0.345)], 12, 'x', 0, ap, 0.10)
            body(m, [(M + 'drumDark', 0.15, 0.15, 0.345, 0.365)], 10, 'x', 0, ap, 0.10)
            body(m, [(M + 'capRing', 0.035, 0.035, 0.365, 0.405)], 6, 'x', 0, ap, 0.10)
            top = 2 * ap
            m.add(M + 'panel', box(-0.12, 0.10, 0.26, 0.18, top, 0.02))
            m.add(M + 'drumDark', box(-0.12, 0.10, 0.17, 0.11, top + 0.02, 0.02))
            gap('mech c: panel inside the drum top flat (z half-width)', 0.09, R * math.sin(math.pi / 8))
            gap('mech c: cells vs drum', -0.30 + 0.031, 0.10 - ap)
            for i in range(5):
                body(m, [(M + 'cell', 0.03, 0.03, 0.0, 0.14), (M + 'band', 0.031, 0.031, 0.06, 0.08), (M + 'cap', 0.03, 0.0, 0.14, 0.18)], 6, 'y', -0.22 + i * 0.08, 0, -0.30)
        else:
            s = white_case(m)
            for i in range(6):
                body(m, [(M + 'cell', 0.034, 0.034, 0.0, 0.18), (M + 'band', 0.035, 0.035, 0.08, 0.11), (M + 'cap', 0.034, 0.0, 0.18, 0.23)], 6, 'y', -0.32 + i * 0.085, 0, -0.33)
            gap('charged cells c: cells vs case', -0.33 + 0.035, s)
            gap('charged cells c: cells apart', -0.32 + 0.035, -0.235 - 0.035)
    return m

def canister(m, x, z):
    body(m, [(M + 'body', 0.15, 0.15, 0.0, 0.05), (M + 'mark', 0.153, 0.153, 0.05, 0.10), (M + 'body', 0.15, 0.15, 0.10, 0.16),
             (M + 'window', 0.125, 0.125, 0.16, 0.29), (M + 'body', 0.14, 0.14, 0.29, 0.44), (M + 'mark', 0.10, 0.10, 0.44, 0.46),
             (M + 'body', 0.065, 0.065, 0.46, 0.47)], 8, 'y', x, 0, z)   # yellow top ring round a black centre, as CE

def fuelcell(stk):
    m = Mesh()
    if stk == 'a':
        canister(m, 0, 0)
    else:
        canister(m, -0.165, 0.12); canister(m, 0.165, 0.12); canister(m, 0.0, -0.17)
        gap('fuel cells b: canisters apart', -0.165 + 0.153, 0.165 - 0.153)
        gap('fuel cells b: front vs back', 0.153, math.hypot(0.165, 0.29) - 0.153)
    return m

# ---------------------------------------------------------------- colour sets (hex from CE's art; see analysis)
CAN = {'FMJ': ('#404830', '#384030', '#607048'), 'AP': ('#787070', '#505050', '#A8A0A0'), 'HP': ('#788060', '#505840', '#A8B090'),
       'Inc': ('#A87800', '#885800', '#E0A800'), 'HE': ('#404040', '#282828', '#686868'), 'Sabot': ('#484030', '#302820', '#786850')}
def can3(k):
    a, b, c = CAN[k]; return {'can': a, 'canDark': b, 'canLight': c}

MODELS = {}
def model(key, fn, sets, stacks='abc', textures=None, label=''):
    MODELS[key] = dict(fn=fn, sets=sets, stacks=stacks, label=label)

model('pistol', pistol, {
    'fmj': dict(case='#C8C080', tip='#D09860', mag='#404040', band='#404040', **can3('FMJ')),
    'ap': dict(case='#C8C080', tip='#F0F0F0', mag='#404040', band='#802828', **can3('AP')),
    'hp': dict(case='#C8C080', tip='#D09860', mag='#404040', band='#90A870', **can3('HP'))}, label='M1 Pistol')
model('revolver', revolver, {
    'fmj': dict(case='#C8C080', tip='#C08850', loader='#686868', **can3('FMJ')),
    'ap': dict(case='#C8C080', tip='#E0E0E0', loader='#686868', **can3('AP')),
    'hp': dict(case='#C8C080', tip='#D09860', loader='#686868', **can3('HP'))}, label='M2 Revolver')
model('rifle', rifle, {
    'fmj': dict(case='#C8C080', tip='#C08850', mag='#404040', band='#404040', **can3('FMJ')),
    'ap': dict(case='#C8C080', tip='#A8A098', mag='#404040', band='#802828', **can3('AP')),
    'hp': dict(case='#C8C080', tip='#C08850', mag='#404040', band='#90A870', **can3('HP')),
    'inc': dict(case='#C8C080', tip='#582080', mag='#404040', band='#0088B0', **can3('Inc')),
    'he': dict(case='#C8C080', tip='#E0E050', mag='#404040', band='#F8F840', **can3('HE')),
    'sabot': dict(case='#C8C080', tip='#E8E8E8', mag='#404040', band='#E0B8A0', **can3('Sabot'))}, label='M3 Rifle')
model('highcal', highcal, {
    'fmj': dict(case='#C8C080', tip='#D09860', band='#D09860', link='#303030', **can3('FMJ')),
    'ap': dict(case='#C8C080', tip='#D0D0D0', band='#D0D0D0', link='#303030', **can3('AP')),
    'inc': dict(case='#C8C080', tip='#7058C0', band='#7058C0', link='#303030', **can3('Inc')),
    'he': dict(case='#C8C080', tip='#383030', band='#F04040', link='#303030', **can3('HE')),
    'sabot': dict(case='#C8C080', tip='#C8C8C8', band='#A83030', link='#303030', **can3('Sabot'))}, label='M4 Heavy cartridges')
model('grenade', grenade, {
    'he': dict(nose='#B89048', band='#181818', body='#98A070', link='#383838', can='#404040', canDark='#282828', canLight='#686868'),
    'air': dict(nose='#C0C0B0', band='#A04028', body='#406030', link='#383838', can='#204030', canDark='#182820', canLight='#406050'),
    'dp': dict(nose='#B89048', band='#282838', body='#707878', link='#383838', can='#404860', canDark='#283040', canLight='#606878'),
    'emp': dict(nose='#90C0D0', band='#00C0F8', body='#A0A088', link='#383838', can='#708088', canDark='#506060', canLight='#90A0A8'),
    'smk': dict(nose='#D8D8D8', band='#A8A8A8', body='#98A070', link='#383838', can='#605850', canDark='#403838', canLight='#807870')}, label='M5 Grenades')
model('shotgun', shotgun, {
    'shot': dict(hull='#A00008', head='#D8D088', mark='#A00008', card='#303030', box='#802008', boxDark='#401000'),
    'slug': dict(hull='#104020', head='#D8D088', mark='#104020', card='#304838', box='#304038', boxDark='#182018'),
    'beanbag': dict(hull='#E0E0E0', head='#B8B8B8', mark='#E81018', card='#D0D0D0', box='#C8C8C8', boxDark='#686868'),
    'emp': dict(hull='#485868', head='#90A8C0', mark='#00C0F8', card='#485868', box='#7890B0', boxDark='#181818')}, label='M6 Shotgun')
model('chshotgun', lambda s: shotgun(s, charged=True), {
    'reg': dict(hull='#E0C008', head='#787870', mark='#282828', card='#807008', case2='#D8D8C8', case2Dark='#B0B0A0', band='#E0C008'),
    'conc': dict(hull='#E02000', head='#787870', mark='#F8F8F8', card='#801000', case2='#D8D8C8', case2Dark='#B0B0A0', band='#E02000'),
    'ion': dict(hull='#287090', head='#787870', mark='#F8F8F8', card='#103040', case2='#D8D8C8', case2Dark='#B0B0A0', band='#287090')}, label='M6 Charged shotgun (shotgun a/b, white case c)')
model('heavy', heavy, {
    'heat': dict(case='#B08030', proj='#484848', band='#F8D830', nose='#484848', crate='#404040', crateDark='#282828'),
    'he': dict(case='#B08030', proj='#404820', band='#F8D830', nose='#F8D830', crate='#485030', crateDark='#303820'),
    'ap': dict(case='#B08030', proj='#404040', band='#404040', nose='#585858', crate='#404040', crateDark='#282828'),
    'apcr': dict(case='#B08030', proj='#787878', band='#787878', nose='#888880', crate='#404040', crateDark='#282828'),
    'emp': dict(case='#B08030', proj='#8090A0', band='#00C0F8', nose='#A8B8C8', crate='#708088', crateDark='#506060')}, label='M7 Heavy cannon')
model('light', light, {
    'ap': dict(case='#A08850', proj='#888888', band='#A08850', nose='#888888', clip='#606070', **can3('AP')),
    'he': dict(case='#A08850', proj='#606840', band='#A08850', nose='#D8D8D0', clip='#606070', **can3('HE')),
    'sabot': dict(case='#A08850', proj='#585858', band='#A08850', nose='#C0C8C8', clip='#606070', **can3('Sabot'))}, label='M7L Light cannon')
model('artillery', artillery, {
    'he': dict(body='#808868', nose='#D0D0D0', band='#F8F840', drive='#C07048', wood='#B88058', woodDark='#986848'),
    'ab': dict(body='#909878', nose='#282828', band='#C04040', drive='#C07048', wood='#B88058', woodDark='#986848'),
    'inc': dict(body='#D07848', nose='#E0E0D8', band='#E8E8E8', drive='#C07048', wood='#B88058', woodDark='#986848'),
    'emp': dict(body='#C0C8D8', nose='#788898', band='#00C0F8', drive='#C07048', wood='#B88058', woodDark='#986848'),
    'smk': dict(body='#909088', nose='#D0D0D0', band='#F8F840', drive='#C07048', wood='#B88058', woodDark='#986848'),
    'anti': dict(body='#686868', nose='#284060', band='#7080A0', drive='#C07048', wood='#B88058', woodDark='#986848')}, label='M8 Artillery')
MB = dict(tail='#989890', charge='#F0F0C0', fin='#C8C8C8', wood='#705840')
model('mortar', mortar, {
    'he': dict(body='#808868', ring='#F0F050', crate='#808868', **MB),
    'airburst': dict(body='#909878', ring='#D84040', crate='#484848', **MB),
    'inc': dict(body='#D07848', ring='#E8E8E8', crate='#D07848', **MB),
    'emp': dict(body='#B8C0D0', ring='#00C0F8', crate='#B8C0D0', **MB),
    'firefoam': dict(body='#C05858', ring='#E0E0E0', crate='#D04848', **MB),
    'smoke': dict(body='#909088', ring='#E8E8E8', crate='#909088', **MB),
    'antigrain': dict(body='#686868', ring='#7080A0', crate='#686868', **MB),
    'toxic': dict(body='#B0C080', ring='#487038', crate='#609848', **MB),
    'deadlife': dict(body='#605858', ring='#A09898', crate='#706058', **MB)}, label='M9 Mortar (re-skin)')
CM = dict(cell='#C8C8B8', cap='#787870', mag='#A8A898', magDark='#707068', case2='#D8D8C8', case2Dark='#B0B0A0')
model('chmag', chmag, {'reg': dict(band='#E0C008', **CM), 'conc': dict(band='#E02000', **CM), 'ion': dict(band='#287090', **CM)}, label='M10 Charged mags')
CC = dict(cell='#C8C8B8', cap='#787870', case2='#D8D8C8', case2Dark='#787870')
model('chcells', chcells, {'reg': dict(band='#E0C008', **CC), 'conc': dict(band='#E02000', **CC), 'ion': dict(band='#287090', **CC)}, label='M11 Charged heavy cells')
model('mechcells', lambda s: chcells(s, mech=True), {'mech': dict(cell='#807068', band='#303030', cap='#706058', drum='#B8C0B0', drumDark='#383830', drumBand='#989080', capRing='#484840', panel='#E0E0D8')}, label='M11 Mech charged (own drum c)')
model('fuelcell', fuelcell, {'demo': dict(body='#303030', window='#A888B0', mark='#F0D030'),
                             'inc': dict(body='#303030', window='#C03010', mark='#F0D030')}, stacks='ab', label='M12 Mech shells')

# generic texture folder -> look (model, colour set); the pack's rule (Api 42) maps each ammo def to these
LOOKS = {
    'Things/Ammo/Pistol/FMJ': 'pistol_fmj', 'Things/Ammo/Pistol/AP': 'pistol_ap', 'Things/Ammo/Pistol/HP': 'pistol_hp',
    'Things/Ammo/Revolver/FMJ': 'revolver_fmj', 'Things/Ammo/Revolver/AP': 'revolver_ap', 'Things/Ammo/Revolver/HP': 'revolver_hp',
    'Things/Ammo/HighCaliber/FMJ': 'highcal_fmj', 'Things/Ammo/HighCaliber/AP': 'highcal_ap', 'Things/Ammo/HighCaliber/Incendiary': 'highcal_inc',
    'Things/Ammo/HighCaliber/HE': 'highcal_he', 'Things/Ammo/HighCaliber/Sabot': 'highcal_sabot',
    'Things/Ammo/GrenadeLauncher/HE': 'grenade_he', 'Things/Ammo/GrenadeLauncher/AIR': 'grenade_air', 'Things/Ammo/GrenadeLauncher/DP': 'grenade_dp',
    'Things/Ammo/GrenadeLauncher/EMP': 'grenade_emp', 'Things/Ammo/GrenadeLauncher/SMK': 'grenade_smk',
    'Things/Ammo/Shotgun/Shot': 'shotgun_shot', 'Things/Ammo/Shotgun/Slug': 'shotgun_slug', 'Things/Ammo/Shotgun/Beanbag': 'shotgun_beanbag', 'Things/Ammo/Shotgun/EMP': 'shotgun_emp',
    'Things/Ammo/Charged/ShotgunRegular': 'chshotgun_reg', 'Things/Ammo/Charged/ShotgunConc': 'chshotgun_conc', 'Things/Ammo/Charged/ShotgunIon': 'chshotgun_ion',
    'Things/Ammo/Cannon/Tank/HEAT': 'heavy_heat', 'Things/Ammo/Cannon/Tank/HE': 'heavy_he', 'Things/Ammo/Cannon/Tank/AP': 'heavy_ap',
    'Things/Ammo/Cannon/Tank/APCR': 'heavy_apcr', 'Things/Ammo/Cannon/Tank/EMP': 'heavy_emp',
    'Things/Ammo/HighCaliber/Bofors/AP': 'light_ap', 'Things/Ammo/HighCaliber/Bofors/HE': 'light_he', 'Things/Ammo/HighCaliber/Bofors/Sabot': 'light_sabot',
    'Things/Ammo/Cannon/Howitzer/HE': 'artillery_he', 'Things/Ammo/Cannon/Howitzer/AB': 'artillery_ab', 'Things/Ammo/Cannon/Howitzer/INC': 'artillery_inc',
    'Things/Ammo/Cannon/Howitzer/EMP': 'artillery_emp', 'Things/Ammo/Cannon/Howitzer/SMK': 'artillery_smk', 'Things/Ammo/Cannon/Howitzer/ANTI': 'artillery_anti',
    'Things/Ammo/Mortar/HE': 'mortar_he', 'Things/Ammo/Mortar/Airburst': 'mortar_airburst', 'Things/Ammo/Mortar/Incendiary': 'mortar_inc',
    'Things/Ammo/Mortar/EMP': 'mortar_emp', 'Things/Ammo/Mortar/Firefoam': 'mortar_firefoam', 'Things/Ammo/Mortar/Smoke': 'mortar_smoke',
    'Things/Ammo/Mortar/Antigrain': 'mortar_antigrain', 'Things/Ammo/Mortar/Toxic': 'mortar_toxic', 'Things/Ammo/Mortar/Deadlife': 'mortar_deadlife',
    'Things/Ammo/Charged/SmallRegular': 'chmag_reg', 'Things/Ammo/Charged/SmallConc': 'chmag_conc', 'Things/Ammo/Charged/SmallIon': 'chmag_ion',
    'Things/Ammo/Charged/MediumRegular': 'chmag_reg', 'Things/Ammo/Charged/MediumConc': 'chmag_conc', 'Things/Ammo/Charged/MediumIon': 'chmag_ion',
    'Things/Ammo/Charged/LargeRegular': 'chcells_reg', 'Things/Ammo/Charged/LargeConc': 'chcells_conc', 'Things/Ammo/Charged/LargeIon': 'chcells_ion',
    'Things/Ammo/Charged/MediumMech': 'mechcells_mech',
    'Things/Ammo/FuelCell/Large': 'fuelcell_demo (Demolition) / fuelcell_inc (GrenadeIncendiary): same CE art, picked by ammoClass',
}
for k in ('FMJ', 'AP', 'HP', 'Incendiary', 'HE', 'Sabot'):
    v = {'FMJ': 'fmj', 'AP': 'ap', 'HP': 'hp', 'Incendiary': 'inc', 'HE': 'he', 'Sabot': 'sabot'}[k]
    LOOKS['Things/Ammo/Rifle/%s' % k] = 'rifle_' + v
    LOOKS['Things/Ammo/Rifle/Battlerifle/%s' % k] = 'rifle_' + v

if __name__ == '__main__':
    only = sys.argv[3:] if len(sys.argv) > 3 else None
    reports = []
    for key, md in MODELS.items():
        if only and key not in only:
            continue
        shapes = {}
        for stk in md['stacks']:
            CHECKS_BEFORE = len(CHECKS)
            shapes[stk] = md['fn'](stk)
            for c in CHECKS[CHECKS_BEFORE:]:
                c['model'] = '%s_%s' % (key, stk)
        for typ, cols in md['sets'].items():
            mtl = write_mtl(key, typ, cols)
            used = set()
            for stk, mesh in shapes.items():
                used |= {p[0][len(M):] for p in mesh.parts}
                rep = write_obj(mesh, 'ce_ammo_%s_%s_%s' % (key, typ, stk), mtl, '%s, colour set %s, stack sprite %s (CE Graphic_StackCount).' % (md['label'], typ, stk))
                reports.append(rep)
            missing = used - set(cols)
            if missing:
                raise SystemExit('missing colours %s for %s/%s' % (sorted(missing), key, typ))
    bad = [r for r in reports if r['open_edges'] or r['degenerate'] or r['bounds']['y'][0] < -1e-6
           or max(abs(r['bounds']['x'][0]), abs(r['bounds']['x'][1]), abs(r['bounds']['z'][0]), abs(r['bounds']['z'][1])) > 0.47]
    failed = [c for c in CHECKS if not c['ok']]
    out = {'objs': len(reports), 'bad': bad, 'checks': CHECKS, 'failed_checks': failed,
           'tris': {r['name']: r['tris'] for r in reports}, 'bounds': {r['name']: r['bounds'] for r in reports}, 'looks': LOOKS}
    if REPORT:
        json.dump(out, open(REPORT, 'w'), indent=1)
    print('%d OBJ, %d bad, %d/%d clearance checks failed' % (len(reports), len(bad), len(failed), len(CHECKS)))
    for b in bad[:20]:
        print(' BAD', b['name'], b['open_edges'], b['degenerate'], b['bounds'])
    for c in failed:
        print(' CHECK', c)
