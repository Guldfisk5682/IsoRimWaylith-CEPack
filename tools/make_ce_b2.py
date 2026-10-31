# IsoRim Waylith - Combat Extended Pack, model batch 2 (2026-10-30, Blender preview round 1): CE's auto-turrets
# Turret_Medium, Turret_Heavy, Turret_Blaster (CIWS_Blaster is commented out in CE: skipped, user decision 3).
# Built from CE's own textures (Things/Building/Turrets/MachineGunBase, MediumTurret_Top, HeavyTurret_Top,
# BlasterTurret_Top) and the approved analysis (ce_models_b2_analysis.md + the user's decisions of 2026-10-30):
#   1 base (shared, ce_mgbase.obj): a copy of IsoRim's vanilla mini-turret base (IsoRim17 Models/turret_mini.obj, read
#     only: its 5-sided leg tubes and their top ends at the head) re-aimed to CE's MachineGunBase art: feet at the art's
#     foot pads (S (0,-0.54), NE/NW (+-0.50,+0.29)), small foot pads as the art draws them, a round hub d0.30 (dark top
#     #252525, rim #464646) in place of the octagonal head (same height 0.36..0.46); CE's darker colours;
#   2 bores 0.54 (Medium, Blaster) and 0.55 (Heavy); the gun bodies float 0.0105 above the hub top (0.46);
#   M19 (2026-10-31, user playtest: the guns sat below IsoRim's low firing wall top 0.76): base hub and guns raised by
#     MG_LIFT 0.36 (shape unchanged; the legs keep their feet, their top ends move up with the hub): bores 0.90 (Medium,
#     Blaster) and 0.91 (Heavy);
#   4 left out: Medium muzzle band, Heavy muzzle slots / thin rear strip / top-plate seam; kept: Medium sleeve cover,
#     Heavy sleeve slot, the Blaster's 3 vents;  5 the Blaster's olive cell is plain olive (no glow).
# IsoRim's turret conventions as batch 1 (make_ce_b1.py): <model> = the base (CE's auto-turrets are not rotatable);
# <topModel> = the gun, barrel along +x, origin = yaw pivot at the bore, topHeight = bore; muzzle = middle of the gun's
# front-most face.
# Axes while building: x east (gun: forward), y north (gun: its left), z up; one unit = one cell; origin = cell centre.
# OBJ: one unit = one cell, +Y up, file -Z = game north. Every part is its own closed convex shell.
# Checks (--checks): closed shells, bounds, height under the 1.60 storey, the gun swept 360 degrees against the base
# (min gap 0.01, conservative plane distance as batch 1), muzzle.
# usage: <Blender>\5.2\python\bin\python.exe -I make_ce_b2.py <abs Models dir> --isorim <abs IsoRim17 Models dir> [--checks <abs checks.json>]
import json, math, os, sys

OUT = sys.argv[1]
CHECKS = sys.argv[sys.argv.index('--checks') + 1] if '--checks' in sys.argv else None
ISORIM = sys.argv[sys.argv.index('--isorim') + 1]
os.makedirs(OUT, exist_ok=True)
LIB = 'ce_b2.mtl'
GAP = 0.01
STOREY = 1.60


def kd(hexcol, lift=1.1):
    h = hexcol.lstrip('#')
    return tuple(min(1.0, int(h[i:i + 2], 16) / 255.0 * lift) for i in (0, 2, 4))


# name: (hex sampled from CE's art, metal swatch?)
COLOURS = {
    'ceb2_leg': ('#4C4C4C', True),        # MachineGunBase legs
    'ceb2_foot': ('#484848', True),       # foot pads
    'ceb2_hub': ('#252525', True),        # hub top (dark round disc)
    'ceb2_rim': ('#464646', True),        # hub rim
    'ceb2_med_body': ('#755E43', True),
    'ceb2_med_hump': ('#70624C', True),
    'ceb2_med_sleeve': ('#63503B', True),
    'ceb2_med_grey': ('#515151', True),   # rear plate, barrel
    'ceb2_med_cover': ('#474747', True),
    'ceb2_hvy_side': ('#635040', True),   # armoured body, chamfers
    'ceb2_hvy_top': ('#826B52', True),
    'ceb2_hvy_rear': ('#6D5845', True),
    'ceb2_hvy_sleeve': ('#77624D', True),
    'ceb2_hvy_plate': ('#565656', True),  # front plate, sleeve slot
    'ceb2_hvy_collar': ('#727272', True),
    'ceb2_hvy_barrel': ('#4C4C4C', True),
    'ceb2_bl_shell': ('#7F7F7F', True),
    'ceb2_bl_olive': ('#6F8145', False),
    'ceb2_bl_band': ('#5B5B5B', True),    # rear band, neck step 1
    'ceb2_bl_dark': ('#282828', True),    # neck step 2, taper
    'ceb2_bl_housing': ('#3A3A3A', True),
    'ceb2_bl_vent': ('#0E0E0E', False),
    'ceb2_bl_tip': ('#515151', True),
}

# ---------------------------------------------------------------- geometry
def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def mul(a, s): return (a[0] * s, a[1] * s, a[2] * s)
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def norm(a):
    l = math.sqrt(dot(a, a)); return (a[0] / l, a[1] / l, a[2] / l)


class Part:
    """One closed convex shell: verts, outward triangles, its face planes."""
    def __init__(self, name, mat, verts, polys):
        self.name, self.mat, self.verts = name, mat, verts
        c = mul((sum(v[0] for v in verts), sum(v[1] for v in verts), sum(v[2] for v in verts)), 1.0 / len(verts))
        self.tris, self.planes = [], []
        for poly in polys:
            for k in range(1, len(poly) - 1):
                t = (poly[0], poly[k], poly[k + 1])
                a, b, cc = (verts[i] for i in t)
                n = cross(sub(b, a), sub(cc, a))
                if math.sqrt(dot(n, n)) < 1e-12:
                    continue
                if dot(n, sub(a, c)) < 0:
                    t = (t[0], t[2], t[1]); n = mul(n, -1)
                self.tris.append(t)
                n = norm(n)
                d = dot(n, a)
                if not any(abs(dot(n, q[0]) - 1) < 1e-6 and abs(q[1] - d) < 1e-6 for q in self.planes):
                    self.planes.append((n, d))

    def sdf(self, p):
        return max(dot(n, p) - d for n, d in self.planes)

    def samples(self, k=4):
        pts = list(self.verts)
        for t in self.tris:
            a, b, c = (self.verts[i] for i in t)
            for i in range(k + 1):
                for j in range(k + 1 - i):
                    u, v = i / k, j / k
                    pts.append(add(add(mul(a, 1 - u - v), mul(b, u)), mul(c, v)))
        return pts

    def moved(self, f):
        p = Part.__new__(Part)
        p.name, p.mat, p.tris = self.name, self.mat, self.tris
        p.verts = [f(v) for v in self.verts]
        p.planes = []
        c = mul((sum(v[0] for v in p.verts), sum(v[1] for v in p.verts), sum(v[2] for v in p.verts)), 1.0 / len(p.verts))
        seen = []
        for t in p.tris:
            a, b, cc = (p.verts[i] for i in t)
            n = norm(cross(sub(b, a), sub(cc, a)))
            d = dot(n, a)
            if not any(abs(dot(n, q[0]) - 1) < 1e-6 and abs(q[1] - d) < 1e-6 for q in seen):
                seen.append((n, d))
        p.planes = seen
        return p


def box(name, mat, x0, x1, y0, y1, z0, z1):
    v = [(x, y, z) for z in (z0, z1) for y in (y0, y1) for x in (x0, x1)]
    return Part(name, mat, v, [(0, 1, 3, 2), (4, 5, 7, 6), (0, 1, 5, 4), (2, 3, 7, 6), (0, 2, 6, 4), (1, 3, 7, 5)])


def prism(name, mat, poly, z0, z1):
    n = len(poly)
    v = [(x, y, z0) for x, y in poly] + [(x, y, z1) for x, y in poly]
    faces = [tuple(range(n)), tuple(range(n, 2 * n))] + [(i, (i + 1) % n, (i + 1) % n + n, i + n) for i in range(n)]
    return Part(name, mat, v, faces)


def hull8(name, mat, a, b):
    """Convex loft between two rectangles a, b = [4 corner points each, same winding]."""
    v = list(a) + list(b)
    return Part(name, mat, v, [(0, 1, 2, 3), (4, 5, 6, 7)] + [(i, (i + 1) % 4, (i + 1) % 4 + 4, i + 4) for i in range(4)])


def lathe(name, mat, p0, axis, profile, n=16, phase=0.0):
    """Solid of revolution along axis from p0: profile = [(s, r), ...] (s along the axis, r >= 0; r = 0 is a point)."""
    ax = norm(axis)
    ref = (0.0, 0.0, 1.0) if abs(ax[2]) < 0.9 else (1.0, 0.0, 0.0)
    u = norm(cross(ax, ref)); w = cross(ax, u)
    v, rings = [], []
    for s, r in profile:
        c = add(p0, mul(ax, s))
        if r <= 1e-9:
            rings.append([len(v)]); v.append(c); continue
        ring = []
        for i in range(n):
            a = 2 * math.pi * (i + phase) / n
            ring.append(len(v)); v.append(add(c, add(mul(u, r * math.cos(a)), mul(w, r * math.sin(a)))))
        rings.append(ring)
    faces = []
    if len(rings[0]) > 1:
        faces.append(tuple(rings[0]))
    if len(rings[-1]) > 1:
        faces.append(tuple(rings[-1]))
    for A, B in zip(rings, rings[1:]):
        for i in range(n):
            if len(A) == 1:
                faces.append((A[0], B[i], B[(i + 1) % n]))
            elif len(B) == 1:
                faces.append((A[i], A[(i + 1) % n], B[0]))
            else:
                faces.append((A[i], A[(i + 1) % n], B[(i + 1) % n], B[i]))
    return Part(name, mat, v, faces)


def cyl(name, mat, p0, p1, r, n=16, phase=0.0, r1=None):
    d = sub(p1, p0)
    L = math.sqrt(dot(d, d))
    return lathe(name, mat, p0, d, [(0.0, r), (L, r if r1 is None else r1)], n, phase)


def octagon(across):
    a = across / 2.0
    R = a / math.cos(math.pi / 8)
    return [(R * math.cos(math.pi / 8 + k * math.pi / 4), R * math.sin(math.pi / 8 + k * math.pi / 4)) for k in range(8)]


def leg(name, mat, top, foot, r, n=8):
    """A tube from the head (top) to a foot pad; the end is lifted so its lower rim stays above the ground."""
    d = norm(sub(foot, top))
    tilt = math.acos(min(1.0, abs(d[2])))   # angle from vertical
    end = (foot[0], foot[1], max(foot[2], r * math.sin(tilt) + 0.002))
    return cyl(name, mat, top, end, r, n)


# ---------------------------------------------------------------- base: IsoRim's mini-turret base, re-aimed to CE's art
MG_LIFT = 0.36                       # M19: added on top of the approved v1 heights below
HUB_TOP = 0.46
BODY_Z = HUB_TOP + 0.0105            # gun bodies start here (>= 0.01 clear of the hub top)
CE_FEET = [(0.0, -0.54), (0.50, 0.29), (-0.50, 0.29)]   # foot-pad centres on CE's MachineGunBase (cells, +y north)


def isorim_leg_ends():
    """The 5-sided leg tubes of IsoRim17 Models/turret_mini.obj: [(top pentagon, foot pentagon)], build axes."""
    pts, polys, mat = [], [], None
    for line in open(os.path.join(ISORIM, 'turret_mini.obj')):
        t = line.split()
        if not t:
            continue
        if t[0] == 'usemtl':
            mat = t[1]
        elif t[0] == 'v':
            x, y, z = map(float, t[1:4]); pts.append((x, -z, y))
        elif t[0] == 'f' and mat == 'gun_dark' and len(t) == 6:
            polys.append([pts[int(s.split('/')[0]) - 1] for s in t[1:]])
    centre = lambda p: mul((sum(v[0] for v in p), sum(v[1] for v in p), sum(v[2] for v in p)), 1.0 / len(p))
    tops = [p for p in polys if centre(p)[2] > 0.2]
    feet = [p for p in polys if centre(p)[2] <= 0.2]
    assert len(tops) == 3 and len(feet) == 3, (len(tops), len(feet))
    key = lambda v: (round(v[0], 3), round(v[1], 3), round(v[2], 3))
    tris = []
    mat = None
    for line in open(os.path.join(ISORIM, 'turret_mini.obj')):
        t = line.split()
        if t and t[0] == 'usemtl':
            mat = t[1]
        elif t and t[0] == 'f' and mat == 'gun_dark' and len(t) == 4:
            tris.append([key(pts[int(q.split('/')[0]) - 1]) for q in t[1:]])
    legs = []
    for f in feet:
        fk = set(key(v) for v in f)
        linked = set()
        for tr in tris:
            if any(k in fk for k in tr):
                linked.update(k for k in tr if k not in fk)
        tp = max(tops, key=lambda p: sum(1 for v in p if key(v) in linked))
        legs.append((tp, f))
    return legs, centre


def up(parts, dz):
    """M19: parts moved up by dz."""
    return [p.moved(lambda v: (v[0], v[1], v[2] + dz)) for p in parts] if dz else parts


def rotz(v, a):
    c, s = math.cos(a), math.sin(a)
    return (v[0] * c - v[1] * s, v[0] * s + v[1] * c, v[2])


def mgbase():
    P = []
    legs, centre = isorim_leg_ends()
    used = set()
    for i, (tp, fp) in enumerate(legs):
        fc = centre(fp)
        ang0 = math.atan2(fc[1], fc[0])
        # the CE foot nearest in angle (IsoRim's legs are turned ~10 degrees from the art)
        j = min((k for k in range(3) if k not in used), key=lambda k: abs(math.remainder(math.atan2(CE_FEET[k][1], CE_FEET[k][0]) - ang0, 2 * math.pi)))
        used.add(j)
        tx, ty = CE_FEET[j]
        da = math.atan2(ty, tx) - ang0
        top = [add(rotz(v, da), (0.0, 0.0, MG_LIFT)) for v in tp]
        offs = [rotz(sub(v, fc), da) for v in fp]
        lift = max(0.0, 0.002 - min(fc[2] + o[2] for o in offs))
        foot = [add((tx, ty, fc[2] + lift), o) for o in offs]
        # top pentagon and foot pentagon in matching order: pair each top vertex with the foot vertex at the same angle round the axis
        ax = norm(sub(centre(foot), centre(top)))
        ref = norm(cross(ax, (0, 0, 1)))
        def ang(v, c):
            d = sub(v, c); d = sub(d, mul(ax, dot(d, ax)))
            return math.atan2(dot(cross(ref, d), ax), dot(ref, d))
        ct, cf = centre(top), centre(foot)
        top = sorted(top, key=lambda v: ang(v, ct)); foot = sorted(foot, key=lambda v: ang(v, cf))
        v = top + foot
        faces = [(0, 1, 2, 3, 4), (5, 6, 7, 8, 9)] + [(k, (k + 1) % 5, (k + 1) % 5 + 5, k + 5) for k in range(5)]
        P.append(Part('leg%d' % i, 'ceb2_leg', v, faces))
        P.append(cyl('pad%d' % i, 'ceb2_foot', (tx, ty, 0.0), (tx, ty, 0.025), 0.055, 6, 0.5))
    P.append(cyl('hub_rim', 'ceb2_rim', (0, 0, 0.36 + MG_LIFT), (0, 0, 0.45 + MG_LIFT), 0.15, 12))
    P.append(cyl('hub_top', 'ceb2_hub', (0, 0, 0.45 + MG_LIFT), (0, 0, HUB_TOP + MG_LIFT), 0.125, 12))
    return P


def loft(name, mat, xa, ya, za0, za1, xb, yb, zb0, zb1):
    """Convex loft between two y-symmetric rectangles across x (a wedge / taper): at x = xa half-width ya, z za0..za1."""
    a = [(xa, -ya, za0), (xa, ya, za0), (xa, ya, za1), (xa, -ya, za1)]
    b = [(xb, -yb, zb0), (xb, yb, zb0), (xb, yb, zb1), (xb, -yb, zb1)]
    return hull8(name, mat, a, b)


def hump(name, mat, x0, x1b, x1t, yb, yt, z0, z1):
    """Raised block with chamfered long edges and a sloped front: base x0..x1b half-width yb at z0, top x0..x1t half-width yt at z1."""
    a = [(x0, -yb, z0), (x1b, -yb, z0), (x1b, yb, z0), (x0, yb, z0)]
    b = [(x0, -yt, z1), (x1t, -yt, z1), (x1t, yt, z1), (x0, yt, z1)]
    return hull8(name, mat, a, b)


# ---------------------------------------------------------------- Medium
MED_BORE0 = 0.54
MED_BORE = MED_BORE0 + MG_LIFT


def medium_gun():
    P = []
    P.append(box('rear', 'ceb2_med_grey', -0.35, -0.30, -0.10, 0.10, 0.49, 0.61))
    P.append(box('body', 'ceb2_med_body', -0.30, 0.10, -0.135, 0.135, BODY_Z, 0.61))
    P.append(hump('hump', 'ceb2_med_hump', -0.30, 0.00, -0.06, 0.12, 0.07, 0.61, 0.66))
    P.append(loft('neck', 'ceb2_med_sleeve', 0.10, 0.135, BODY_Z + 0.004, 0.61, 0.17, 0.08, 0.49, 0.59))
    P.append(box('sleeve', 'ceb2_med_sleeve', 0.17, 0.38, -0.08, 0.08, 0.49, 0.59))
    P.append(box('cover', 'ceb2_med_cover', 0.18, 0.31, -0.04, 0.04, 0.59, 0.603))
    P.append(cyl('barrel', 'ceb2_med_grey', (0.37, 0, MED_BORE0), (0.56, 0, MED_BORE0), 0.04, 8, 0.5))
    return up(P, MG_LIFT)


# ---------------------------------------------------------------- Heavy
HVY_BORE0 = 0.55
HVY_BORE = HVY_BORE0 + MG_LIFT


def heavy_gun():
    P = []
    P.append(prism('rear', 'ceb2_hvy_rear', [(-0.38, -0.10), (-0.35, -0.135), (-0.30, -0.135), (-0.30, 0.135), (-0.35, 0.135), (-0.38, 0.10)], 0.49, 0.63))
    P.append(box('body', 'ceb2_hvy_side', -0.30, 0.07, -0.205, 0.205, BODY_Z, 0.63))
    P.append(hump('armour', 'ceb2_hvy_side', -0.30, 0.07, 0.05, 0.19, 0.08, 0.63, 0.68))
    P.append(box('top', 'ceb2_hvy_top', -0.28, 0.05, -0.08, 0.08, 0.68, 0.692))
    P.append(loft('front', 'ceb2_hvy_plate', 0.07, 0.19, BODY_Z, 0.63, 0.10, 0.15, BODY_Z, 0.60))
    P.append(box('sleeve', 'ceb2_hvy_sleeve', 0.10, 0.41, -0.10, 0.10, 0.49, 0.61))
    P.append(box('slot', 'ceb2_hvy_plate', 0.15, 0.30, -0.03, 0.03, 0.61, 0.622))
    P.append(cyl('collar', 'ceb2_hvy_collar', (0.40, 0, HVY_BORE0), (0.46, 0, HVY_BORE0), 0.10, 8, 0.5, r1=0.06))
    P.append(cyl('barrel', 'ceb2_hvy_barrel', (0.45, 0, HVY_BORE0), (0.82, 0, HVY_BORE0), 0.05, 8, 0.5))
    return up(P, MG_LIFT)


# ---------------------------------------------------------------- Blaster
BL_BORE0 = 0.54
BL_BORE = BL_BORE0 + MG_LIFT


def blaster_gun():
    P = []
    P.append(box('rear', 'ceb2_bl_band', -0.35, -0.30, -0.11, 0.11, 0.49, 0.61))
    P.append(prism('shell', 'ceb2_bl_shell', [(-0.30, -0.16), (0.00, -0.16), (0.10, -0.11), (0.10, 0.11), (0.00, 0.16), (-0.30, 0.16)], BODY_Z, 0.61))
    P.append(hump('cell', 'ceb2_bl_olive', -0.33, -0.01, -0.06, 0.10, 0.065, 0.61, 0.67))
    P.append(box('neck1', 'ceb2_bl_band', 0.10, 0.17, -0.12, 0.12, 0.49, 0.60))
    P.append(box('neck2', 'ceb2_bl_dark', 0.17, 0.22, -0.10, 0.10, 0.50, 0.59))
    P.append(box('housing', 'ceb2_bl_housing', 0.22, 0.52, -0.075, 0.075, 0.495, 0.585))
    for i, vx in enumerate((0.23, 0.33, 0.42)):
        P.append(box('vent%d' % i, 'ceb2_bl_vent', vx, vx + 0.07 if i < 2 else vx + 0.06, -0.025, 0.025, 0.585, 0.597))
    P.append(loft('taper', 'ceb2_bl_dark', 0.52, 0.075, 0.495, 0.585, 0.57, 0.05, 0.50, 0.58))
    P.append(box('tip', 'ceb2_bl_tip', 0.57, 0.66, -0.05, 0.05, 0.50, 0.58))
    return up(P, MG_LIFT)


# ---------------------------------------------------------------- output + checks
def open_edges(part):
    E = {}
    for a, b, c in part.tris:
        for e in ((a, b), (b, c), (c, a)):
            E[e] = E.get(e, 0) + 1
    return sum(1 for (a, b), n in E.items() if E.get((b, a), 0) != n)


def write(parts, name, header, pivot=0.0):
    lines = ['# IsoRim model: %s' % name, '# ' + header,
             '# Generated by tools/make_ce_b2.py. One unit = one map cell, +Y up, file -Z = game north (model faces north).']
    if pivot:
        lines.append('# Turret gun (topModel): origin = yaw pivot at the bore (topHeight %.2f), barrel along +X.' % pivot)
    lines.append('mtllib ' + LIB)
    vb = 1
    for i, p in enumerate(parts):
        lines.append('o %s_%s' % (name, p.name))
        lines.append('usemtl %s' % p.mat)
        for x, y, z in p.verts:
            lines.append('v %.4f %.4f %.4f' % (x, z - pivot, -y))
        for a, b, c in p.tris:
            lines.append('f %d %d %d' % (a + vb, b + vb, c + vb))
        vb += len(p.verts)
    with open(os.path.join(OUT, name + '.obj'), 'w', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')


def clearance(A, B):
    """Smallest conservative gap between parts A and B (negative = they overlap)."""
    g = min(B.sdf(p) for p in A.samples())
    return min(g, min(A.sdf(p) for p in B.samples()))


def yawed(part, deg):
    c, s = math.cos(math.radians(deg)), math.sin(math.radians(deg))
    return part.moved(lambda v: (v[0] * c - v[1] * s, v[0] * s + v[1] * c, v[2]))


def bbox(parts):
    vs = [v for p in parts for v in p.verts]
    return [[round(min(v[i] for v in vs), 4), round(max(v[i] for v in vs), 4)] for i in range(3)]


def near(A, B, reach):
    """Bounding spheres about the yaw axis: skip pairs that can never meet whatever the aim."""
    ra = [math.hypot(v[0], v[1]) for v in A.verts]; za = [v[2] for v in A.verts]
    rb = [math.hypot(v[0], v[1]) for v in B.verts]; zb = [v[2] for v in B.verts]
    return not (min(za) > max(zb) + reach or min(zb) > max(za) + reach or min(ra) > max(rb) + reach or min(rb) > max(ra) + reach)


TURRETS = [
    # key, thingDef, bore, gun()
    ('medium', 'Turret_Medium', MED_BORE, medium_gun),
    ('heavy', 'Turret_Heavy', HVY_BORE, heavy_gun),
    ('blaster', 'Turret_Blaster', BL_BORE, blaster_gun),
]


def main():
    with open(os.path.join(OUT, LIB), 'w', newline='\n') as f:
        f.write("# IsoRim Waylith - Combat Extended Pack, model batch 2: CE's auto-turrets. Fixed colours sampled from CE's\n"
                "# textures (lifted ~1.1x for the 3D shading, as batch 1); metal parts carry IsoRim's Metal swatch.\n")
        for n, (h, metal) in COLOURS.items():
            r, g, b = kd(h)
            f.write('newmtl %s\nKd %.3f %.3f %.3f\n' % (n, r, g, b))
            if metal:
                f.write('map_Kd IsoRim/Swatches/Metal\n')
    base = mgbase()
    write(base, 'ce_mgbase', "CE MachineGunBase (Turret_Medium / _Heavy / _Blaster base): IsoRim's turret_mini base re-aimed to CE's art")
    report = []
    boe = sum(open_edges(p) for p in base)
    for key, thing, bore, fg in TURRETS:
        gun = fg()
        write(gun, 'ce_%s_gun' % key, 'CE %s gun (topModel), CE\'s gun art points east' % thing, pivot=bore)
        r = {'name': key, 'thingDef': thing, 'size': 1, 'topHeight': bore, 'problems': []}
        for tag, parts in (('base', base), ('gun', gun)):
            oe = sum(open_edges(p) for p in parts)
            r[tag] = {'tris': sum(len(p.tris) for p in parts), 'open_edges': oe, 'bounds_xyz': bbox(parts)}
            if oe:
                r['problems'].append('%s: %d open edges' % (tag, oe))
        top = max(v[2] for p in base + gun for v in p.verts)
        r['top'] = round(top, 3)
        if top > STOREY:
            r['problems'].append('higher than the 1.60 storey: %.3f' % top)
        fx = max(v[0] for p in gun for v in p.verts)
        ends = [v for p in gun for v in p.verts if v[0] > fx - 1e-4]
        r['muzzle_gun_space'] = [round(fx, 3), round(sum(v[2] for v in ends) / len(ends) - bore, 3), round(sum(v[1] for v in ends) / len(ends), 3)]
        r['muzzle_part'] = [p.name for p in gun if any(v[0] > fx - 1e-4 for v in p.verts)]
        per = {}
        cand = [(g, b) for g in gun for b in base if near(g, b, 0.15)]
        for deg in range(0, 360, 5):
            for g, b in cand:
                c = clearance(yawed(g, deg), b)
                if (g.name, b.name) not in per or c < per[(g.name, b.name)][0]:
                    per[(g.name, b.name)] = (c, deg)
        ranked = sorted((c, deg, gn, bn) for (gn, bn), (c, deg) in per.items())
        worst = ranked[0] if ranked else (9.0, None, None, None)
        r['sweep_min_gap'] = {'gap': round(worst[0], 4), 'aim': worst[1], 'gun_part': worst[2], 'base_part': worst[3]} if worst[1] is not None else 'no part pair within reach'
        r['sweep_closest'] = ['%s/%s %.3f (aim %d)' % (gn, bn, c, deg) for c, deg, gn, bn in ranked[:4]]
        for c, deg, gn, bn in ranked:
            if c < GAP - 1e-6:
                r['problems'].append('gun meets base: gap %.4f at aim %d (%s / %s)' % (c, deg, gn, bn))
        r['pairs'] = {}
        report.append(r)
        print('%-7s base %4d tris (open %d), gun %4d tris, top %.3f, muzzle %s %s, sweep %s, problems %s' % (
            key, r['base']['tris'], boe, r['gun']['tris'], top, r['muzzle_gun_space'], r['muzzle_part'], r['sweep_closest'][:2], r['problems'] or 'none'))
    print('base bounds', bbox(base))
    if CHECKS:
        with open(CHECKS, 'w') as f:
            json.dump(report, f, indent=1)


main()
