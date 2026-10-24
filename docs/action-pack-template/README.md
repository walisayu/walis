# Walis 動作套件範本

這個目錄是「動作套件」repo 的起始內容。把它整份複製成一個新的 git repo，
貢獻者對那個 repo 開 PR，維護者審核合併後 Walis 主機 `git pull` 就會多出這些動作。
動作只是 JSON（參數宣告＋shell 樣板），不含程式碼，所以審 PR 就是審一段 shell。

## 目錄結構

```
pack.json                      套件名稱、版本、說明
actions/<分類>/<id>.json       一個檔案一個動作，檔名必須等於 id
schema/action.schema.json      JSON Schema，編輯器可用來自動補全與檢查
scripts/validate.mjs           零依賴的本機檢查：node scripts/validate.mjs
```

子目錄只是給人看的，Walis 會遞迴讀 `actions/` 底下所有 `.json`；分類以檔案裡的 `category` 為準。

## 貢獻流程

1. Fork 這個 repo，在 `actions/<分類>/` 底下新增 `<id>.json`。
   **開發規則見 Walis repo 的 `docs/action-development.md`**：id 用自己的前綴、參數 camelCase、
   使用者輸入一律 `{{x}}`、機密不當參數、只寫 POSIX sh。
2. 跑 `node scripts/validate.mjs`，錯誤要清掉、警告要在 PR 說明理由；CI 跑同一支。
3. 在自己的 Walis 用「安裝動作」貼進去，建任務用預設值跑一次。
4. PR 說明寫：做什麼、需要哪些工具在執行器上、有沒有網路存取、有沒有用 `{{{ }}}` 及理由。一個 PR 一個主題。
5. 維護者依 `docs/action-development.md` 第 6 節的清單審核，合併後在 Walis 主機更新套件目錄並按「重新載入」。

## 安裝到 Walis

```bash
cd <Walis 資料目錄>/action-packs
git clone <套件 repo 網址> community
```

之後在管理介面「編輯任務 → 流程模式 → 管理動作 → 動作套件」按「重新載入」，
或呼叫 `POST /api/actions/packs/reload`。資料目錄以外的位置用 `appsettings.json` 的
`Walis:ActionPacks`（目錄陣列）指定；一個目錄可以是一個套件，也可以是放了多個套件的上層目錄。

套件裡有問題的檔案會被略過並列在「動作套件」清單的錯誤裡，其他動作照常載入。
同 id 的優先順序是：內建 > 套件 > 介面安裝 > 外掛。
