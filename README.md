# DUST

Unity 6（6000.6.5f1）+ HDRP 的沙漠遺跡探索遊戲，支援 2–4 人連線（Netcode for GameObjects + Unity Relay，邀請碼加入）。

## 下載遊玩
到 [Releases](https://github.com/XinBow99/AnovelGame/releases) 下載最新的 `DUST_vX_Y_Z_Windows.zip`，解壓縮後執行 `DUST.exe`。
開房的人把畫面左上角的 6 碼邀請碼給朋友，朋友在選單輸入就能加入。

## 地圖
開房（或單人遊玩）前在選單選地圖：
- **隨機世界**：像創世神一樣用種子碼生成、走到哪生到哪的無限沙漠——沙丘、風蝕台地、散落的石塊與倒塌的遺跡。
  種子碼空白 = 隨機；輸入數字或任何文字都可以，同一個種子每次都會生成一樣的世界。遊戲中按 Esc 可以看 / 複製種子碼。
  連線時以房主的種子為準，每個人在自己電腦上生成同一個世界（不傳地形資料）。
- **遺跡二**：原本手工做的沙漠遺跡地圖。

隨機世界裡散布著**沙漠聚落**（每個種子都不一樣，出生點旁邊一定有一個）：
綠洲村（水池＋椰棗樹）、沙漠聚落、城寨（外圍城牆＋城門）、商隊驛站（市集攤位）、廢棄的聚落（只剩斷牆）。
中央廣場有水井或水池，街道從廣場往外延伸，房子沿街面向道路；走進聚落時畫面上方會顯示聚落名字。
聚落格局在 `DSTSettlement`（藍圖）與 `DSTSettlementBuilder`（蓋房子）；水井、攤位、椰棗樹模型在 `DUST/blender/SM_DST_World_Settlement.blend`。

程式在 `Assets/DUST/Scripts/World/DSTWorld*.cs`（地形公式 `DSTWorldGen`、區塊 `DSTWorldChunk`、擺設 `DSTWorldScatter`、動物導航 `DSTWorldNavMesh`）；
石塊模型 `DUST/blender/SM_DST_World_Rocks.blend`；重新設定用 **DUST → World → Setup Random World**。

## 地圖與地區
- 右上角是小地圖（北朝上），按 **M** 展開全螢幕地圖：滾輪縮放、拖曳移動，「回到我的位置」置中，M / Esc 關閉。
  標示自己（箭頭＝面向）、其他玩家、載具、聚落名稱與出生點。隨機世界的地圖直接由種子畫出來，每個人看到的都一樣。
- 走進不同地區時左下角會跳出「你來到了…」：營地、聚落、古代遺跡，以及各有名字的台地、沙海、盆地、平原。

## 載具
- **皮卡**：W/S 油門、A/D 轉向、Space 手煞車
- **無人機**：W/S 推力、A/D 轉向、Space/Ctrl 升降，降落後才能下機

走到載具旁按 **F** 上下車，**C** 切換視角。

## 碰撞物理
開車撞地圖上的箱子：慢慢頂會推動，撞得夠猛（約 15 km/h 以上）會爆成木板碎片，2 分鐘後在原位重生；木桶只能推。
門檻在箱子的 `DSTPhysicsProp`（`breakDeltaV`）；重新設定用 **DUST → World → Setup Physics Props**。碎片模型：`DUST/blender/SM_DST_Prop_Crate_Debris.blend`。

## 日夜循環
遊戲裡一天 20 分鐘（白天約 12 分鐘、夜晚約 8 分鐘），右上角顯示目前時間。連線時以房主的時間為準。
調整：場景裡的 `TimeOfDay`（一天長度、開場時間、夜晚亮度 `nightExposure`）；重建燈光設定用 **DUST → World → Setup Day-Night Cycle**。

## 開發
- 用 Unity Hub 開啟這個資料夾，Unity 版本是 **6000.6.5f1**
- 主場景：`Assets/DUST/DR2_Map/DR2_DesertRuins02.unity`
- 重建玩家、車輛、無人機、動物 Prefab 之後，要再執行 **DUST → Network → Setup Multiplayer**
- 在一台電腦上測試連線：Window → Multiplayer → Multiplayer Play Mode，勾選 Player 2

## 發布新版本（本地建置）
1. Player Settings 的 Version 改成新版本號（例如 `0.3.0`）
2. Unity 選單 **DUST → Build → Windows Release**，會產生 `Build/DUST_v0_3_0_Windows.zip`
   （檔名只用底線，避免 `-` 與 `.` 在部分 Windows 環境出問題；不會包含 `*DoNotShip*` 除錯資料夾）
3. 發布：

```sh
git tag v0.3.0
git push origin v0.3.0
gh release create v0.3.0 Build/DUST_v0_3_0_Windows.zip --title "DUST v0.3.0" --generate-notes
```
