# DUST

Unity 6（6000.6.5f1）+ HDRP 的沙漠遺跡探索遊戲，支援 2–4 人連線（Netcode for GameObjects + Unity Relay，邀請碼加入）。

## 下載遊玩
到 [Releases](https://github.com/XinBow99/AnovelGame/releases) 下載最新的 `DUST-*-Windows.zip`，解壓縮後執行 `DUST.exe`。
開房的人把畫面左上角的 6 碼邀請碼給朋友，朋友在選單輸入就能加入。

## 開發
- 用 Unity Hub 開啟這個資料夾，Unity 版本是 **6000.6.5f1**
- 主場景：`Assets/DUST/DR2_Map/DR2_DesertRuins02.unity`
- 重建玩家、車輛、動物 Prefab 之後，要再執行 **DUST → Network → Setup Multiplayer**
- 在一台電腦上測試連線：Window → Multiplayer → Multiplayer Play Mode，勾選 Player 2

## 發布新版本（本地建置）
1. Unity：File → Build Profiles → Windows → Build，輸出到 `Build/`（已被 .gitignore 排除）
2. 把輸出資料夾壓成 `DUST-vX.Y.Z-Windows.zip`（不要包含 `*_BurstDebugInformation_DoNotShip`）
3. 發布：

```sh
git tag vX.Y.Z
git push origin vX.Y.Z
gh release create vX.Y.Z DUST-vX.Y.Z-Windows.zip --title "DUST vX.Y.Z" --generate-notes
```
