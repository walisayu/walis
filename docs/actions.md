# 動作開發指南

任務流程裡的每個「動作」都是一份 JSON 定義：宣告要顯示哪些欄位，以及要產生哪一段 shell。
定義本身不含程式碼，安裝一個動作的風險等同於貼一段 shell 樣板，因此可以放心讓團隊互相分享。

## 一份最小的定義

```json
{
  "id": "team.deploy",
  "name": "部署到測試機",
  "description": "呼叫團隊共用的部署腳本。",
  "category": "deploy",
  "icon": "cil-cloud-upload",
  "version": "1.0.0",
  "author": "Leo",
  "parameters": [
    { "name": "target", "label": "目標主機", "type": "text", "required": true, "placeholder": "stg-01" }
  ],
  "template": "./scripts/deploy.sh {{target}}"
}
```

在「建置任務 → 流程編輯器 → 管理動作」貼上這段 JSON 就完成安裝，之後所有任務都能拖進來使用。

## 欄位

| 欄位 | 必填 | 說明 |
| --- | --- | --- |
| `id` | 是 | 小寫識別字，至少包含一個點，例如 `team.deploy`。不可與內建動作重複。 |
| `name` | 是 | 顯示名稱。 |
| `description` | 否 | 一句話說明，會顯示在動作面板。 |
| `category` | 否 | `general`、`file`、`git`、`node`、`dotnet`、`container`、`deploy`、`check`，未填為 `general`。 |
| `icon` | 否 | CoreUI 圖示名稱，例如 `cil-terminal`。 |
| `version` / `author` | 否 | 給人看的資訊。 |
| `parameters` | 否 | 參數定義，見下表。 |
| `template` | 是 | 產生 shell 的樣板。 |

### 參數

| 欄位 | 說明 |
| --- | --- |
| `name` | 樣板裡使用的名稱，只能用英數字與底線。 |
| `label` | 表單上的欄位名稱。 |
| `type` | `text`、`multiline`、`number`、`boolean`、`select`、`path`。 |
| `required` | 必填時，流程編譯會擋下沒填的步驟。 |
| `default` | 預設值；`boolean` 用 `"true"` / `"false"`。 |
| `placeholder` / `help` | 輸入提示與說明文字。 |
| `options` | `select` 專用，格式為 `[{ "value": "a", "label": "A" }]`。 |

## 樣板語法

刻意只有三種語法，讓定義維持成容易審查的純資料：

| 語法 | 行為 |
| --- | --- |
| `{{name}}` | 帶入參數值，並用單引號包起來，值裡的單引號會自動跳脫。 |
| `{{{name}}}` | 原樣帶入，用在命令本體或旗標。**不會跳脫，只用在你自己控制的值。** |
| `{{#if name}}…{{/if}}` | 參數有值（且不是 `false` 或 `0`）時才輸出。 |
| `{{#unless name}}…{{/unless}}` | 參數沒有值時才輸出。 |

條件區塊可以互相嵌套，例如 `{{#if push}}git push{{#if force}} --force{{/if}} {{{name}}}{{/if}}`。

範例：

```
git pull{{#if rebase}} --rebase{{/if}}{{#if remote}} {{remote}}{{/if}}
```

參數 `rebase = true`、`remote = origin` 會產生：

```sh
git pull --rebase 'origin'
```

## 取得原始碼

建置流程的第一步通常是「取得原始碼」（`git.source`）：第一次建置 `git init` 加 `fetch`、之後只更新到遠端最新的提交，
所以重複建置不會因為目錄已存在而失敗，也不受上一次建置留下的狀態影響。儲存庫與分支留空時，用的是建置環境裡的
`WALIS_REPOSITORY` 與 `WALIS_BRANCH`，也就是任務設定的值，同一份位址不必在任務與動作各填一次。

目標目錄預設就是工作目錄。**填成子目錄的話，後面每個步驟都要自己切換進去**——每個步驟是獨立的 shell，
`cd` 不會延續到下一步。

## 私有儲存庫

不要把權杖寫進儲存庫網址（`https://user:token@host/repo.git`）。那個網址會進建置日誌、工作目錄的 `.git/config`、
`WALIS_REPOSITORY` 與匯出檔，而能看任務的人都看得到這些；任務表單會直接拒絕含帳密的網址，
內建的「取得原始碼」印出網址時也會去掉帳密，執行器還會把它找得到的網址帳密加進遮蔽清單——但這些都是補救，
正確作法是讓權杖根本不出現在網址裡：

1. 在「環境變數」為這個任務新增一個**機密**變數，例如 `GIT_TOKEN`，值填權杖。機密變數只會注入綁定的任務，建置輸出裡會被換成 `***`。
2. 儲存庫欄位填不含帳密的網址：`https://github.com/org/repo.git`。
3. 在「取得原始碼」之前加一個「執行命令」步驟，用 credential helper 把權杖交給 git：

```sh
git config --global credential.helper '!f() { printf "username=x-access-token\npassword=%s\n" "$GIT_TOKEN"; }; f'
```

或者直接寫腳本模式的 `git clone`，用 `-c` 只影響這一次命令：

```sh
git -c credential.helper='!f() { printf "username=x-access-token\npassword=%s\n" "$GIT_TOKEN"; }; f' \
  clone --depth 1 "$WALIS_REPOSITORY" .
```

`HOME` 在建置裡指向工作目錄，所以 `--global` 寫的是工作目錄底下的 `.gitconfig`，不會留到服務帳號的設定；
開了「每次建置前清空工作目錄」時每次都會重設。GitHub 的 username 可以隨意（慣例 `x-access-token`），GitLab 用 `oauth2`。
SSH 金鑰也一樣：放進機密變數，在步驟裡寫到檔案（`umask 077; printf '%s' "$DEPLOY_KEY" > id_deploy`）再用
`GIT_SSH_COMMAND="ssh -i id_deploy -o IdentitiesOnly=yes"`。

## 編譯結果

流程會依序編譯成一段腳本，存回任務的命令欄位，因此本機執行器與遠端 `walis-runner`
都不需要知道動作的存在：

```sh
# 這段腳本由 Walis 的動作流程產生，請在流程編輯器調整。
set -e

# 1. 輸出訊息
echo '開始部署'

# 2. 部署到測試機
./scripts/deploy.sh 'stg-01'
```

- 每個步驟都會加上編號註解，對照日誌時很好找。
- 勾選「失敗時繼續」的步驟會包成 `( … ) || true`。
- 停用的步驟不會出現在輸出裡。

## 切換到腳本模式

流程編輯器可以隨時切到腳本模式檢視或直接編輯產生的內容。動作流程會一併保存，
切回流程模式時會還原成原本的步驟；在腳本模式做的手動修改不會被保留，這一點切換時會再提醒一次。

## 分享與安裝

動作定義就是一個 JSON 檔，放進 Git、貼到聊天室或做成一包檔案都可以。
安裝時 Walis 會檢查：識別碼格式、參數名稱是否重複、型別是否支援、樣板有沒有用到未宣告的參數，
有問題會逐條列出來而不是安裝一個壞掉的動作。

## 步驟依賴與流程圖

步驟之間可以宣告依賴（`ActionStep.dependsOn`，填步驟 id），流程就是一張有向無環圖：
每一步等它依賴的步驟都結束才開始，沒有依賴的步驟是起點、彼此同時執行，能同時跑的分支就同時跑
（每個建置最多 8 步同時）。整個流程都沒填 `dependsOn` 的舊資料，從順序與「平行」旗標推導：
每一步依賴前一組，連續標記平行的同組——語意跟以前完全一樣，所以既有任務不用改。

執行條件改成看上游：`success` 要依賴的步驟全部成功（失敗但設定續行的算成功）；
`failure` 在上游有失敗（含因失敗而略過的）時跑；`always` 一律跑。一條分支失敗不會影響
不依賴它的分支，匯合點會被標成略過並把失敗往下游傳，所以接在匯合點後面的清理／通知步驟照舊會跑。

介面在流程模式有「清單」與「圖」兩種檢視：圖用 Vue Flow 畫，從節點右側的圓點拖到另一個節點
就是加一條依賴、點連線就拿掉，位置由 dagre 自動排版、不存檔；步驟設定裡也有「依賴步驟」清單。
第一次改依賴時整個流程會轉成明確的圖（把推導出來的依賴寫進每一步）。建置時 `ActionScriptCompiler.Plan`
把依賴轉成步驟編號存在 `BuildSteps.DependsOnJson`，停用的步驟不進計畫、依賴它的改依賴它的上游；
`BuildStepGraph` 負責推導、循環檢查與拓撲順序，`BuildStepSequence` 照圖排程，本機與遠端執行器共用。
單一腳本（預覽與 `Jobs.Command`）照拓撲順序輸出並註記依賴。

## 步驟輸出與引用

每個步驟是獨立的 shell，環境變數不會延續。要把值交給後面的步驟有兩條路：

- `$WALIS_ENV_FILE`：追加 `KEY=VALUE`，後面的步驟直接讀環境變數。無型別、不留紀錄，適合中間值。
  合併時會略過 `WALIS_` 開頭的名稱（建置編號、步驟輸出、上游狀態是系統寫的，步驟不能改寫）與
  `HOME`／`ZDOTDIR`（執行器釘住它們，shell 才不會讀到服務帳號的啟動檔）；任務層級的環境變數同樣不能以
  `WALIS_` 開頭。`PATH`、`LD_PRELOAD` 這類名稱則刻意不擋——能設變數的人本來就能寫 shell，而任務要用自己
  安裝的工具鏈就得改 `PATH`。
- `$WALIS_OUTPUT_FILE`：寫 `name=value`（一行一個）或一個 JSON 物件，這是「這一步的輸出」：
  會存進建置紀錄（介面的步驟區塊看得到，機密值會遮蔽），而且能被後面步驟的參數引用。

引用的寫法是在參數值裡放 `${{ steps.<識別字>.<名稱> }}`，識別字在步驟設定裡填（小寫英數與底線，
流程內唯一）。儲存任務時編譯器把引用換成 shell 變數 `"${WALIS_STEP_<識別字>_<名稱>}"`，
執行時 `BuildStepSequence` 在每一步結束後把輸出注入成同名環境變數。值從頭到尾不會被拼進腳本文字，
所以沒有注入問題；遠端執行器跑的是同一套 `BuildStepSequence`，不需要懂運算式。

限制：只能引用這一步的前置步驟（依賴鏈上游、含遞移；平行或後面的步驟不能引用）、被停用的步驟不算；
輸出最多 100 個、每個值最多 8 KB、整個輸出檔最多 64 KB；名稱只能英數字與底線。
JSON 物件的純量值照字面、巢狀值保留成 JSON 文字。內建動作「設定輸出」就是一個寫輸出檔的捷徑；
腳本模式的任務直接用 `$WALIS_STEP_<識別字>_<名稱>`。

動作定義可以用 `outputs`（`[{ "name", "description" }]`）宣告自己會寫哪些輸出，只是給介面的
「插入前面步驟的輸出」選單與文件看，執行時不強制。

## 資料驅動的條件

步驟可以設「執行條件」運算式（`ActionStep.condition`），執行時用前面步驟的輸出與環境變數求值，
不成立就把這一步標成略過（不算失敗，下游照常依自己的條件判斷）。搭配依賴圖就是 if／switch：
兩個分支各設互斥的條件、都依賴同一個上游。

```
steps.check.changed == 'true' && env.BRANCH != 'main'
matches(steps.api.status, '^2..$') || empty(steps.api.body)
```

語法只有字串常值、`steps.<識別字>.<名稱>`、`env.<名稱>`、`==`、`!=`、`&&`、`||`、`!`、括號，
與 `contains`、`startsWith`、`endsWith`、`matches`（正規式，200 ms 逾時）、`empty` 五個函式；
沒有算術與指派。裸值以「非空且不是 false／0」為真。解析器在 `StepCondition`，儲存任務時檢查語法
且引用只能指向前置步驟；執行時語法錯誤會讓那一步失敗。條件跟執行條件（success／always／failure）
都要成立才會跑。

要讓流程「呼叫 API、取欄位、依結果分支」，內建動作有 `http.request`（curl；輸出 `status`、`body`，
4xx／5xx 不讓步驟失敗，交給條件判斷）與 `data.json-get`（從 JSON 取欄位，輸出 `value`；
執行器要有 python3 或 node）。這兩個動作有宣告 `outputs`，「插入前面步驟的輸出」選單列得出來。

## 動作套件：讓別人開發、由你審核

要讓外部貢獻者提供動作，用「動作套件」：一個獨立的 git repo，裡面是 `pack.json`
加上 `actions/**/<id>.json`，每個檔案就是上面那種動作定義。貢獻者對那個 repo 開 PR，
你審核合併，Walis 主機更新 clone 後重新載入就好；Walis 只讀目錄、不寫回，所以 git 永遠是唯一來源。

開發規則（命名、參數、樣板的限制與審核清單）在 `docs/action-development.md`。共用的套件 repo 是 https://github.com/walisayu/walis-actions 。起始範本在 `docs/action-pack-template/`（含 README 的貢獻與審核流程、JSON Schema 與零依賴的
`scripts/validate.mjs` 本機檢查）。整份複製成新 repo 即可。

載入規則：

- 目錄來自 `Walis:ActionPacks`（`appsettings.json` 裡的目錄陣列）加上資料目錄底下的 `action-packs/`。
  目錄本身有 `pack.json` 就是一個套件，否則它底下每個有 `pack.json` 的子目錄各是一個套件。
- 每個 JSON 走與介面安裝相同的 `ActionManifest.TryParse` 檢查，另外檔名必須等於 `id`。
  壞掉的檔案略過並記在該套件的 `errors`，其他動作照常載入。
- 同 id 的優先順序：內建 > 套件 > 介面安裝 > 外掛；套件之間先載入者贏，後者記錯誤。
- 套件動作在目錄裡的 `source` 是 `pack:<套件名稱>`，流程編輯器以「套件」標記顯示，不能從介面移除。
- `GET /api/actions/packs` 列出套件與錯誤，`POST /api/actions/packs/reload` 重新讀目錄（都是管理員限定）。
  排程工作者每分鐘比對套件目錄的檔案指紋，有變動就自動重載，所以 `git pull` 完不必再按重新載入。
- 資料目錄在建置沙箱裡禁止寫入，要用排程任務 `git pull` 套件就得放在資料目錄外並用 `Walis:ActionPacks` 指定；
  macOS 服務的發行腳本會寫 `$PREFIX/action-packs`，`deploy/register-job.sh` 會 clone 共用套件到那裡並註冊每天拉一次的「動作套件更新」任務。

