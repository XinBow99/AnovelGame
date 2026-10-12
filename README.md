# DUST

Unity 6（6000.6.5f1）+ HDRP 的沙漠遺跡探索遊戲，支援 2–4 人連線（Netcode for GameObjects + Unity Relay，邀請碼加入）。

## 下載遊玩
到 [Releases](https://github.com/XinBow99/AnovelGame/releases) 下載最新的 `DUST_vX_Y_Z_Windows.zip`，解壓縮後執行 `DUST.exe`。
開房的人把畫面左上角的 6 碼邀請碼給朋友，朋友在選單輸入就能加入。

## 載具
- **皮卡**：W/S 油門、A/D 轉向、Space 手煞車
- **無人機**：W/S 推力、A/D 轉向、Space/Ctrl 升降，降落後才能下機

走到載具旁按 **F** 上下車，**C** 切換視角。

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
