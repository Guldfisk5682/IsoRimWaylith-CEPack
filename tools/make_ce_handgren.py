# IsoRim Waylith - Combat Extended Pack: CE's hand-thrown canister grenades (fix M20, 2026-10-31, Blender preview round 1).
# CE_Weapon_GrenadeSmoke (the user's finding), and the same canister family: CE_Weapon_GrenadeConcussion (same shape as
# the smoke grenade, other colours), CE_Weapon_GrenadeFlashbang (M84: cap, two rims, blue middle), CE_Weapon_GrenadeFirefoam
# (wide red can). Without these they fall back to IsoRim's Grenades-category model (the vanilla frag grenade.obj).
# Built as IsoRim's own grenade weapons (IsoRim Models/grenade_tox.obj): held size, standing, the body axis at the origin,
# bottom at y -0.07 (the hand), drawn with <layFlat> (tipped onto its side on the ground) and <rotation>-18 like
# R3D_Weapon_Grenade*. One model per grenade (the vanilla grenade entries have no stack models).
# Shapes and colours from CE's own art (Things/Ammo/HandGrenades/<Type>/<Type>_a.png, 128 px a cell), read at 0.0025 cells
# a pixel (the smoke grenade comes out the size of IsoRim's grenade_tox). Left out (too small at play size): the pull ring,
# the flashbang's holes, the firefoam flame icon, the fuze outline. Kept: body, bands, fuze neck + head, the spoon (lever)
# down the side (the art's left = -x).
# Game axes while building: x east, y up, z north, one cell per unit. The OBJ stores z flipped (file z = -game z). Every part
# is its own closed shell, triangulated. Checks: closed shells, spoon clear of the body (min gap 0.002 at this size).
# Stack models b, c: see the stack section below.
# usage: <Blender>\5.2\python\bin\python.exe -I make_ce_handgren.py <abs Models dir> [<abs report.json>]
import json, math, os, sys

OUT = sys.argv[1]
REPORT = sys.argv[2] if len(sys.argv) > 2 else None
os.makedirs(OUT, exist_ok=True)
PX = 0.0025          # cells per art pixel
BOTTOM = -0.07       # body bottom (as grenade_tox)
LIFT = 1.08
LIB = 'ce_handgren.mtl'
N = 12               # sides of the round parts


def lathe(profile, n=N):
    """profile = [(y, r), ...] bottom to top; r = 0 closes with a point. Returns (verts, tris) of a closed shell."""
    v, rings = [], []
    for y, r in profile:
        if r <= 1e-9:
            rings.append([len(v)]); v.append((0.0, y, 0.0)); continue
        ring = []
        for i in range(n):
            a = 2 * math.pi * (i + 0.5) / n
            ring.append(len(v)); v.append((r * math.cos(a), y, r * math.sin(a)))
        rings.append(ring)
    t = []
    def cap(ring, up):
        c = len(v); v.append((0.0, v[ring[0]][1], 0.0))
        for i in range(len(ring)):
            a, b = ring[i], ring[(i + 1) % len(ring)]
            t.append((c, b, a) if up else (c, a, b))
    if len(rings[0]) > 1:
        cap(rings[0], False)
    if len(rings[-1]) > 1:
        cap(rings[-1], True)
    for A, B in zip(rings, rings[1:]):
        for i in range(n):
            if len(A) == 1:
                t.append((A[0], B[(i + 1) % n], B[i]))
            elif len(B) == 1:
                t.append((A[i], A[(i + 1) % n], B[0]))
            else:
                t += [(A[i], A[(i + 1) % n], B[(i + 1) % n]), (A[i], B[(i + 1) % n], B[i])]
    return v, t


def box(x0, x1, y0, y1, z0, z1):
    v = [(x, y, z) for y in (y0, y1) for z in (z0, z1) for x in (x0, x1)]
    q = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    return v, [t for a, b, c, d in q for t in ((a, b, c), (a, c, d))]


def orient(v, t):
    """Make every triangle face away from the shell's centre (all parts are convex)."""
    c = [sum(p[i] for p in v) / len(v) for i in range(3)]
    out = []
    for a, b, cc in t:
        A, B, C = v[a], v[b], v[cc]
        n = [(B[1] - A[1]) * (C[2] - A[2]) - (B[2] - A[2]) * (C[1] - A[1]),
             (B[2] - A[2]) * (C[0] - A[0]) - (B[0] - A[0]) * (C[2] - A[2]),
             (B[0] - A[0]) * (C[1] - A[1]) - (B[1] - A[1]) * (C[0] - A[0])]
        m = [(A[i] + B[i] + C[i]) / 3 - c[i] for i in range(3)]
        out.append((a, b, cc) if sum(n[i] * m[i] for i in range(3)) >= 0 else (a, cc, b))
    return out


def open_edges(t):
    E = {}
    for a, b, c in t:
        for e in ((a, b), (b, c), (c, a)):
            E[e] = E.get(e, 0) + 1
    return sum(1 for (a, b), k in E.items() if E.get((b, a), 0) != k)


def y(px_from_bottom):
    return BOTTOM + px_from_bottom * PX


def r(px_across):
    return px_across * PX / 2


# ---------------------------------------------------------------- shapes (art pixels: y counted from the body bottom)
def canister(spec):
    """Smoke / concussion: olive (black) body, two bands, grey neck + fuze head, spoon on -x."""
    P = []
    bot, top, d = 101, 49, 29                      # art rows of the body bottom / top, body width
    H = lambda row: y(bot - row)
    R = r(d)
    P.append(('body', lathe([(H(bot), R - 0.004), (H(bot - 2), R), (H(top), R)])))
    for i, (r0, r1) in enumerate(((62.5, 68.0), (71.5, 77.0))):
        P.append(('band%d' % i, lathe([(H(r1), R + 0.0015), (H(r0), R + 0.0015)])))
    P.append(('neck', lathe([(H(top), r(11)), (H(43.5), r(11))])))
    P.append(('head', lathe([(H(43.5), r(17)), (H(35.0), r(17))])))
    P.append(('spoon', box(-R - 0.0085, -R - 0.0035, H(93), H(37), -0.006, 0.006)))
    return P


def flashbang(spec):
    P = []
    bot = 96
    H = lambda row: y(bot - row)
    P.append(('rim_low', lathe([(H(96), r(27) - 0.003), (H(94), r(27)), (H(84), r(27))])))
    P.append(('core', lathe([(H(84), r(20)), (H(58), r(20))])))
    P.append(('rim_up', lathe([(H(58), r(27)), (H(46), r(27))])))
    P.append(('cap', lathe([(H(46), r(21)), (H(35), r(21)), (H(33), r(21) - 0.003)])))
    P.append(('spoon', box(-r(27) - 0.0085, -r(27) - 0.0035, H(83), H(36), -0.006, 0.006)))
    return P


def firefoam(spec):
    P = []
    bot, top, d = 104, 40, 37
    H = lambda row: y(bot - row)
    R = r(d)
    P.append(('body', lathe([(H(bot), R - 0.006), (H(bot - 3), R), (H(top + 6), R), (H(top + 2), R - 0.007), (H(top), R - 0.016)])))
    for i, (r0, r1) in enumerate(((47.0, 53.0), (89.0, 96.5))):
        P.append(('band%d' % i, lathe([(H(r1), R + 0.0015), (H(r0), R + 0.0015)])))
    P.append(('fuze', lathe([(H(top), r(10)), (H(34.5), r(10))])))
    P.append(('spoon', box(-R - 0.0085, -R - 0.0035, H(84), H(42), -0.006, 0.006)))
    return P


TYPES = [  # key, thingDef, CE texture, shape, colours (hex from CE's art), metal parts
    ('smoke', 'CE_Weapon_GrenadeSmoke', 'Smoke', canister,
     dict(body='#585C4A', band='#FFF9AA', neck='#ADAEB0', head='#949694', spoon='#2A2A2A', crate='#5A4F48', lid='#8A796D')),
    ('concussion', 'CE_Weapon_GrenadeConcussion', 'Concussion', canister,
     dict(body='#333333', band='#E3D932', neck='#909192', head='#808080', spoon='#2A2A2A', crate='#4C4830', lid='#66613D')),
    ('flashbang', 'CE_Weapon_GrenadeFlashbang', 'Flashbang', flashbang,
     dict(rim='#6A7356', core='#285F97', cap='#6A7356', spoon='#2A2A2A', crate='#6F786F', lid='#98A298')),
    ('firefoam', 'CE_Weapon_GrenadeFirefoam', 'Firefoam', firefoam,
     dict(body='#A32F2F', band='#C9C9CA', fuze='#A7A7A7', spoon='#2A2A2A', crate='#A82222', lid='#D03030', trim='#D8D8D8')),
]
METAL = ('neck', 'head', 'spoon', 'fuze')


def colour_of(part, cols):
    for k in sorted(cols, key=len, reverse=True):
        if part.startswith(k):
            return k
    raise KeyError(part)


def kd(h):
    h = h.lstrip('#')
    return tuple(min(1.0, int(h[i:i + 2], 16) / 255.0 * LIFT) for i in (0, 2, 4))



# ---------------------------------------------------------------- stack models (b, c), as CE's Graphic_StackCount art
# b = three grenades standing (two behind, one in front); c = the crate with two grenades standing before its front-left
# (CE's _c art: crate ~2.9 grenade heights wide, ~1.5 high; the lid as a slab with a small overhang; stencils and hazard
# icons left out; the firefoam crate keeps its white corner strips). Sized to the held-size grenade (one world scale).
# IsoRim draws the whole entry with <layFlat> (tipped 90 degrees about x: up -> north, lifted by the model's half depth)
# and <rotation>-18, stack models too. So b and c are stored pre-turned: the final standing geometry G (y up from 0) is
# turned back by the -18 degrees and the tip undone, v = (x, z, H/2 - y) with H the height of G, so that IsoRim's tip
# and lift stand them upright on the ground, square to the cell.
ROT = -18.0          # the entry's <rotation> (RimWorld degrees, clockwise from above)


def moved(vt, dx=0.0, dz=0.0, yaw=0.0):
    """Turned clockwise from above by yaw degrees (RimWorld), then moved."""
    v, t = vt
    c, s = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    return [(x * c + z * s + dx, y, -x * s + z * c + dz) for x, y, z in v], t


def lifted(parts):
    """Parts of a standing held-size grenade (bottom at BOTTOM) moved so the bottom is at y 0."""
    return [(n, ([(x, y - BOTTOM, z) for x, y, z in v], t)) for n, (v, t) in parts]


def radius(parts):
    return max(math.hypot(p[0], p[2]) for n_, (v, t) in parts if n_ != 'spoon' for p in v)


def stack_b(parts):
    g = lifted(parts)
    Rb = radius(parts)
    dx = Rb + 0.006                                   # back pair: a spoon stays clear of the neighbour
    dz = math.sqrt(max(0.0, (2 * Rb + 0.003) ** 2 - dx * dx))
    out = []
    for k, (x, z) in enumerate(((-dx, dz / 2), (dx, dz / 2), (0.0, -dz / 2))):
        out += [('g%d_%s' % (k, n), moved(vt, x, z)) for n, vt in g]
    return out


def stack_c(parts, cols):
    g = lifted(parts)
    Rb = radius(parts)
    H = max(p[1] for n, (v, t) in g for p in v)       # grenade height
    W, D, Hc = 2.9 * H, 1.6 * H, 1.5 * H              # crate
    cx, cz = 0.06, 0.08
    out = [('crate', box(cx - W / 2, cx + W / 2, 0.0, Hc - 0.02, cz - D / 2, cz + D / 2)),
           ('lid', box(cx - W / 2 - 0.008, cx + W / 2 + 0.008, Hc - 0.02, Hc, cz - D / 2 - 0.008, cz + D / 2 + 0.008))]
    if 'trim' in cols:                                # firefoam: white strips up the front corners, on the front face
        for k, (x0, x1) in enumerate(((cx - W / 2, cx - W / 2 + 0.025), (cx + W / 2 - 0.025, cx + W / 2))):
            out.append(('trim%d' % k, box(x0, x1, 0.0, Hc - 0.02, cz - D / 2 - 0.003, cz - D / 2)))
    fz = cz - D / 2 - Rb - 0.014                      # grenades before the front face
    step = 2 * Rb + 0.0085 + 0.004
    x0 = cx - W / 2 + Rb + 0.03
    for k, x in enumerate((x0, x0 + step)):
        out += [('g%d_%s' % (k, n), moved(vt, x, fz)) for n, vt in g]
    return out


def pre_turned(parts):
    """Final standing geometry -> stored geometry (undo the entry's rotation and layFlat; see above)."""
    H = max(p[1] for n, (v, t) in parts for p in v)
    out = []
    for n, (v, t) in parts:
        v2, t2 = moved((v, t), yaw=-ROT)               # turned back by the entry's rotation
        out.append((n, ([(x, z, H / 2 - y) for x, y, z in v2], t2)))
    return out


def part_colour(pname, cols):
    base = pname.split('_', 1)[1] if pname.startswith('g') and '_' in pname else pname
    return colour_of(base, cols)


def write_model(name, key, thing, tex, parts, cols, note):
    lines = ['# IsoRim model: %s' % name,
             '# %s (CE art Things/Ammo/HandGrenades/%s). Generated by tools/make_ce_handgren.py.' % (thing, tex),
             '# One unit = one map cell, +Y up, -Z = north. ' + note,
             'mtllib ' + LIB]
    vb, oe = 1, 0
    for pname, (v, t) in parts:
        t = orient(v, t)
        oe += open_edges(t)
        lines.append('o %s_%s' % (name, pname))
        lines.append('usemtl hg_%s_%s' % (key, part_colour(pname, cols)))
        lines += ['v %.4f %.4f %.4f' % (x, yy, -z) for x, yy, z in v]
        lines += ['f %d %d %d' % (a + vb, b + vb, c + vb) for a, b, c in t]
        vb += len(v)
    with open(os.path.join(OUT, name + '.obj'), 'w', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    return vb - 1, oe


def piece_gaps(parts):
    """Top-view gaps between the pieces of a stack (each grenade, the crate with lid and trim): round parts as circles
    about their axis, boxes as boxes. Conservative for these upright shapes."""
    groups = {}
    for n, (v, t) in parts:
        groups.setdefault(n.split('_', 1)[0] if n.startswith('g') else 'crate', []).append((n, v))

    def outline(n, v):
        xs = [p[0] for p in v]; zs = [p[2] for p in v]
        if n.endswith('spoon') or not n.startswith('g'):
            return ('box', min(xs), max(xs), min(zs), max(zs))
        cx, cz = (min(xs) + max(xs)) / 2, (min(zs) + max(zs)) / 2
        return ('circle', cx, cz, max(math.hypot(p[0] - cx, p[2] - cz) for p in v))

    def gap(a, b):
        if a[0] == 'circle' and b[0] == 'circle':
            return math.hypot(a[1] - b[1], a[2] - b[2]) - a[3] - b[3]
        if a[0] == 'circle':
            a, b = b, a
        if b[0] == 'circle':   # box a, circle b
            dx = max(a[1] - b[1], 0.0, b[1] - a[2]); dz = max(a[3] - b[2], 0.0, b[2] - a[4])
            return math.hypot(dx, dz) - b[3]
        gx = max(b[1] - a[2], a[1] - b[2]); gz = max(b[3] - a[4], a[3] - b[4])
        return math.hypot(gx, gz) if gx > 0 and gz > 0 else max(gx, gz)

    out = {}
    keys = sorted(groups)
    for i in range(len(keys)):
        for j in range(i + 1, len(keys)):
            out['%s/%s' % (keys[i], keys[j])] = round(min(gap(outline(*pa), outline(*pb)) for pa in groups[keys[i]] for pb in groups[keys[j]]), 4)
    return out


def main():
    report = []
    mtl = ["# IsoRim Waylith - Combat Extended Pack: CE's canister hand grenades (tools/make_ce_handgren.py). Fixed colours",
           "# sampled from CE's textures (lifted ~1.08x); fuze and spoon parts carry IsoRim's Metal swatch."]
    for key, thing, tex, fn, cols in TYPES:
        for c, h in cols.items():
            r_, g_, b_ = kd(h)
            mtl.append('newmtl hg_%s_%s\nKd %.3f %.3f %.3f' % (key, c, r_, g_, b_))
            if c in METAL:
                mtl.append('map_Kd IsoRim/Swatches/Metal')
        parts = fn(None)
        nv, oe = write_model('ce_handgren_%s' % key, key, thing, tex, parts, cols,
                             'Stack a: held size, standing, body axis at the origin (as grenade_tox); laid by <layFlat>.')
        R = radius(parts)
        sg = -R - max(p[0] for p in dict(parts)['spoon'][0])
        r_ = {'key': key, 'thingDef': thing, 'a': {'verts': nv, 'open_edges': oe, 'spoon_gap': round(sg, 4)}, 'problems': []}
        if oe:
            r_['problems'].append('a: open edges %d' % oe)
        if sg < 0.002 - 1e-6:
            r_['problems'].append('a: spoon meets body')
        for tag, G in (('b', stack_b(parts)), ('c', stack_c(parts, cols))):
            gaps = piece_gaps(G)
            Gb = [[round(min(p[i] for n, (v, t) in G for p in v), 3), round(max(p[i] for n, (v, t) in G for p in v), 3)] for i in range(3)]
            nv, oe = write_model('ce_handgren_%s_%s' % (key, tag), key, thing, tex, pre_turned(G), cols,
                                 'Stack %s, stored pre-turned for <layFlat> + <rotation>-18 (stands upright, square to the cell, once laid).' % tag)
            mg = min(gaps.values())
            r_[tag] = {'verts': nv, 'open_edges': oe, 'standing_bounds_xyz': Gb, 'min_gap_between_pieces': mg, 'gaps': gaps}
            if oe:
                r_['problems'].append('%s: open edges %d' % (tag, oe))
            if mg < 0.002 - 1e-6:
                r_['problems'].append('%s: pieces meet (%s)' % (tag, mg))
            if max(Gb[0][1] - Gb[0][0], Gb[2][1] - Gb[2][0]) > 0.92:
                r_['problems'].append('%s: longer than 0.92 cells' % tag)
        report.append(r_)
        print(key, json.dumps(r_))
    with open(os.path.join(OUT, LIB), 'w', newline='\n') as f:
        f.write('\n'.join(mtl) + '\n')
    if REPORT:
        json.dump(report, open(REPORT, 'w'), indent=1)


main()
