# IsoRim Waylith - Combat Extended Pack, model batch 1 (2026-10-30, Blender preview round 1): CE's manned turrets
# Turret_M240B, Turret_KPV, Turret_AGSThirty, Turret_Flak.
# Built from CE's own textures (Things/Building/Turrets/*_base, *_gun, FlakTurret_Base/_Top) and the approved analysis
# (ce_models_b1_analysis.md + the user's decisions of 2026-10-30):
#   - M240 bore 0.48 and AGS-30 bore 0.40 keep their real low heights; KPV on a ZGU-1 mount (flat legs, centre post),
#     bore 0.62; Flak bore 1.00, shield leaning back 20 degrees, rear part as the truncated cone the art shows;
#   - AGS drum (v2, user review): a flat plain disc like the real drum, axis sideways, d0.28 front-back (the art's 0.28),
#     0.11 thick, flat end faces, on the gun's right at the same mounting point;
#   - left out: M240 charging handle, AGS cocking lever and sight mount, KPV olive trim line, Flak muzzle band; the Flak
#     fuze-setter's 3 holes only as shallow dark recesses.
# IsoRim's turret conventions (IsoRim17, read only: Defs/Models.xml R3D_Turret_*, Patches.DrawTurret_Prefix,
# Projectiles.GunEnd):
#   - <model> = the base, turned with the building's rotation, built facing north as the mod's base art (unrotated);
#   - <topModel> = the gun, drawn at (DrawPos.x, topHeight, DrawPos.z) and turned to the turret's aim; built with the
#     barrel along +x (CE's gun art points east), its origin = the yaw pivot at the bore, so topHeight = bore height;
#   - the muzzle (shots, flashes) = the middle of the gun model's front-most face (largest x): the barrel tip here.
# Axes while building: x east (gun: forward), y north (gun: its left), z up; one unit = one cell; origin = footprint centre.
# OBJ: one unit = one cell, +Y up, file -Z = game north (as IsoRim's models). Every part is its own closed convex shell.
# Checks (written to --checks): closed shells, bounds, height under the 1.60 storey, footprint cells kept free, the gun
# swept round 360 degrees against the base (min gap 0.01), named clearance pairs inside a model (min gap 0.01), muzzle.
# The clearance test is conservative: a sample point's distance to a convex part is taken as the largest signed distance
# to its face planes (never more than the true distance).
# usage: <Blender>\5.2\python\bin\python.exe -I make_ce_b1.py <abs Models dir> [--checks <abs checks.json>]
import json, math, os, sys

OUT = sys.argv[1]
CHECKS = sys.argv[sys.argv.index('--checks') + 1] if '--checks' in sys.argv else None
os.makedirs(OUT, exist_ok=True)
LIB = 'ce_b1.mtl'
GAP = 0.01
STOREY = 1.60


def kd(hexcol, lift=1.1):
    h = hexcol.lstrip('#')
    return tuple(min(1.0, int(h[i:i + 2], 16) / 255.0 * lift) for i in (0, 2, 4))


# name: (hex sampled from CE's art, metal swatch?)
COLOURS = {
    'ceb1_black': ('#262626', True),       # M240 / KPV legs, M240 top cover
    'ceb1_grey': ('#4D4D4D', True),        # M240 head, receiver, stock; KPV hub, post
    'ceb1_m240_barrel': ('#565759', True),
    'ceb1_m240_hider': ('#4C4C4E', True),
    'ceb1_m240_ammo': ('#B39B59', False),
    'ceb1_kpv_feet': ('#535456', True),
    'ceb1_kpv_receiver': ('#404040', True),
    'ceb1_kpv_cradle': ('#454545', True),
    'ceb1_kpv_jacket': ('#3B3A3C', True),
    'ceb1_kpv_ammo': ('#9F8A4F', False),
    'ceb1_kpv_wood': ('#7D5931', False),
    'ceb1_kpv_muzzle': ('#484749', True),
    'ceb1_ags_leg': ('#666633', True),
    'ceb1_ags_olive': ('#808040', True),
    'ceb1_ags_rear': ('#333333', True),
    'ceb1_ags_front': ('#4D4D4D', True),
    'ceb1_ags_barrel': ('#3D3C3D', True),
    'ceb1_ags_drum': ('#73733A', True),
    'ceb1_ags_handle': ('#231F20', False),
    'ceb1_ags_sight': ('#434143', True),
    'ceb1_flak_light': ('#A49F99', True),  # gun body, shield, boxes, cone
    'ceb1_flak_high': ('#ABA8A4', True),   # upper-left box
    'ceb1_flak_barrel': ('#98938D', True), # barrel, cradle
    'ceb1_flak_dark': ('#383333', False),  # fuze-setter recesses
    'ceb1_flak_cap': ('#5F5F5F', True),    # trunnion caps
    'ceb1_flak_seat': ('#7F694F', False),
    'ceb1_flak_arm': ('#8C8883', True),
    'ceb1_flak_collar': ('#8A8681', True),
    'ceb1_flak_ped': ('#6A6763', True),    # pedestal, jacks, carriage under the gun
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


# ---------------------------------------------------------------- M240B
M240_BORE = 0.48


def m240_base():
    P = []
    P.append(cyl('head', 'ceb1_grey', (0, 0, 0.30), (0, 0, 0.37), 0.085))
    P.append(cyl('pintle', 'ceb1_grey', (0, 0, 0.37), (0, 0, 0.39), 0.03, 12))
    feet = [(0.0, 0.50), (-0.38, -0.41), (0.38, -0.41)]
    top = (0, 0, 0.335)
    for i, (fx, fy) in enumerate(feet):
        P.append(leg('leg%d' % i, 'ceb1_black', top, (fx, fy, 0.02), 0.025))
        P.append(cyl('foot%d' % i, 'ceb1_black', (fx, fy, 0.0), (fx, fy, 0.02), 0.045, 12))
    # rear crossbar (M122): between the rear legs at y -0.23
    t = 0.23 / 0.41
    z = top[2] + t * (0.02 - top[2]) + 0.01
    P.append(cyl('crossbar', 'ceb1_black', (-0.38 * t - 0.01, -0.23, z), (0.38 * t + 0.01, -0.23, z), 0.025, 8))
    return P


def m240_gun():
    P = []
    P.append(box('receiver', 'ceb1_grey', -0.37, 0.0, -0.045, 0.045, 0.40, 0.53))
    P.append(box('cover', 'ceb1_black', -0.37, 0.0, -0.045, 0.045, 0.53, 0.57))
    P.append(box('stock', 'ceb1_grey', -0.57, -0.37, -0.03, 0.03, 0.43, 0.53))
    P.append(box('beltbox', 'ceb1_m240_ammo', -0.13, 0.0, 0.045, 0.29, 0.42, 0.54))
    P.append(cyl('barrel', 'ceb1_m240_barrel', (-0.01, 0, M240_BORE), (0.56, 0, M240_BORE), 0.025, 12))
    P.append(cyl('hider', 'ceb1_m240_hider', (0.56, 0, M240_BORE), (0.72, 0, M240_BORE), 0.03, 12))
    return P


# ---------------------------------------------------------------- KPV
KPV_BORE = 0.62


def kpv_base():
    P = []
    P.append(cyl('hub', 'ceb1_grey', (0, 0, 0.0), (0, 0, 0.16), 0.10))
    P.append(cyl('post', 'ceb1_grey', (0, 0, 0.16), (0, 0, 0.47), 0.06))
    feet = [(0.0, 0.58), (-0.45, -0.25), (0.45, -0.25)]
    for i, (fx, fy) in enumerate(feet):
        P.append(leg('leg%d' % i, 'ceb1_black', (0, 0, 0.10), (fx, fy, 0.03), 0.025))
        P.append(cyl('foot%d' % i, 'ceb1_kpv_feet', (fx, fy, 0.0), (fx, fy, 0.03), 0.045, 12))
    return P


def kpv_gun():
    P = []
    P.append(cyl('pintle', 'ceb1_kpv_cradle', (0, 0, 0.48), (0, 0, 0.555), 0.06))
    P.append(box('receiver', 'ceb1_kpv_receiver', -0.60, 0.17, -0.07, 0.07, 0.55, 0.71))
    P.append(box('cradle', 'ceb1_kpv_cradle', -0.21, -0.04, -0.11, 0.11, 0.48, 0.70))
    P.append(box('grip_bar', 'ceb1_kpv_receiver', -0.645, -0.595, -0.075, 0.075, 0.60, 0.64))
    for s in (1, -1):
        P.append(cyl('knob%d' % s, 'ceb1_kpv_wood', (-0.645, 0.075 * s, 0.57), (-0.645, 0.075 * s, 0.69), 0.03, 12))
    P.append(box('ammo', 'ceb1_kpv_ammo', -0.21, -0.05, -0.33, -0.11, 0.50, 0.64))
    P.append(cyl('jacket', 'ceb1_kpv_jacket', (0.165, 0, KPV_BORE), (0.50, 0, KPV_BORE), 0.07, 16, r1=0.05))
    P.append(cyl('barrel', 'ceb1_kpv_cradle', (0.49, 0, KPV_BORE), (0.75, 0, KPV_BORE), 0.03, 12))
    P.append(cyl('muzzle', 'ceb1_kpv_muzzle', (0.75, 0, KPV_BORE), (0.97, 0, KPV_BORE), 0.0375, 12))
    return P


# ---------------------------------------------------------------- AGS-30
AGS_BORE = 0.40
DRUM_Z = 0.38   # drum centre height (v2): lowest that keeps 0.01 to the tripod legs at every aim


def ags_base():
    P = []
    P.append(cyl('head', 'ceb1_ags_olive', (0, 0, 0.24), (0, 0, 0.31), 0.09))
    P.append(cyl('post', 'ceb1_ags_olive', (0, 0, 0.31), (0, 0, 0.32), 0.03, 12))
    feet = [(0.0, 0.52), (-0.36, -0.41), (0.36, -0.41)]
    for i, (fx, fy) in enumerate(feet):
        P.append(leg('leg%d' % i, 'ceb1_ags_leg', (0, 0, 0.275), (fx, fy, 0.02), 0.025))
        P.append(cyl('foot%d' % i, 'ceb1_ags_olive', (fx, fy, 0.0), (fx, fy, 0.02), 0.04, 12))
    return P


def ags_gun():
    P = []
    P.append(box('rear', 'ceb1_ags_rear', -0.28, 0.06, -0.095, 0.095, 0.33, 0.48))
    P.append(box('front', 'ceb1_ags_front', 0.06, 0.25, -0.095, 0.095, 0.33, 0.48))
    a = [(0.25, -0.095, 0.33), (0.25, 0.095, 0.33), (0.25, 0.095, 0.48), (0.25, -0.095, 0.48)]
    b = [(0.37, -0.05, 0.36), (0.37, 0.05, 0.36), (0.37, 0.05, 0.44), (0.37, -0.05, 0.44)]
    P.append(hull8('taper', 'ceb1_ags_front', a, b))
    P.append(cyl('barrel', 'ceb1_ags_barrel', (0.36, 0, AGS_BORE), (0.66, 0, AGS_BORE), 0.03, 12))
    for s in (1, -1):
        P.append(cyl('handle%d' % s, 'ceb1_ags_handle', (-0.22, 0.09 * s, 0.42), (-0.22, 0.245 * s, 0.42), 0.03, 12))
    P.append(cyl('sight', 'ceb1_ags_sight', (-0.175, 0.124, 0.46), (-0.01, 0.124, 0.46), 0.03, 12))
    # drum magazine on the right (v2): a flat disc, axis sideways, d0.28, 0.11 thick, flat faces
    P.append(cyl('drum', 'ceb1_ags_drum', (0.16, -0.095, DRUM_Z), (0.16, -0.205, DRUM_Z), 0.14, 20))
    return P


# ---------------------------------------------------------------- Flak (3x3)
FLAK_BORE = 1.00
LEAN = math.radians(20.0)
SH_X0, SH_Z0, SH_T = 0.27, 0.62, 0.04          # shield front face bottom edge, thickness
SH_TOP = 1.40
SH_VTOP = (SH_TOP - SH_Z0) / math.cos(LEAN)


def shield_pt(u, v, w):
    """Shield plate coordinates: u across (gun's left +), v up the plate (leaning back), w into the plate (0 front)."""
    return (SH_X0 - v * math.sin(LEAN) - w * math.cos(LEAN), u, SH_Z0 + v * math.cos(LEAN) - w * math.sin(LEAN))


def shield_piece(name, poly):
    v = [shield_pt(u, vv, 0.0) for u, vv in poly] + [shield_pt(u, vv, SH_T) for u, vv in poly]
    n = len(poly)
    return Part(name, 'ceb1_flak_light', v, [tuple(range(n)), tuple(range(n, 2 * n))] + [(i, (i + 1) % n, (i + 1) % n + n, i + n) for i in range(n)])


def flak_base():
    P = []
    P.append(prism('collar', 'ceb1_flak_collar', octagon(0.69), 0.0, 0.25))
    P.append(prism('pedestal', 'ceb1_flak_ped', octagon(0.38), 0.25, 0.62))
    for k, (dx, dy) in enumerate(((0, 1), (1, 0), (0, -1), (-1, 0))):
        def rot(pts):
            # local (r along the arm, s across) -> world
            return [(r * dx - s * dy, r * dy + s * dx) for r, s in pts]
        P.append(prism('arm_in%d' % k, 'ceb1_flak_arm', rot([(0.33, -0.145), (0.77, -0.145), (0.77, 0.145), (0.33, 0.145)]), 0.0, 0.20))
        P.append(prism('arm_out%d' % k, 'ceb1_flak_arm', rot([(0.77, -0.115), (1.09, -0.115), (1.09, 0.115), (0.77, 0.115)]), 0.0, 0.16))
        P.append(prism('pad%d' % k, 'ceb1_flak_arm', rot([(1.09, -0.10), (1.29, -0.10), (1.36, 0.0), (1.29, 0.10), (1.09, 0.10)]), 0.0, 0.06))
        jx, jy = 1.22 * dx, 1.22 * dy
        P.append(cyl('jack%d' % k, 'ceb1_flak_ped', (jx, jy, 0.06), (jx, jy, 0.15), 0.04, 12))
    return P


def flak_gun():
    P = []
    B = FLAK_BORE
    P.append(box('carriage', 'ceb1_flak_ped', -0.50, 0.13, -0.30, 0.30, 0.63, 0.80))
    P.append(box('breech', 'ceb1_flak_light', -0.80, 0.27, -0.26, 0.26, 0.80, 1.20))
    P.append(cyl('cone', 'ceb1_flak_light', (-0.80, 0, B), (-1.93, 0, B), 0.20, 16, r1=0.11))
    P.append(box('upper_box', 'ceb1_flak_high', -1.02, -0.01, 0.26, 0.40, 0.95, 1.20))
    P.append(prism('fuze_box', 'ceb1_flak_light', [(-0.82, -0.56), (-0.28, -0.56), (-0.06, -0.26), (-0.82, -0.26)], 0.86, 1.16))
    for i, hx in enumerate((-0.69, -0.52, -0.36)):
        P.append(cyl('recess%d' % i, 'ceb1_flak_dark', (hx, -0.39, 1.142), (hx, -0.39, 1.163), 0.055, 12))
    P.append(box('seat_arm', 'ceb1_flak_ped', -0.30, -0.24, 0.30, 0.58, 0.64, 0.68))
    P.append(cyl('seat_post', 'ceb1_flak_ped', (-0.27, 0.55, 0.66), (-0.27, 0.55, 0.745), 0.025, 8))
    P.append(cyl('seat', 'ceb1_flak_seat', (-0.27, 0.55, 0.74), (-0.27, 0.55, 0.80), 0.235, 16))
    P.append(cyl('cradle', 'ceb1_flak_barrel', (0.27, 0, B), (0.88, 0, B), 0.24, 16))
    for s in (1, -1):
        P.append(cyl('trunnion%d' % s, 'ceb1_flak_cap', (0.605, 0.20 * s, B), (0.605, 0.31 * s, B), 0.08, 12))
    P.append(cyl('barrel', 'ceb1_flak_barrel', (0.80, 0, B), (1.92, 0, B), 0.125, 16))
    # shield: trapezoid (0.80 half-width at the foot, 0.95 at the top: the art's angled wing tips), a window for the breech
    hw = lambda v: 0.80 + 0.15 * v / SH_VTOP
    vb = (0.79 - SH_Z0) / math.cos(LEAN)                                         # window foot: front edge at z 0.79
    vt = (1.21 - SH_Z0 + SH_T * math.sin(LEAN)) / math.cos(LEAN)                 # window top: back edge at z 1.21
    for s, nm in ((1, 'shield_l'), (-1, 'shield_r')):
        poly = [(0.28 * s, 0.0), (hw(0) * s, 0.0), (hw(SH_VTOP) * s, SH_VTOP), (0.28 * s, SH_VTOP)]
        P.append(shield_piece(nm, poly if s > 0 else poly[::-1]))
    P.append(shield_piece('shield_low', [(-0.28, 0.0), (0.28, 0.0), (0.28, vb), (-0.28, vb)]))
    P.append(shield_piece('shield_top', [(-0.28, vt), (0.28, vt), (0.28, SH_VTOP), (-0.28, SH_VTOP)]))
    return P


# ---------------------------------------------------------------- output + checks
def open_edges(part):
    E = {}
    for a, b, c in part.tris:
        for e in ((a, b), (b, c), (c, a)):
            E[e] = E.get(e, 0) + 1
    return sum(1 for (a, b), n in E.items() if E.get((b, a), 0) != n)


def write(parts, name, header, pivot=0.0):
    lines = ['# IsoRim model: %s' % name, '# ' + header,
             '# Generated by tools/make_ce_b1.py. One unit = one map cell, +Y up, file -Z = game north (model faces north).']
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
    # key, thingDef, size, bore, base(), gun(), free cells (x0, y0, x1, y1), clearance pairs inside the gun
    ('m240b', 'Turret_M240B', 1, M240_BORE, m240_base, m240_gun, [], []),
    ('kpv', 'Turret_KPV', 1, KPV_BORE, kpv_base, kpv_gun, [], [('ammo', 'jacket')]),
    ('ags30', 'Turret_AGSThirty', 1, AGS_BORE, ags_base, ags_gun, [], [('drum', 'handle-1'), ('sight', 'handle1')]),
    ('flak', 'Turret_Flak', 3, FLAK_BORE, flak_base, flak_gun, [(-1.5, -1.5, -0.5, -0.5)],
     [(s, o) for s in ('shield_l', 'shield_r', 'shield_low', 'shield_top')
      for o in ('carriage', 'breech', 'cradle', 'barrel', 'upper_box', 'fuze_box', 'seat', 'seat_arm', 'trunnion1', 'trunnion-1')]),
]


def main():
    only = [a for a in sys.argv[2:] if not a.startswith('--') and a != CHECKS]
    with open(os.path.join(OUT, LIB), 'w', newline='\n') as f:
        f.write("# IsoRim Waylith - Combat Extended Pack, model batch 1: CE's manned turrets. Fixed colours sampled from CE's\n"
                "# textures (lifted ~1.1x for the 3D shading, as IsoRim's other building colours); metal parts carry IsoRim's Metal swatch.\n")
        for n, (h, metal) in COLOURS.items():
            r, g, b = kd(h)
            f.write('newmtl %s\nKd %.3f %.3f %.3f\n' % (n, r, g, b))
            if metal:
                f.write('map_Kd IsoRim/Swatches/Metal\n')
    report = []
    for key, thing, size, bore, fb, fg, free, pairs in TURRETS:
        if only and key not in only:
            continue
        base, gun = fb(), fg()
        write(base, 'ce_%s_base' % key, 'CE %s base (model), built facing north as CE\'s base art' % thing)
        write(gun, 'ce_%s_gun' % key, 'CE %s gun (topModel), CE\'s gun art points east' % thing, pivot=bore)
        r = {'name': key, 'thingDef': thing, 'size': size, 'topHeight': bore, 'problems': []}
        for tag, parts in (('base', base), ('gun', gun)):
            oe = sum(open_edges(p) for p in parts)
            r[tag] = {'tris': sum(len(p.tris) for p in parts), 'open_edges': oe, 'bounds_xyz': bbox(parts)}
            if oe:
                r['problems'].append('%s: %d open edges' % (tag, oe))
        top = max(v[2] for p in base + gun for v in p.verts)
        r['top'] = round(top, 3)
        if top > STOREY:
            r['problems'].append('higher than the 1.60 storey: %.3f' % top)
        # muzzle (IsoRim's GunEnd: the middle of the front-most face)
        fx = max(v[0] for p in gun for v in p.verts)
        ends = [v for p in gun for v in p.verts if v[0] > fx - 1e-4]
        r['muzzle_gun_space'] = [round(fx, 3), round(sum(v[2] for v in ends) / len(ends) - bore, 3), round(sum(v[1] for v in ends) / len(ends), 3)]
        # footprint cells kept free (base parts; the gun sweeps over them as CE's art does)
        for x0, y0, x1, y1 in free:
            bad = [p.name for p in base if any(x0 < v[0] < x1 and y0 < v[1] < y1 for v in p.verts)]
            r['free_cell_%g_%g' % (x0 + 0.5, y0 + 0.5)] = 'free' if not bad else bad
            if bad:
                r['problems'].append('interaction cell blocked by ' + ','.join(bad))
        # the gun swept round against the base
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
        byname = {p.name: p for p in gun}
        r['pairs'] = {}
        for a, b in pairs:
            c = clearance(byname[a], byname[b])
            r['pairs']['%s/%s' % (a, b)] = round(c, 4)
            if c < GAP - 1e-6:
                r['problems'].append('%s meets %s: gap %.4f' % (a, b, c))
        report.append(r)
        print('%-6s base %4d tris, gun %4d tris, top %.3f, muzzle %s, sweep %s, problems %s' % (
            key, r['base']['tris'], r['gun']['tris'], top, r['muzzle_gun_space'], r['sweep_closest'], r['problems'] or 'none'))
    if CHECKS:
        with open(CHECKS, 'w') as f:
            json.dump(report, f, indent=1)


main()
