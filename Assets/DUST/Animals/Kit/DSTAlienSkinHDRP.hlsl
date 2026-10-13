#ifndef DST_ALIEN_SKIN_HDRP_INCLUDED
#define DST_ALIEN_SKIN_HDRP_INCLUDED

// SG_DST_AlienSkin 的 Custom Function：把 DSTAlienSkin（GPT 交付，不改）接成 HDRP Lit 要的輸出。
// UV1 = rest XY、UV2 = (rest Z, 零件欄位)、頂點色 = 肚子 / 角質 / 花紋 / 虹彩；_SeedLo/_SeedHi = 物種 key 拆成兩個 16 位元。
#include "Assets/DUST/Animals/Kit/DSTAlienSkin.hlsl"

void DSTAlienSkinHDRP_float(
    float2 RestXY, float2 RestZKind, float4 Anatomy,
    float SeedLo, float SeedHi, float Detail,
    float3 PositionWS, float3 NormalWS,
    out float3 BaseColor, out float Metallic, out float Smoothness,
    out float Occlusion, out float CoatMask, out float IridescenceMask,
    out float IridescenceThickness, out float3 OutNormalWS)
{
    float film01, sheen, height;
    DSTAlienSkin_float(RestXY, RestZKind, Anatomy, SeedLo, SeedHi, Detail,
        BaseColor, Metallic, Smoothness, Occlusion, CoatMask,
        IridescenceMask, film01, sheen, height);
    // 交付包的厚度 0..1 = 0..1000 nm；HDRP Lit 的 1 = 3000 nm（BSDF.hlsl: Dinc = 3 * thickness）
    IridescenceThickness = film01 / 3.0;
    // Lit 沒有 sheen 層：毛多的物種在邊緣稍微亮一點代替
    float rim = pow(1.0 - saturate(abs(dot(normalize(NormalWS), normalize(-PositionWS)))), 3.0);
    BaseColor += BaseColor * sheen * rim;
    DSTAlienNormal_float(height, PositionWS, NormalWS, OutNormalWS);
}
#endif
