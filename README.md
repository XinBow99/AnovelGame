# DUST

Unity 6（6000.6.5f1）+ HDRP 的沙漠遺跡探索遊戲，支援 2–4 人連線（Netcode for GameObjects + Unity Relay，邀請碼加入）。

## 開發
- 用 Unity Hub 開啟這個資料夾，Unity 版本是 **6000.6.5f1**
- 主場景：`Assets/DUST/DR2_Map/DR2_DesertRuins02.unity`
- 重建玩家、車輛、動物 Prefab 之後，要再執行 **DUST → Network → Setup Multiplayer**
- 在一台電腦上測試連線：Window → Multiplayer → Multiplayer Play Mode，勾選 Player 2

## CI/CD（GitHub Actions + GameCI）
- push 到 `main` 或開 PR：輸出 Windows 版，到 Actions 頁面下載 Artifact
- 推 `v*` 標籤：自動建立 GitHub Release 並附上 zip

```sh
git tag v0.2.0
git push origin v0.2.0
```

需要先在 repo 的 **Settings → Secrets and variables → Actions** 設定：

| Secret | 內容 |
|---|---|
| `UNITY_EMAIL` | Unity 帳號 email |
| `UNITY_PASSWORD` | Unity 帳號密碼 |
| `UNITY_LICENSE` | Personal 授權檔（`Unity_lic.ulf`）的完整內容 |
