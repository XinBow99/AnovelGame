#ifndef DST_ALIEN_SKIN_INCLUDED
#define DST_ALIEN_SKIN_INCLUDED

// Shader Graph Custom Function, File mode, Float precision, fragment stage.
// UV1.xy = original Blender rest XY; UV2.xy = rest Z, slot id.
// Vertex Color = belly, keratin, pattern coverage, iridescence coverage.
// SeedLo/SeedHi are numeric unsigned 16-bit halves, not float bit casts.

uint DSTSkinHash(uint x)
{
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    return x ^ (x >> 16);
}

float DSTSkinRandom(uint seed, uint stream)
{
    return (DSTSkinHash(seed ^ (stream * 0x9e3779b9u)) >> 8) * (1.0 / 16777216.0);
}

float DSTSkinLattice(int3 p, uint seed)
{
    return (DSTSkinHash(asuint(p.x) * 0x8da6b343u ^
                        asuint(p.y) * 0xd8163841u ^
                        asuint(p.z) * 0xcb1ab31fu ^ seed) >> 8) * (1.0 / 16777216.0);
}

float DSTSkinNoise(float3 p, uint seed)
{
    int3 cell = (int3)floor(p);
    float3 t = frac(p);
    t = t * t * (3.0 - 2.0 * t);
    float a = lerp(DSTSkinLattice(cell, seed), DSTSkinLattice(cell + int3(1,0,0), seed), t.x);
    float b = lerp(DSTSkinLattice(cell + int3(0,1,0), seed), DSTSkinLattice(cell + int3(1,1,0), seed), t.x);
    float c = lerp(DSTSkinLattice(cell + int3(0,0,1), seed), DSTSkinLattice(cell + int3(1,0,1), seed), t.x);
    float d = lerp(DSTSkinLattice(cell + int3(0,1,1), seed), DSTSkinLattice(cell + int3(1,1,1), seed), t.x);
    return lerp(lerp(a, b, t.y), lerp(c, d, t.y), t.z);
}

float2 DSTSkinCells(float3 p, uint seed)
{
    int3 cell = (int3)floor(p);
    float3 f = frac(p);
    float nearest = 10.0, second = 10.0;
    [unroll] for (int z = -1; z <= 1; ++z)
    [unroll] for (int y = -1; y <= 1; ++y)
    [unroll] for (int x = -1; x <= 1; ++x)
    {
        int3 offset = int3(x, y, z);
        int3 key = cell + offset;
        float3 jitter = float3(DSTSkinLattice(key, seed),
                              DSTSkinLattice(key, seed ^ 0xa511e9b3u),
                              DSTSkinLattice(key, seed ^ 0x63d83595u));
        float3 delta = (float3)offset + 0.2 + 0.6 * jitter - f;
        float distance2 = dot(delta, delta);
        second = max(nearest, min(second, distance2));
        nearest = min(nearest, distance2);
    }
    return sqrt(float2(nearest, second));
}

float3 DSTSkinLinear(float3 srgb)
{
    return lerp(srgb / 12.92, pow(max((srgb + 0.055) / 1.055, 0.0), 2.4),
                step(0.04045, srgb));
}

void DSTAlienSkin_float(
    float2 RestXY, float2 RestZKind, float4 Anatomy,
    float SeedLo, float SeedHi, float Detail,
    out float3 BaseColor, out float Metallic, out float Smoothness,
    out float Occlusion, out float CoatMask, out float IridescenceMask,
    out float FilmThickness01, out float Sheen, out float Height)
{
    uint seed = ((uint)clamp(round(SeedLo), 0.0, 65535.0)) |
                (((uint)clamp(round(SeedHi), 0.0, 65535.0)) << 16);
    // This domain is independent of the species/ecology random stream.
    seed = DSTSkinHash(seed ^ 0x44555354u);
    uint family = DSTSkinHash(seed ^ 0x06a09e67u) % 6u;
    uint pattern = DSTSkinHash(seed ^ 0xbb67ae85u) % 6u;
    float3 p = float3(RestXY, RestZKind.x);
    uint slot = (uint)round(RestZKind.y);
    float4 masks = saturate(Anatomy);
    float3 base, belly, accent, dark;
    float rough, coat, film, fur;
    if (family == 0u)
    {
        base=float3(.035,.19,.40); belly=float3(.15,.43,.48);
        accent=float3(.12,.83,.73); dark=float3(.015,.045,.11);
        rough=.43; coat=.28; film=430; fur=.8;
    }
    else if (family == 1u)
    {
        base=float3(.145,.045,.25); belly=float3(.42,.23,.48);
        accent=float3(.69,.28,.71); dark=float3(.025,.012,.055);
        rough=.34; coat=.42; film=570; fur=.12;
    }
    else if (family == 2u)
    {
        base=float3(.48,.105,.055); belly=float3(.72,.37,.15);
        accent=float3(.19,.052,.022); dark=float3(.085,.024,.018);
        rough=.69; coat=.08; film=120; fur=.18;
    }
    else if (family == 3u)
    {
        base=float3(.63,.69,.72); belly=float3(.78,.72,.66);
        accent=float3(.24,.50,.68); dark=float3(.20,.28,.35);
        rough=.31; coat=.38; film=360; fur=.30;
    }
    else if (family == 4u)
    {
        base=float3(.018,.22,.155); belly=float3(.18,.34,.25);
        accent=float3(.12,.52,.37); dark=float3(.009,.055,.043);
        rough=.48; coat=.48; film=510; fur=.05;
    }
    else
    {
        base=float3(.055,.22,.62); belly=float3(.14,.25,.50);
        accent=float3(.91,.23,.25); dark=float3(.009,.045,.30);
        rough=.48; coat=.23; film=640; fur=.48;
    }
    float3 tint = lerp(0.83, 1.15, float3(DSTSkinRandom(seed,1u),
                         DSTSkinRandom(seed,2u), DSTSkinRandom(seed,3u)));
    base=DSTSkinLinear(saturate(base*tint));
    belly=DSTSkinLinear(saturate(belly*tint));
    accent=DSTSkinLinear(saturate(accent*tint));
    dark=DSTSkinLinear(saturate(dark*tint));
    rough = clamp(rough + (DSTSkinRandom(seed,4u)-.5)*.11, .2, .88);
    coat = saturate(coat + (DSTSkinRandom(seed,5u)-.5)*.16);
    film += (DSTSkinRandom(seed,6u)-.5)*70.0;
    float frequency = lerp(34.0, 65.0, DSTSkinRandom(seed,7u));
    float3 offset = float3(DSTSkinRandom(seed,8u),DSTSkinRandom(seed,9u),
                           DSTSkinRandom(seed,10u))*32.0;
    float3 q = p*frequency + offset;
    float warp = DSTSkinNoise(q*.38, seed);
    q += (warp-.5)*.8;
    float density = .15 + .65*warp;
    float motif = 0.0;
    // Cull expensive cell patterns as they become subpixel.
    float footprint = max(length(ddx(q)), length(ddy(q)));
    float detailFade = saturate(Detail) * (1.0-smoothstep(.35, 1.2, footprint));
    if (detailFade > .001 && masks.b > .001)
    {
        float aa = max(.025, footprint*.65);
        if (pattern == 0u)
        {
            float band = sin(q.y*2.8 + warp*5.0)*.5+.5;
            motif = smoothstep(.78-aa, .9+aa, band);
        }
        else if (pattern == 3u)
        {
            motif = smoothstep(.5-aa, .67+aa, DSTSkinNoise(q*.8, seed ^ 71u));
        }
        else
        {
            float2 cells = DSTSkinCells(q, seed);
            if (pattern == 1u)
                motif = smoothstep(.24-aa,.34+aa,cells.x) *
                        (1.0-smoothstep(.36-aa,.49+aa,cells.x));
            else if (pattern == 5u)
                motif = 1.0-smoothstep(.21-aa,.39+aa,cells.x);
            else
                motif = 1.0-smoothstep(.025-aa,.085+aa,cells.y-cells.x);
        }
    }
    BaseColor = lerp(base, belly, masks.r);
    if (slot == 0u)
        BaseColor = lerp(BaseColor,dark,smoothstep(.365,.428,p.z)*.65);
    if (slot == 3u)
        BaseColor = lerp(BaseColor,dark,(1.0-smoothstep(.16,.23,p.z))*.8);
    if (slot == 4u)
        BaseColor = lerp(BaseColor,dark,.75);
    BaseColor = lerp(BaseColor,accent,motif*density*masks.b*detailFade);
    BaseColor = lerp(BaseColor,dark,masks.g*.95);
    float dust = slot == 0u ? (1.0-smoothstep(.035,.13,p.z)) *
                 lerp(.045,.15,DSTSkinRandom(seed,11u)) : 0.0;
    BaseColor = lerp(BaseColor,DSTSkinLinear(float3(.59,.45,.30)),dust);
    rough = lerp(rough,.34,masks.g)+dust*.5;
    float flow = slot == 3u ? 1.0 : (slot == 0u ? 1.0-smoothstep(.21,.28,p.z) : 0.0);
    float3 hairP = p*lerp(float3(1250,150,1250),float3(1250,1250,150),flow)+offset;
    float microFade = detailFade*(1.0-smoothstep(.35,1.0,
                          max(length(ddx(hairP)),length(ddy(hairP)))));
    float micro = 0.5;
    if (microFade > .001)
        micro = DSTSkinNoise(hairP, seed ^ 113u);
    BaseColor *= 1.0+(micro-.5)*.25*microFade;
    Height = ((micro-.5)*.00018*fur + (motif-.5)*.00008*(1.0-fur))
             *detailFade*(1.0-masks.g);
    Metallic = 0.0;
    Smoothness = saturate(1.0-rough-(micro-.5)*.08*microFade);
    Occlusion = 1.0; // Use HDRP SSAO; no invented baked-occlusion texture.
    CoatMask = coat*(1.0-dust);
    IridescenceMask = masks.a;
    FilmThickness01 = saturate(film/1000.0);
    Sheen = fur*.28*(1.0-masks.g);
    if (slot == 5u)
    {
        float r = length(float2((p.y+.32625)/.0042,(p.z-.446875)/.0034));
        BaseColor = lerp(DSTSkinLinear(float3(.010,.012,.015)),
                        DSTSkinLinear(float3(.33,.18,.035)),smoothstep(.28,.52,r));
        BaseColor = lerp(BaseColor,DSTSkinLinear(float3(.025,.022,.015)),smoothstep(.75,1.1,r));
        Smoothness=.87; CoatMask=.5; IridescenceMask=0.0; Sheen=0.0; Height=0.0;
    }
}

// Optional second Custom Function. Wire world-space normal to a graph whose
// fragment Normal block uses World space, or Transform World -> Tangent.
void DSTAlienNormal_float(float Height, float3 PositionWS, float3 NormalWS, out float3 OutNormalWS)
{
    float3 n = normalize(NormalWS);
    float3 dx = ddx(PositionWS), dy = ddy(PositionWS);
    float3 r1 = cross(dy,n), r2 = cross(n,dx);
    float det = dot(dx,r1);
    float3 gradient = (ddx(Height)*r1 + ddy(Height)*r2) *
                      sign(det)/max(abs(det),1e-12);
    OutNormalWS = normalize(n-gradient);
}
#endif
