# 外掛 SDK（@walisayu/plugin-sdk）

原始碼在 `source/walis-plugin-sdk`，使用說明在該目錄的 README，
外掛作者看的合約說明在管理介面的「外掛 → 開發指南」。

這份記錄的是**為什麼這樣做**，以及維護時要注意的地方。

## 為什麼需要 SDK

管理介面的 textarea 適合改一行，不適合開發：沒有型別、沒有測試、沒有相依管理，
而且作者要自己記得「host 沒有 require、沒有 await」這類限制——通常是在正式建置失敗時才想起來。

SDK 把這些前移到本機：型別在編輯器裡就會提示，測試在 `node --test` 裡跑，
相容性問題在 `walis-plugin build` 就被擋下。

## 三個必須維持一致的合約

改動任何一邊時，另一邊要跟著改，否則「本機測過」會變成謊言：

| 合約 | host 端 | SDK 端 |
| --- | --- | --- |
| **跳脫規則** | `PluginHost.ShellQuote` | `src/shell.ts` 的 `quote` |
| **參數預設值語意** | `ActionScriptCompiler.Resolve`（boolean 空字串） | `src/harness.ts` 的 `applyDefaults` |
| **網域比對** | `PluginHost.IsAllowed`（只比協定與主機） | `src/harness.ts` 的 `isAllowed` |

兩邊都有測試釘住這些行為（C# 在 `PluginTests`，TS 在 `test/`）。

## 打包產物的形式

`walis-plugin build` 用 esbuild 產生 IIFE（`globalName: __walisPlugin`、`target: es2020`），
最後補一行：

```js
globalThis.render = __walisPlugin.default.render;
```

host 是 `engine.Execute(code)` 之後取全域的 `render`，所以這一行是 SDK 與 host 之間的介面。
`PluginTests.接受_SDK_打包出來的形式` 這個測試就是為了鎖住它——改壞會讓所有用 SDK 開發的外掛失效。

`target: es2020` 是保守值：Jint 支援到 ES2023，但降一級可以避開比較新的語法邊角。

## 相容性檢查

`src/bundle.ts` 的 `FORBIDDEN` 列出 host 沒有的東西（require、process、fs、setTimeout、
裸 fetch、await、Promise、XMLHttpRequest）與 20,000 字元的長度上限。
`walis-plugin install` 在有警告時直接拒絕上傳。

注意 `fetch` 的規則刻意用後行否定 `(?<!ctx\.http\.)`，否則合法的 `ctx.http.fetch` 會被誤判。

## 發布

套件尚未發布到 npm。`@walis`、`walis`、`walis-plugin-sdk` 這幾個名稱在 2026-09-05 檢查時都還沒被佔用。

已經準備好的：`publishConfig.access = public`（scoped 套件的預設是私有，不設會被拒絕）、
`files` 只收 `dist`、`template`、README 與 LICENSE、`prepack` 會重新編譯、MIT 授權檔。
`npm pack --dry-run` 是 20 個檔案 10.2 kB；用打包出來的 tarball 實測過
「安裝 → `walis-plugin init` → `render`」整條流程。

### 已發布

`@walisayu/plugin-sdk@0.1.0`（2026-09-05）。從 npm 安裝後實測過
「`init` → `render` → `build`」整條流程。

**版號從 0.1.0 開始**：host API 還在動（目前只有 `action` 一種擴充點），
1.0.0 會給出「介面已穩定」的承諾，現在還不是。

### scope 的坑

一開始想用 `@walis`，但那個 scope 的 owner 是 `walis` 這個帳號，
而發布用的帳號是 `walisayu`——`npm publish` 會回 **E404 而不是 403**，
訊息看起來像「套件不存在」，實際上是沒有權限。查法：

```bash
npm whoami          # 我是誰
npm org ls <scope>  # 這個 scope 的 owner 是誰
```

最後改用個人 scope `@walisayu`，那是登入帳號一定拿得到的。

### 發布需要 OTP

帳號的 2FA 是 `auth-and-writes`，所以 `npm publish` 會要一組 6 位驗證碼。
在自己的終端機跑就會互動式詢問；要自動化就到 npmjs.com 產生 **Automation** token（會繞過 2FA）。

發布後 registry 有快取延遲，`npm view` 可能要等 20～30 秒才查得到。

### 沒有 provenance，手動發布的規矩

npm 的 provenance（`--provenance`，產生 Sigstore 證明）**只能在支援的 CI 上產生**（GitHub Actions、GitLab CI），
而 Walis 刻意不在 GitHub 放任何建置或發佈流程（見 `internal/README.md`），所以這個套件沒有 provenance，
使用者在 npm 頁面看不到「Provenance」區塊。這是已知且接受的取捨；補強方式是把手動發布的條件寫死：

- 一律在維護者自己的終端機跑 `npm publish`，用 2FA（`auth-and-writes`）互動輸入 OTP；
  **不要**為了方便建 Automation token——它會繞過 2FA，等於把發布權交給任何拿到 token 的人。
- 發布前 `npm ci`（不是 `npm install`）、`npm test`、`npm pack --dry-run` 看清單只有 `dist`、`template`、README、LICENSE。
- 發布的 commit 要先推上 Gitea 並 tag（`plugin-sdk-vX.Y.Z`），tarball 的 `gitHead` 才對得上原始碼。
- 發布後用 `npm view @walisayu/plugin-sdk dist.integrity` 把 integrity 值記進 CHANGELOG 對應段落，
  使用者（與未來的自己）可以對照 lockfile 裡的值。

哪天改到有 CI 再加 `--provenance`，到時候要同步改這段與 `package.json` 的 `publishConfig`。

## 打包設定上踩過的坑

- `tsc` 的 `rootDir` 若涵蓋 `test/`，輸出會變成 `dist/src/cli.js`，`bin` 就指錯了。
  正式編譯只收 `src`，測試用 `tsconfig.test.json` 另外輸出到 `dist-test`。
- scoped 套件不設 `publishConfig.access = public` 會因為「需要付費帳號」而發布失敗。
- `#!/usr/bin/env node` 這行 tsc 會保留，不必另外處理。
