# Walis

**一台機器就跑得動的 CI/CD。**

[English](README.md) · [walis.io](https://walis.io)

Walis 幫你跑建置腳本、留下日誌、出事的時候通知你。不需要 repo、不需要容器、
不需要先裝執行環境——發行檔就是一個包含管理介面的獨立執行檔。它還會自己更新自己。

```bash
./Walis.Api --urls http://0.0.0.0:11120
# 瀏覽器開 http://<這台機器>:11120
```

---

## 為什麼還要一套 CI

| | Jenkins | Woodpecker | Walis |
| --- | --- | --- | --- |
| 前提 | JVM 與外掛生態 | Git forge 與容器 | 一台機器跟一段 shell |
| 一個任務是 | repo 裡的 pipeline | repo 裡的 pipeline | 一段 shell，或 repo 裡的 pipeline |
| 安裝體積 | 數百 MB 加外掛 | 容器映像 | 一個執行檔 |
| 外掛 | Java，跑在伺服器裡 | 沒有 | JavaScript，關在沙箱裡，回傳 shell |

Walis 針對的是「Jenkins 太重、cron 又不夠」的情況：幾台機器、幾個任務，
以及一個想知道到底跑了什麼的人。

## 安裝

全新安裝第一次就能用：從任何一台機器用 HTTP 開啟、建立第一位管理員、執行建置。
從其他機器設定時，要輸入伺服器啟動時印出的設定碼。[docs/self-hosting.zh-TW.md](docs/self-hosting.zh-TW.md)
說明設定碼在哪裡，以及開放給可信任網路以外的人之前該收緊的設定（HTTPS、主機名稱、建置隔離）。

### 一行安裝

```bash
curl -fsSL https://walis.io/install.sh | sh
```

它會判斷平台、下載對應的發行檔、先驗 `SHA256SUMS.txt` 的 Ed25519 簽章（見[驗證下載](#驗證下載)）、
再比對校驗碼才裝起來。
要把某台機器當建置機（例如 Raspberry Pi），改裝執行器並註冊成服務：

```bash
curl -fsSL https://walis.io/install.sh | sudo sh -s -- \
  --runner --service --server http://walis.internal:11120 --name pi-01 --token <金鑰>
```

`--service` 會寫一份 systemd unit 並啟動；不加就只是裝好，自己決定什麼時候跑。
Windows 不走這支腳本，請下載下面的 zip。

`<金鑰>` 是綁定這個執行器名稱的 API 金鑰，建立方式見
[遠端執行器](docs/self-hosting.zh-TW.md#遠端執行器)。

### 下載發行檔

每個壓縮檔都是 self-contained，目標機器不必先裝 .NET。

| 平台 | 伺服器 | 執行器 |
| --- | --- | --- |
| Windows x64 | `walis-<版本>-win-x64.zip` | `walis-runner-<版本>-win-x64.zip` |
| Windows ARM64 | `walis-<版本>-win-arm64.zip` | `walis-runner-<版本>-win-arm64.zip` |
| macOS Intel | `walis-<版本>-osx-x64.tar.gz` | `walis-runner-<版本>-osx-x64.tar.gz` |
| macOS Apple Silicon | `walis-<版本>-osx-arm64.tar.gz` | `walis-runner-<版本>-osx-arm64.tar.gz` |
| Linux x64 | `walis-<版本>-linux-x64.tar.gz` | `walis-runner-<版本>-linux-x64.tar.gz` |
| Linux ARM64（含裝 64 位元系統的 Raspberry Pi 4／5） | `walis-<版本>-linux-arm64.tar.gz` | `walis-runner-<版本>-linux-arm64.tar.gz` |
| Linux ARM 32 位元（32 位元系統的 Raspberry Pi） | `walis-<版本>-linux-arm.tar.gz` | `walis-runner-<版本>-linux-arm.tar.gz` |
| Alpine／musl x64 | `walis-<版本>-linux-musl-x64.tar.gz` | `walis-runner-<版本>-linux-musl-x64.tar.gz` |
| Alpine／musl ARM64 | `walis-<版本>-linux-musl-arm64.tar.gz` | `walis-runner-<版本>-linux-musl-arm64.tar.gz` |

```bash
tar -xzf walis-v1.0.0-linux-arm64.tar.gz
cd walis-v1.0.0-linux-arm64
./Walis.Api --urls http://0.0.0.0:11120
```

Raspberry Pi 很適合當建置機：伺服器跑在桌機或 VM 上，Pi 上放**執行器**，
需要真正 ARM 硬體的建置就丟過去跑。

```bash
WALIS_TOKEN=<金鑰> ./walis-runner --server http://walis.internal:11120 --name pi-01 --labels linux,arm64
```

### 驗證下載

每個版本都附 `SHA256SUMS.txt`、它的 Ed25519 簽章 `SHA256SUMS.txt.sig`，以及 `RUNTIME.txt`
（壓縮檔內嵌的 .NET 執行階段版本）。安裝腳本會先驗簽章才相信校驗碼；要自己驗需要 OpenSSL 1.1.1
以上（macOS 內建的 LibreSSL 不支援 Ed25519，請 `brew install openssl@3`）。

公鑰是 [`deploy/release-signing.pub`](deploy/release-signing.pub)，SHA-256 指紋（對 DER 編碼的公鑰計算）：

```
2cecab2e647ee5e43c5f80a74961196af50d0eb195fb2a8563a96aade5aebbbf
```

```bash
# 1. 確認手上的公鑰就是上面那把
openssl pkey -pubin -in release-signing.pub -outform DER | openssl dgst -sha256
# 2. 先驗校驗檔，再驗壓縮檔
openssl pkeyutl -verify -pubin -inkey release-signing.pub -rawin -in SHA256SUMS.txt -sigfile SHA256SUMS.txt.sig
sha256sum -c --ignore-missing SHA256SUMS.txt      # macOS 用 shasum -a 256 -c
```

壓縮檔、校驗碼與簽章都放在同一台伺服器，沒有簽章的話，被換掉的發行檔可以附上一份對得起來的
`SHA256SUMS.txt`。金鑰由維護者離線保管；換鑰時會在 CHANGELOG 公告，並以舊鑰簽的一版帶出新鑰。

自己打包：

```bash
./deploy/release.sh                                   # 全部平台
./deploy/release.sh --targets linux-arm64,linux-arm   # 只做 Raspberry Pi 那兩種
./deploy/release.sh --runner-only --targets linux-arm64
./deploy/release.sh --list
```

### 從原始碼跑

需要 .NET 10 SDK 與 Node.js 20 以上。

```bash
dotnet run --project source/Walis.Api --no-launch-profile -- --urls http://127.0.0.1:11126
npm --prefix source/Walis.Admin run dev     # 介面在 http://127.0.0.1:11124
```

### 容器

目前沒有現成的 compose 檔。開發時使用的 Dockerfile 與它們的前提，見
[docs/self-hosting.zh-TW.md](docs/self-hosting.zh-TW.md#容器)。

## 它能做什麼

**任務與建置**

- 一個任務是一段 shell，或是在介面上組出來的動作流程；兩種最後都編譯成同一段腳本。
- 建置切成**步驟**，每一步各自記錄狀態、輸出、結束碼與耗時。某一步失敗時後續標記為略過，
  除非那一步設定了失敗仍繼續。
- 執行中**即時串流輸出**，本機與遠端執行器都一樣。
- **矩陣建置**：`{"NODE":["20","22"],"OS":["mac"]}` 會把一次觸發展開成多筆建置，
  各自把自己的組合注入成環境變數。
- **平行步驟**：把某一步標記成與前一步同時執行。同一組共用工作目錄，但各自有獨立的環境變數檔。
- **快取**：列出來的路徑在建置成功後保存，下一次建置先還原。
- **產出檔案**：符合條件的檔案會從工作目錄複製出來，跟著建置紀錄一起留著。

**觸發方式**

手動、Cron，或推送 webhook——接受 GitHub、GitLab 與單純的 `{repository, branch}`。

下游觸發以上游任務的執行角色為權限上限：operator 任務不能啟動僅限 admin 的部署，
即使是管理員手動啟動上游也一樣。需要這類串接時，由管理員將上游執行角色設為 admin；
只有管理員能指定此角色，推進下游時也會再次檢查權限變更。

**執行器**

伺服器內建一個本機執行器。另一台機器上的 `walis-runner` 會輪詢認領指派給它的工作、
逐步回報、上傳產出檔案。兩邊跑的是同一份步驟迴圈，行為一致。

**隔離**

所有建置在清理、快取或執行前都會驗證工作目錄白名單、實體路徑、符號連結與受保護目錄
是否重疊；子程序只繼承白名單內的環境，不會拿到 controller 的連線字串或其他服務機密。
macOS 上每個步驟另外包在 `sandbox-exec` 裡。沒有可用 OS 沙箱的地方（Linux、Windows 與遠端執行器）
預設以未隔離方式執行，並在建置日誌註明。管理員也可以改成要求隔離邊界：這些平台就會拒絕執行，
除非執行器放在專用的 VM 或容器裡並由兩端宣告；做法見[建置隔離](docs/self-hosting.zh-TW.md#建置隔離)。

**外掛**

外掛是 JavaScript，由 Jint 在伺服器裡執行，有時間、記憶體、語句數與遞迴層數的上限。
它**回傳一段 shell**，而不是自己去做事，所以安裝之前你就看得到它到底會跑什麼。
連外只放行 manifest 裡宣告的網域，機密只能經由標頭填入——外掛讀不到值。
開發用 [`@walisayu/plugin-sdk`](https://www.npmjs.com/package/@walisayu/plugin-sdk)。

**給 AI 代理用**

API 本身在 `/mcp` 講 MCP，Claude Code、Codex 或任何 MCP 用戶端只要一個網址加一把金鑰，
就能觸發建置、等結果、讀日誌，本機什麼都不用裝。金鑰有用途與角色限制，資料庫只存
固定成本的 SHA-256 lookup hash；舊版 PBKDF2 API 金鑰升級時會停用，必須輪替一次。

**維運**

角色權限（管理員／操作者／檢視者）加上每個任務各自的檢視與執行角色、一次性密碼、
稽核紀錄、`/metrics` 的 Prometheus 指標、電子郵件／Slack／Webhook 通知，
以及繁體中文、英文、日文三種介面語言（翻譯可以在介面裡直接改）。

## 資料庫與多節點

預設是 SQLite，一個檔案，什麼都不用設。要做高可用就換成
**PostgreSQL、MySQL／MariaDB 或 SQL Server**，然後掛多個 API 節點：

```bash
Database__Provider=postgresql
Database__Node=web-01
ConnectionStrings__Walis='Host=db.internal;Database=walis;Username=walis;Password=…'
```

節點之間不互相通訊。建置用一句帶條件的 UPDATE 認領，只有一個節點拿得到；
每個節點只回收自己中斷的建置；Cron、離線偵測、清理與排程備份靠租約決定由誰做，
整個叢集只做一次。細節見 [docs/database.md](docs/database.md)。

## 備份與還原

排程備份會打包資料庫、產出檔案與你指定的額外路徑，強制使用至少 16 個字元的通關密語，
並以版本化、分塊 AES-256-GCM 認證加密後，上傳到本機路徑、S3 相容儲存、Dropbox、
Google Drive 或 OneDrive。每個區塊與結尾都會驗證，竄改或截斷的檔案不會進入還原流程；
舊版 OpenSSL `Salted__` CBC 檔僅保留離線解密遷移能力，API 不再接受。

資料庫是以**與資料庫無關的格式**匯出的，所以同一份備份可以還原到任何一種支援的資料庫——
從 SQLite 搬到 PostgreSQL 走的就是這條路。還原在管理介面上做，或呼叫 `POST /api/backup/restore`。

## 專案結構

| 路徑 | 內容 |
| --- | --- |
| `source/Walis.Api` | 最小 API、HTTP 端點、建置工作者、MCP 伺服器 |
| `source/Walis.Core` | 領域模型、資料層與方言、步驟執行、外掛、備份 |
| `source/Walis.Admin` | Vue 3 + TypeScript 管理介面 |
| `source/Walis.Runner` | `walis-runner`，遠端執行器 |
| `source/Walis.Mcp` | 給只支援 stdio 的用戶端的 MCP 轉接程式 |
| `source/walis-plugin-sdk` | `@walisayu/plugin-sdk`——型別、測試 harness、打包與 CLI |
| `source/Walis.Tests` | xUnit 測試 |
| `deploy/` | 安裝、發行打包與服務定義 |

## 開發

```bash
dotnet build Walis.slnx
dotnet test source/Walis.Tests/Walis.Tests.csproj
npm --prefix source/Walis.Admin run typecheck
npm --prefix source/Walis.Admin run build
./deploy/lint.sh
```

資料庫測試預設只跑 SQLite。設了 `WALIS_TEST_POSTGRES`、`WALIS_TEST_MYSQL`
或 `WALIS_TEST_SQLSERVER`，同一批案例就會在那些資料庫上再跑一次。

## 文件

| 文件 | 內容 |
| --- | --- |
| [docs/database.md](docs/database.md) | 資料庫、方言、多節點、搬家 |
| [docs/self-hosting.zh-TW.md](docs/self-hosting.zh-TW.md) | 第一位管理員、對外連線與 HTTPS、建置隔離、遠端執行器 |
| [docs/actions.md](docs/actions.md) | 動作樣板與開發方式 |
| [docs/plugins.md](docs/plugins.md) | 外掛的設計與取捨 |
| [docs/plugin-development.md](docs/plugin-development.md) | 外掛開發與威脅模型 |
| [docs/plugin-sdk.md](docs/plugin-sdk.md) | npm SDK |
| [docs/ci-comparison.md](docs/ci-comparison.md) | Jenkins 與 Woodpecker 的缺點盤點 |

## 授權

MIT，見 [LICENSE](LICENSE)。
