# 動作開發規範

這份是寫動作（不論是介面安裝、自己的套件目錄，還是對共用套件 repo 開 PR）都要遵守的規則。
格式與樣板語法本身見 `docs/actions.md`；這裡只講「怎麼寫才會被接受」。

規則分兩種：**檢查器會擋的**（`scripts/validate.mjs` 與伺服器都會拒絕，PR 的 CI 也跑同一支）
與**審核者看的**（機器判斷不了，靠人）。前者標「檢查」，後者標「審核」。

## 1. 識別碼與檔案

- 【檢查】`id` 格式是 `<前綴>.<動作>`：小寫英數，段落用 `.` 分隔，動作段可含 `-`，最多 128 字元。例：`acme.deploy-k8s`。
- 【檢查】前綴代表貢獻者、團隊或工具，**不可用內建的前綴**：
  `walis`、`shell`、`file`、`git`、`node`、`dotnet`、`python`、`java`、`go`、`rust`、`ruby`、`php`、
  `docker`、`k8s`、`db`、`cloud`、`deploy`、`check`、`test`、`security`、`notify`、`archive`。
  要補內建工具的動作也用自己的前綴（`acme.git-sparse-clone`），才不會之後跟新版內建撞名。
- 【檢查】檔名等於 id：`actions/<分類>/<id>.json`。子目錄只是整理用，分類以檔案內 `category` 為準。
- 【審核】id 一旦發布就不能改：任務的流程存的是 id，改名等於讓所有用到它的任務編譯失敗。
  要換名字就新增一個，舊的保留並在 `description` 開頭標「已棄用：改用 xxx」。

## 2. 顯示欄位

- 【檢查】`name` 必填，最多 40 字元，動詞開頭的短句（「建置映像」而不是「Docker 映像建置工具」），
  結尾不加句號。介面的調色盤空間有限。
- 【檢查】`description` 必填，一到兩句話說明做什麼與需要什麼工具，例如「用 kubectl 套用 manifest，執行器要有 kubectl 與叢集憑證。」
- 【檢查】`category` 必填且在清單內（見 `schema/action.schema.json`）。不知道放哪就 `general`。
- 【檢查】`icon` 必填，CoreUI 圖示名稱，`cil-` 開頭。圖示清單見 https://coreui.io/icons/ 。
- 【檢查】`version` 必填，語意化版本 `x.y.z`。
  參數改名、拿掉、變必填或改語意 → 主版號；新增選填參數或改樣板但行為相容 → 次版號；修錯字 → 修訂號。
- 【檢查】`author` 必填，人名、團隊名或 GitHub 帳號。
- `name`、`description`、`label` 用英文或繁體中文擇一，同一個動作內一致；不要塞雙語。共用 repo 對外以英文為主。

## 3. 參數

- 【檢查】`name` 用小寫開頭的 camelCase（`imageTag`），不用底線與大寫開頭；建置環境變數才是全大寫。
- 【檢查】`label` 必填；`type` 只能是 `text`、`multiline`、`number`、`boolean`、`select`、`path`。
- 【檢查】`boolean` 一定要有 `default`（`"true"` 或 `"false"`）；`select` 一定要有 `options` 且 `default` 在選項內；
  `number` 的 `default` 要是數字。
- 【檢查】最多 64 個參數；一個動作超過 8 個參數通常代表該拆成兩個動作，檢查器會警告。
- 型別選法：路徑用 `path`、要貼一段腳本用 `multiline`、開關用 `boolean`、固定幾種選法用 `select`，
  其餘才是 `text`。`placeholder` 放一個真實的範例值，不要放說明文字。
- 【審核】必填參數要真的必要；能給合理預設值的就給，讓拖進來不改任何東西也能跑。
- 【審核】**機密不能當參數**：token、密碼、金鑰一律讓使用者設成任務的機密環境變數，樣板裡用 `"$MY_TOKEN"`，
  並在 `description` 說明要設哪個變數。參數值會存在任務裡、顯示在介面上，也會進匯出檔。

## 4. 樣板

- 【檢查】使用者輸入一律用 `{{x}}`（會加單引號跳脫）。`{{{x}}}` 原樣插入只允許三種情況：
  參數型別是 `number`、`boolean`、`select`（值受限），或參數名是 `command`、`script`、`arguments`
  （使用者本來就是在寫 shell）。其他參數用 `{{{x}}}` 會被拒絕。
- 【檢查】樣板只能用到宣告過的參數。
- 【檢查】禁止的寫法（直接拒絕）：
  `curl … | sh`／`wget … | bash` 這類下載即執行、`sudo`、`rm -rf /`、`rm -rf ~`、寫入 `/etc`。
- 【檢查】會警告的寫法（審核者要看理由）：`eval`、`$HOME`、`~/`、`nohup`、`&` 背景執行、`chmod 777`。
- 每個步驟是獨立的 shell（macOS 是 zsh，其他是 `/bin/sh`），**只寫 POSIX sh 相容的語法**：
  不用 `[[ ]]`、陣列、`local`、`function` 關鍵字、`source`（用 `.`）。
- 環境變數不會延續到下一步；要傳值就 `echo "KEY=value" >> "$WALIS_ENV_FILE"`。
- 工作目錄就是 workspace，所有路徑用相對路徑；不要 `cd` 到 workspace 外，不要寫家目錄。
  建置有沙箱，workspace 外的寫入會失敗。
- 樣板要能在「參數都是預設值」時執行成功或給出清楚錯誤；不要靜默跳過。
- 失敗要讓結束碼非零，不要 `|| true` 把錯誤吃掉（除非那正是動作的目的，例如「檢查但不阻擋」，且 name 要說清楚）。
- 【檢查】樣板最多 100,000 字元；實務上超過 40 行就該考慮改成呼叫 repo 裡的腳本。
- 可選：用 `{{#if x}}…{{/if}}`／`{{#unless x}}…{{/unless}}` 處理選填參數，不要靠 shell 判斷空字串。
- 要讓後面的步驟拿到值，寫進 `$WALIS_OUTPUT_FILE`（`name=value` 一行一個），並在定義的 `outputs`
  宣告名稱與說明，介面才能列給使用者挑。`${{ steps.x.y }}` 是給使用者填在參數值裡的，
  **動作樣板本身不要寫**；樣板裡用 `{{x}}` 就會正確處理使用者填進來的引用。
- 【檢查】`outputs` 最多 32 個，名稱英數字與底線、不重複，說明必填。

## 5. 送 PR 前

1. `node scripts/validate.mjs` 全綠。
2. 在自己的 Walis 用「安裝動作」貼進去，建一個任務、拖進來、用預設值跑一次，看日誌。
3. PR 說明寫：做什麼、需要哪些工具在執行器上、有沒有網路存取、有沒有用 `{{{ }}}` 及理由。
4. 一個 PR 一個主題。

## 6. 審核清單（維護者用）

- id 前綴屬於貢獻者，不是內建前綴，也沒跟既有套件動作撞名。
- 每個 `{{{x}}}` 都符合第 4 節的三種情況，且用途合理。
- 樣板不下載執行遠端內容、不提權、不寫 workspace 外、不把機密當參數。
- 預設值安全：不會一拖進來就對正式環境做事（預設 `dryRun=true`、預設 namespace 是 staging 之類）。
- `description` 有寫清楚依賴的工具。
- 版本號符合變更幅度；改既有動作沒有動到 id 與參數名，或有跟著升主版號。
- 改動只在 `actions/` 底下；動到 `scripts/`、`schema/` 的 PR 另外審。
