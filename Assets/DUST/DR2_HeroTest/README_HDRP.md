# DR2 樣板牆（Hero Test）— Unity HDRP 設定

目的：先只做一面 L 形轉角牆，做到最終品質，在 HDRP 驗證效果和效能，通過後再套用到全部模組。

## 內容

| 資料夾 | 內容 |
|---|---|
| `Meshes/` | 每個模組一個 FBX（`SM_DST_*_Hero.fbx`），pivot 和 Blender 一樣（牆：左下後角；道具：底部中心） |
| `DR2_HeroTest_Scene.fbx` | 整組 L 形轉角擺好的樣子，拿來快速做 lookdev 用 |
| `Textures/` | 每套材質 4 張：`_BaseColor`、`_Normal`（OpenGL）、`_MaskMap`、`_Height` |
| `HDRI/HDRI_DST_Goegap_4k.hdr` | 沙漠正午 HDRI（Poly Haven「goegap」，CC0） |

貼圖來源都是 Poly Haven（CC0，可商用）：`sandstone_blocks_08`、`sandstone_cracks`、`dense_sand`、`weathered_brown_planks`。

## 1. 匯入設定

| 檔案 | 設定 |
|---|---|
| FBX | Scale Factor 1。牆不需要 Lightmap UV（用 APV 的話）；要烘焙 Lightmap 就勾 **Generate Lightmap UVs** |
| `_BaseColor` | sRGB **開** |
| `_Normal` | Texture Type = **Normal map** |
| `_MaskMap`、`_Height` | sRGB **關** |
| HDRI | Texture Shape = **Cube**，Mapping = **Latitude-Longitude Layout** |

MaskMap 已經照 HDRP 規格打包好：R = 金屬度（0）、G = AO、B = 細節遮罩（1）、A = 光滑度（= 1 − Roughness）。

## 2. 材質（HDRP/Lit）

| 材質 | 貼圖組 | UV 對應 | 實際尺寸 | 位移 |
|---|---|---|---|---|
| `M_DST_Sandstone` | Sandstone_Blocks | **Triplanar，世界座標** | 4 m 一張（512 px/m） | Pixel Displacement，振幅約 0.04 m |
| `M_DST_Trim` | Trim_Sandstone | Triplanar，世界座標 | 2 m 一張 | Pixel Displacement，約 0.02 m |
| `M_DST_Ground_Sand` | Sand_Dense | Planar，世界座標 | 1.8 m 一張 | Pixel Displacement，約 0.02 m |
| `M_DST_Wood` | Wood_Weathered | UV0（Tiling 約 0.55） | 1.8 m | 不需要 |

- **牆和飾帶一定要用世界座標的 Triplanar（或 Planar）**。模組長度有 0.5 / 1 / 2 / 4 m，用 UV0 的話接縫處磚縫會錯開；世界座標投影不管怎麼拼都無縫。
- 飾帶在 Blender 裡有調色：飽和度約 ×0.85、明度約 ×0.95，讓它接近牆面的石色。在 Unity 用 Base Color 乘一個略帶灰的顏色就可以達到同樣效果。

## 3. 光照與 Volume（從這組數值開始調）

**Directional Light（太陽）**
- Intensity：**100,000 lux**（晴天正午），Angular Diameter 0.53
- 仰角：**46.5°**（實際從這張 HDRI 量到的太陽高度）
- 方位：先把 Rotation X 設成 46.5，再轉 HDRI Sky 的 **Rotation**，直到天空裡的太陽跟光的方向對齊（在 Scene 視窗直接看太陽的位置）
- 構圖建議：讓陽光**斜掃過主要牆面**（跟牆面夾 20–40°），石材的凹凸和倒角高光才會出來

**Volume overrides**
| Override | 建議值 |
|---|---|
| Visual Environment | Sky Type = HDRI Sky |
| HDRI Sky | 指定 goegap cubemap；強度先用 Exposure 模式，跟太陽一起調到畫面平衡 |
| Exposure | Fixed，約 **EV 14.5**（晴天大約 EV 15）；或用 Automatic Histogram，限制在 13–15 |
| Tonemapping | ACES |
| Fog | 開啟；Max Distance 約 2,000 m，Base Height 0，Mean Free Path 約 600–1,000 m（帶出遠山偏藍的空氣感） |
| Ambient Occlusion | Intensity 約 0.5，Radius 約 1.5 m |
| Contact Shadows | 開（小道具和牆腳的接觸陰影） |
| Micro Shadows | 開 |
| Shadows | Max Distance 約 150 m，4 cascades |
| 全域光照 | **Adaptive Probe Volumes（APV）**＋烘焙；陰影裡的暖色反彈光主要靠它 |

## 4. 沙塵、積沙、雨痕（Shader Graph，下一步）

Blender 的 lookdev 材質裡，這幾層是**用世界高度和法線方向自動算出來的**，不用逐一畫頂點色。要在 Unity 做出一樣的效果，需要一個 HDRP Lit Shader Graph（`SG_DST_ModularStone`）：

1. **石材**：Triplanar 取樣 BaseColor / Normal / MaskMap（世界座標，4 m）
2. **大範圍顏色變化**：世界座標 Noise（比例約 0.12）→ 乘到顏色上，範圍 ×0.86～×1.08
3. **牆腳積沙**：`mask = 1 - smoothstep(0.05, 0.85, worldY + noise*0.25 + (height-0.5)*0.6)`，混合沙地的 BaseColor 和光滑度。用石材的 Height 去打散邊緣，沙子會先填進磚縫
4. **朝上面的沙塵**：`saturate((normalWS.y - 0.6) / 0.3)`，同樣用 Height 打散
5. **雨痕**：拉長成垂直的 Noise（XZ ×6、Y ×0.35），只在 1.5 m 到牆頂之間生效，讓顏色變暗約 45%

做好後，所有牆模組共用這一個 Shader Graph，地圖上 2,000 個實例會自動得到一致的風化效果。

## 5. 效能參考（LOD0）

| 模組 | 三角面 |
|---|---|
| 直牆 400 / 200 | 12（沒有外露的邊，不需要倒角） |
| 轉角柱 | 20 |
| 拱窗牆 S | 約 2,300（布林運算留下的 n-gon，正式版要清理到 1,000 以下） |
| Cornice 400 | 約 2,300（小方塊有倒角；LOD1 改成用法線貼圖表現） |
| 木箱 L | 約 2,000 |

## 6. 實測後的最終數值（2026-10-11，Unity 6000.6.5f1）

已經在 `C:\Users\asdew\My project` 實際建好，場景是 `Assets/DUST/DR2_HeroTest/DR2_HeroTest.unity`，Volume Profile 是 `VP_DR2_HeroTest.asset`。

| 項目 | 數值 | 說明 |
|---|---|---|
| 天空 HDRI | `HDRI_DST_Goegap_4k_NoSun.hdr` | **去掉太陽的版本**。原圖的太陽佔了總能量的 87.6%，留著會跟 Directional Light 重複計算 |
| HDRI Sky | Intensity Mode = Multiplier，**46,987** | 讓上半球照度剛好等於 15,000 lux（用腳本設定時，Lux 模式需要的上半球照度不會自動計算，所以改用 Multiplier） |
| 太陽方向 | 朝向太陽的向量 (0.432, 0.727, 0.534)，仰角 46.6° | 在 HDRP 裡實際驗證過：天空殘留的光暈正好在這個方向 |
| 太陽強度 | 100,000 lux，5600 K，Angular Diameter 0.53 | |
| Exposure | Fixed，**EV 13.8** | EV 15 套上 ACES 後砂岩偏暗 |
| Fog | Mean Free Path 1,500 m，Max Fog Distance 800 m | 3,000 m 會讓 HDRI 下半部的地面整片被霧蓋掉 |
| 飾帶貼圖 | 飽和度降到 60% | Unity 裡的 `sandstone_cracks` 比 Blender 預覽時橘很多，乘顏色沒辦法降飽和度，所以直接改貼圖 |

截圖工具：`Assets/DUST/Editor/DR2Capture.cs`（`DR2Capture.Shot(camera, path)`），可以把指定攝影機渲染成 PNG。
