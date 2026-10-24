# 外掛機制設計筆記

**階段 1 已實作**（`action` 類外掛：Jint 直譯器、資源上限、能力宣告、動作面板整合、預覽與安裝介面）。
`notification` 與 webhook 解析的擴充點尚未實作。這份文件保留當初的評估與決策，實作與它一致；
差異之處以「實作備註」標出。

現有的擴充機制是動作定義（`docs/actions.md`）：一份純資料 JSON，宣告參數並提供 shell 樣板。
外掛機制要解決的是那套「純資料」做不到的四類需求：

- **條件與迴圈邏輯**：依環境或前一步的結果挑不同流程
- **呼叫外部 API**：建立工單、查部署狀態、取版本號
- **解析輸出**：把測試報告或掃描結果轉成結構化資料
- **格式化通知**：各家 IM 的訊息格式差異大，樣板拼不出來

反過來說，「跑一段指令」現有機制已經夠好，不該搬進外掛。

## Jenkins 做了什麼

### 三種擴充方式

| 方式 | 語言／打包 | 用途 | 沙箱 |
| --- | --- | --- | --- |
| Plugin（`.hpi`） | Java，載進同一個 JVM | 官方更新中心約 1,900+ 個 | **沒有** |
| Shared Library | Groovy，放在 git repo | 團隊內部共用流程 | 可選 |
| Pipeline 腳本內嵌 | Groovy | 單一任務 | Groovy Sandbox：方法白名單 + 管理員核准 |

值得注意的是：**Walis 想要的「別人開發、我上傳」比較接近後兩者，而不是 Plugin。**

### 擴充點分類

Jenkins core 以 `ExtensionPoint` 介面加 `@Extension` 標註定義擴充點，數量是數百個等級。

| 類別 | 代表擴充點 |
| --- | --- |
| 建置流程 | `Builder`／`BuildStep`、`Publisher`（`Recorder`／`Notifier`）、`BuildWrapper`、Pipeline 的 `Step` 與 `GlobalVariable` |
| 觸發與來源 | `SCM`、`SCMSource`（multibranch）、`Trigger` |
| 執行環境 | `Cloud`（動態開關 agent）、`ComputerLauncher`、`ToolInstaller`、`NodeProperty`、`QueueTaskDispatcher`、`LoadBalancer`、`Callable`（把程式碼送到 agent 執行） |
| 安全 | `SecurityRealm`（認證來源）、`AuthorizationStrategy`（授權模型）、`CredentialsProvider` |
| 輸出與資料 | `ConsoleLogFilter`／`ConsoleAnnotator`（遮蔽密碼、ANSI 上色、時間戳）、`ArtifactManager`（換儲存後端）、測試報告解析 |
| 介面 | `RootAction`、`ManagementLink`、`Action` + `TransientActionFactory`、`PageDecorator`、`Widget`、Jelly 表單與 `doCheckXxx` 驗證 |
| 生命週期 | `RunListener`、`ItemListener`、`SaveableListener`、`ComputerListener`、CLI 指令、REST 端點、JCasC configurator |

現在的主流是 Pipeline：外掛沒有提供 `Step`，在 Pipeline 的世界裡等於不存在。

### 能力邊界

**Plugin 沒有沙箱。** 它與 core 同一個 JVM、同一個行程，可以讀任何檔案、開任何連線、
改其他外掛的狀態、把 controller 的記憶體吃光。Jenkins 的安全模型就是「你信任你安裝的東西」，
「別人開發、我上傳」靠的是更新中心的人工審核與安全公告，不是技術隔離。

真正有沙箱的是 Pipeline 的 Groovy Sandbox：方法白名單，遇到沒白名單的呼叫就中止，
並產生一筆待管理員核准的簽章。**這個模型才是 Walis 該抄的。**

### 代價

| 問題 | 表現 |
| --- | --- |
| 無隔離 | 一個外掛的洩漏或無窮迴圈拖垮整台 controller |
| 依賴地獄 | 外掛互相依賴加版本相容矩陣，升級連鎖爆炸 |
| 升級風險 | core 改介面打爛既有外掛；許多外掛無人維護 |
| 安全 | 大量 CVE 來自外掛，每次公告都要人工判斷影響 |
| UI 技術債 | Jelly 加 Stapler，與現代前端脫節 |
| 兩套模型 | Freestyle 與 Pipeline 長期並存，外掛要同時支援 |

換來的是幾乎任何工具都有現成整合——這是 Jenkins 至今仍在的原因。

## 核心矛盾

Jenkins 的外掛之所以能做那麼多事，**正是因為它沒有沙箱**：`Cloud`、`SecurityRealm`、
`ArtifactManager` 這些擴充點本質上就需要完整的系統存取權。

而 Walis 的目標是「其他人也能開發上傳」。這兩件事直接衝突：

- 要**能力大**（Jenkins 那種）→ 只能靠信任與人工審核，技術上擋不住
- 要**能安全接受陌生人上傳** → 擴充點必須設計成「外掛只回傳資料，實際動作由 Walis 執行」

### 設計原則

1. **外掛的產出是資料，不是行為。** `action` 類外掛回傳一段 shell 字串，管理員存檔前看得到，
   Walis 保有最後決定權。這是 Jenkins 想做但架構上做不到的事。
2. **兩級信任。** 受限（預設，別人上傳的）只能產生 shell 片段，連外與使用機密一律不給，
   manifest 裡宣告了也不算；信任（管理員安裝時明確勾選）才拿得到 manifest 宣告的能力。
   宣告是「我需要什麼」，勾選信任是「我核准了」，兩者缺一不可。
3. **能力由 manifest 宣告、管理員核准。** 沒宣告就沒有，宣告了也要人按下同意。

## 對照 Walis 現況

| Jenkins 擴充點 | Walis 對應 | 決定 |
| --- | --- | --- |
| Pipeline `Step`／`Builder` | 動作定義（純資料樣板） | **升級成 JS，第一順位** |
| `Publisher`／`Notifier` | 通知管道（寫死在 `NotificationDispatcher`） | 開放，第二順位 |
| `Trigger`／webhook 解析 | `PushWebhookService`（GitHub／GitLab 寫死） | 開放，讓別人補其他 Git 服務 |
| 備份目的地 | `IBackupTargetClient` | 介面形狀已經對了，順手 |
| `ConsoleLogFilter` | 機密遮蔽（`MaskSecrets`）已內建 | 可開放，價值中等 |
| `ArtifactManager` | `ArtifactStore` | 低優先 |
| `Cloud`（動態 agent） | 無（執行器手動註冊） | **不做**，Jenkins 最複雜的部分 |
| `SecurityRealm`／授權 | 內建登入與 OTP | **不做**，認證外掛出事就是全開 |
| UI 擴充 | 無 | **不做**，Jenkins 這塊是純負債 |

## 引擎選擇

| 方案 | 部署衝擊 | 沙箱 | 生態 | 對 `walis-runner` |
| --- | --- | --- | --- | --- |
| **Jint**（純 C#） | 無，一個 NuGet | 天生沒有 I/O，不注入就沒有；內建逾時／語句數／記憶體上限 | 無 npm，只有 ES2023 標準庫 | 直接跟著 runner 走 |
| ClearScript（V8） | 每個 RID 一組原生檔，self-contained 發行變大 | V8 isolate 好，host 物件曝露要小心 | 無 npm | 每台 runner 都要對的原生檔 |
| Node 子行程 | 每台機器都要裝 Node | OS 程序隔離 | 完整 npm | runner 也要裝 Node |
| Deno 子行程 | 單一執行檔，仍是外部依賴 | 最好，權限是 CLI 旗標 | 部分相容 | 每台 runner 要一份 |
| QuickJS／Wasmtime | 一個 wasm runtime | 天生沙箱 | 無 | 管線最複雜 |

**結論：第一階段用 Jint。** 兩個決定性理由：

1. 部署已經是 self-contained 執行檔，
   加原生依賴等於拆掉那個好處。
2. 外掛要能在遠端執行器上跑。Jint 是純 managed，`Walis.Runner` 直接引用就有；
   Node／Deno 方案等於規定每台 build agent 都要先裝好 runtime——這正是 Jenkins 讓人痛的地方之一。

代價很誠實：**沒有 npm**，外掛作者不能 `import axios`，要用 host 提供的 `http`。
對膠水程式不是問題，對「我要用某個 SDK」就是問題。所以 host API 的形狀要先定好，
將來真要換成 Node sidecar，外掛不必重寫。

### Jint 的實務細節

- **async 很尷尬**：Jint 有 Promise 但要手動打 pump。host API 一律做成同步，
  整個外掛跑在背景執行緒配 `CancellationToken`，`http.fetch()` 直接阻塞回傳。
- **限制要設滿**：`TimeoutInterval`、`LimitMemory`、`MaxStatements`、`LimitRecursion`。
  少設一個，一句 `while (true)` 就吃掉一條執行緒。
- **錯誤要歸屬清楚**：例外訊息帶外掛 id 與行號，寫進建置日誌，不要讓服務吐 500。
- **不要曝露 CLR 物件**：只傳 JSON 可序列化的值進出。守住這條，攻擊面就只剩自己寫的那幾個 API。
- **相容性**：manifest 的 `apiVersion` 硬檢查，host API 加欄位可以，改語意要升版。

## Manifest 草案

```jsonc
{
  "id": "acme.jira",
  "name": "Jira 通知",
  "version": "1.0.0",
  "apiVersion": 1,
  "kind": "notification",
  "entry": "index.js",
  "permissions": {
    "http": ["https://acme.atlassian.net"],
    "secrets": ["JIRA_TOKEN"],
    "kv": true
  }
}
```

`secrets` 的語意很關鍵：外掛只能說「把 `JIRA_TOKEN` 放進這個請求的 header」，
**拿不到值本身**。否則外掛可以把權杖 POST 到任何地方。

儲存方式沿用 `ActionDefinitions` 那套（JSON 存資料庫），或放 `data/plugins/`；
兩者都要有啟用／停用與版本欄位。

## host API 草案

```ts
// @walis/plugin-types
export interface ActionPlugin {
  parameters: ParameterDeclaration[]                              // 沿用現有 ActionParameter 形狀
  render(inputs: Record<string, string>, ctx: Context): string    // 回傳 shell 片段
}

export interface NotificationPlugin {
  send(event: BuildEvent, config: Record<string, string>, ctx: Context): void
}

export interface Context {
  log(message: string): void
  http: {
    fetch(url: string, init?: {
      method?: string
      headers?: Record<string, string>
      body?: string
      withSecret?: { name: string; header: string }
    }): HttpResponse
  }
  kv: { get(key: string): string | null; set(key: string, value: string): void }
  shell: { quote(value: string): string }
}
```

`shell.quote` 一定要提供：現有樣板的 `{{name}}` 已經幫使用者處理跳脫，
換成 JS 之後若讓作者自己拼字串，就會冒出一堆注入漏洞。

## 路線圖

| 階段 | 內容 | 估計 |
| --- | --- | --- |
| 1 | Jint 整合、資源限制、host API 骨架、`action` 擴充點 | 2–3 天 |
| 2 | Manifest、安裝與啟停用、權限核准介面（沿用 `ActionDefinitions` 的儲存模式） | 2–3 天 |
| 3 | `notification` 與 webhook 解析擴充點 | 2 天 |
| 4 | `.d.ts` 型別套件、開發指南、範例外掛、測試 | 2 天 |

之後若真的需要 npm 或重運算，再加 Node／Deno sidecar，**host API 形狀不變**，
外掛跑在哪個引擎變成部署細節。

## 先不做

- **UI 擴充**（外掛塞自己的畫面）：Jenkins 這塊是無盡的相容性泥沼，先用宣告式參數表單撐著。
- **外掛市集與自動更新**：先讓「貼一段 JSON 或上傳一個 zip」能用，再談分發。
- **外掛之間互相依賴**：一旦允許，版本解析問題就整包搬過來。

## 待決定

- 外掛的儲存位置：資料庫（同 `ActionDefinitions`）還是 `data/plugins/` 目錄？
  前者跟著備份走，後者比較好用 git 管理。
- 遠端執行器怎麼取得外掛程式碼：認領工作時一起帶回去，還是另外同步？
- 逾時與資源上限要不要開放在系統設定調整，還是寫死。
- 受限等級的外掛能不能讀環境變數（含機密）——傾向不行，只能透過 `withSecret` 間接使用。

## 實作備註（2026-09-05）

實際做出來的東西與上面的規劃一致，幾個值得記下的決定：

- **參數宣告放在 manifest，不是 JS。** 列動作清單是很頻繁的操作，
  如果參數要靠執行 JS 才拿得到，光開一次流程編輯器就得跑上幾十次直譯器。
- **`ActionScriptCompiler` 不認識外掛。** 它多收一個 `IActionRenderer`：
  樣板動作走 `TemplateActionRenderer`，外掛動作走 API 層的 `PluginActionRenderer`。
  編譯器維持純資料處理，外掛的存在只是換一個渲染器。
- **外掛先一次載入好再編譯。** host API 是同步的，如果渲染時才去查資料庫就會出現 sync-over-async；
  `PluginActionRendererFactory` 在編譯前把啟用中的外掛讀進字典。
- **資源上限四道都設滿**：逾時 10 秒、記憶體 8 MB、語句數 50 萬、遞迴 64 層。
  實測無窮迴圈通常先撞到語句數上限，訊息會直接說「可能有無窮迴圈」。
- **沙箱靠「不給」而不是「擋」**：Jint 沒有 `require`、`process`、`fs`、`fetch`，
  外掛能碰到的就是掛上去的 `ctx.log`、`ctx.shell.quote`、`ctx.http.fetch` 與 `ctx.variables`。
  測試逐一確認那些全域物件都是 `undefined`。
- **機密只能用不能讀**：`ctx.http.fetch(url, { withSecret: { name, header } })` 會由 host 把值填進標頭，
  外掛拿不到內容。沒在 manifest 宣告的名稱直接拒絕。
- **渲染失敗記成編譯錯誤**，不會炸掉整份流程——一個壞掉的外掛只讓那一步失敗。

還沒做的：`notification` 與 webhook 擴充點、`kv` 能力、`.d.ts` 型別套件、外掛開發指南。
