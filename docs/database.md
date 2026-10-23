# 資料庫與多節點部署

Walis 支援四種資料庫：**SQLite（預設）、PostgreSQL、MySQL／MariaDB 與 SQL Server**。
單機用 SQLite 就夠了；要讓多個 API 節點分擔流量、其中一台掛掉還能繼續服務，就得換成
另外三種——SQLite 是單一檔案，多個節點同時寫會壞掉。

## 怎麼設定

連線字串放在 `ConnectionStrings:Walis`，種類放在 `Database:Provider`。
種類沒寫時由連線字串推斷（看得到 `Host=` 當成 PostgreSQL、`Uid=` 當成 MySQL、
`Initial Catalog=` 當成 SQL Server），推不出來就是 SQLite。

```jsonc
{
  "Database": {
    "Provider": "postgresql",   // sqlite | postgresql | mysql | sqlserver
    "Node": "web-01"            // 多節點時每台不同；沒寫就用機器名稱
  },
  "ConnectionStrings": {
    "Walis": "Host=db.internal;Database=walis;Username=walis;Password=…"
  }
}
```

環境變數的寫法（部署成服務時比較常用）：

```bash
Database__Provider=postgresql
Database__Node=web-01
ConnectionStrings__Walis='Host=db.internal;Database=walis;Username=walis;Password=…'
```

各家的連線字串格式：

| 資料庫 | 驅動 | 範例 |
| --- | --- | --- |
| SQLite | Microsoft.Data.Sqlite | `Data Source=data/walis.db` |
| PostgreSQL | Npgsql | `Host=127.0.0.1;Port=5432;Database=walis;Username=walis;Password=…` |
| MySQL／MariaDB | MySqlConnector | `Server=127.0.0.1;Port=3306;Database=walis;Uid=walis;Pwd=…` |
| SQL Server | Microsoft.Data.SqlClient | `Server=127.0.0.1,1433;Database=walis;User Id=sa;Password=…;TrustServerCertificate=true` |

資料庫本身要先建好（`CREATE DATABASE walis;`），資料表由 Walis 啟動時自己建。
版本需求：PostgreSQL 12+、MySQL 8.0.13+（要函式索引）或對應的 MariaDB、SQL Server 2016+。

啟動後可以用 `/health` 確認連到哪裡：

```json
{ "status": "ok", "database": "PostgreSQL", "node": "web-01", "clustered": true }
```

## 結構怎麼來的

結構描述寫在 `Walis.Core/Database/DatabaseSchema.cs`，是一份與資料庫無關的資料：
哪些表、哪些欄位、什麼型別、哪些索引。`DatabaseInitializer` 啟動時比對現況——
缺表就建、缺欄位就 `ALTER TABLE`、缺索引就補，四種資料庫共用同一份定義，
不需要另外寫搬遷腳本。**加欄位就直接加進 `DatabaseSchema`**，但一定要可為 NULL
或帶預設值，否則既有的列填不出來。

型別對應：

| 抽象型別 | SQLite | PostgreSQL | MySQL | SQL Server |
| --- | --- | --- | --- | --- |
| Guid | TEXT | uuid | char(36) | uniqueidentifier |
| Text | TEXT | text | longtext | nvarchar(max) |
| ShortText(n) | TEXT | varchar(n) | varchar(n) | nvarchar(n) |
| Timestamp | TEXT | timestamptz | datetime(6) | datetime2(3) |
| Bool | INTEGER | boolean | tinyint(1) | bit |

## 方言差在哪

差異全部收在 `ISqlDialect`，Repository 不直接寫會綁死一種資料庫的語法：

| 需求 | SQLite | PostgreSQL | MySQL | SQL Server |
| --- | --- | --- | --- | --- |
| 限制筆數 | `LIMIT n` | `LIMIT n` | `LIMIT n` | `OFFSET 0 ROWS FETCH NEXT n ROWS ONLY` |
| 時間差（秒） | `julianday` | `EXTRACT(EPOCH …)` | `TIMESTAMPDIFF` | `DATEDIFF_BIG` |
| 寫入或更新 | `ON CONFLICT` | `ON CONFLICT` | `ON DUPLICATE KEY` | `MERGE` |
| 字串串接 | `\|\|` | `\|\|` | `CONCAT` | `+` |
| 布林常值 | `1` / `0` | `TRUE` / `FALSE` | `1` / `0` | `1` / `0` |
| 計數（64 位元） | `COUNT(1)` | `COUNT(1)` | `COUNT(1)` | `COUNT_BIG(1)` |
| 認領時跳過鎖住的列 | 靠條件式 UPDATE | `FOR UPDATE SKIP LOCKED` | `FOR UPDATE SKIP LOCKED` | `WITH (UPDLOCK, READPAST)` |

幾個容易踩到的地方，都已經在測試裡釘住：

- **秒數要是浮點數**。PostgreSQL 的 `EXTRACT` 回 numeric、SQL Server 的除法回 decimal，
  不轉成 double 的話 Dapper 對不上 `BuildCounts`，總覽整頁會 500。
- **布林不能寫成 1／0**。PostgreSQL 的 boolean 不吃整數，`IsDisabled = 0` 會直接報型別錯。
- **帳號查詢一律 `LOWER()` 比對**。PostgreSQL 區分大小寫，其餘三種預設不分；
  另外在 PostgreSQL 上多建一個 `LOWER(Username)` 的唯一索引擋住 `Leo` 與 `leo` 並存。
- **環境變數的唯一性**（同一個名稱，全域一個、每個任務各一個）在 SQLite／PostgreSQL／
  SQL Server 用條件索引，MySQL 沒有條件索引，改用 `IFNULL(JobId, '')` 的函式索引。

## 多節點（HA）

多個 API 節點連同一個資料庫時，有三件事要協調，Walis 都做在資料庫裡，
不需要額外的協調服務：

1. **建置只能有一個節點跑**。認領是一句帶條件的 UPDATE
   （`SET Status='Running' WHERE Id=… AND Status='Queued'`），只有改到列的那個節點拿得到工作，
   搶輸的換下一筆。支援 `SKIP LOCKED` 的資料庫會在挑選時就先跳過別人鎖住的列，少做白工。
2. **重啟只能回收自己的建置**。`Builds.Node` 記著是哪個節點在跑，節點重啟時只把自己的
   標記為失敗——否則新節點一上線就會把別台正在跑的建置判死。
3. **排程整個叢集只做一次**。Cron 觸發、離線偵測、舊資料清理與排程備份都要搶
   `Leases` 表裡的租約（有效期三分鐘，每分鐘續約）。持有者掛掉之後租約過期，
   別的節點自然接手。SQLite 不必搶，直接算自己是。

   代價是**接手要等租約過期**：持有者突然斷電時，接下來大約兩到三分鐘的 Cron 不會觸發，
   之後才由別的節點接手。實測是 131 秒。分鐘級的排程可以接受這個空窗；
   不能接受的話，排程本來就不該用 Cron 表示。

節點之間不互相通訊，唯一的共用狀態就是資料庫。前面擺一台反向代理輪詢分流即可；
不需要 sticky session——工作階段與登入時的一次性密碼挑戰（只存 lookup hash）、登入節流與
webhook replay 狀態都存在資料庫，
密碼那一步跟輸入驗證碼那一步被分到不同節點也沒問題。

執行器（`walis-runner`）跟這件事是分開的：它本來就是遠端認領工作，
換成哪種資料庫、幾個 API 節點都不影響。

### 哪些是共用的、哪些是各節點自己的

| 東西 | 共用 | 說明 |
| --- | --- | --- |
| 任務、建置、步驟、日誌 | 是 | 都在資料庫 |
| 工作階段、API 金鑰、一次性密碼挑戰與濫用防護 | 是 | token 只存 lookup hash；限流／replay 跨節點，不需要 sticky session |
| 排程、離線偵測、清理、排程備份 | 是 | 靠租約，整個叢集做一次 |
| 建置編號 | 是 | 取號前先鎖住任務那一列，同一個任務排成一列發號 |
| **同時建置數上限** | 否 | 是**每個節點**的上限；三個節點設 2 就是最多同時六個 |
| **產出檔案與建置快取** | 否 | 落在執行那台的 `data/`，見下 |
| CORS 允許來源 | 最終一致 | 每個節點每分鐘重讀一次設定，別台改的最慢一分鐘跟上 |

**產出檔案要放共用儲存。** `data/artifacts` 與 `data/cache` 是本機目錄，建置在哪個節點跑，
檔案就留在那台。多節點時把 `data/` 掛在共用儲存（NFS、SMB 或雲端硬碟掛載）上，
不然從別的節點下載會拿不到——這種情況介面會直接告訴你檔案在哪個節點，而不是含糊地說不存在。

**內建執行器在叢集裡是同一個名稱。** 每個節點都以 `local` 回報心跳，執行器列表只會看到一列，
只要還有一個節點活著就顯示在線。要區分是哪台跑的，看建置的「節點」欄位。

## 備份與還原

備份包裡的資料庫是一份與資料庫無關的邏輯匯出（`walis/walis-data.jsonl`，
每行一列資料的 JSON Lines）。所以**同一份備份可以還原到任何一種支援的資料庫**——
從 SQLite 搬到 PostgreSQL 走的就是這條路。SQLite 另外會附一份 `walis/walis.db`
檔案複本，不必經過 Walis 也能直接開。

還原在管理介面的「設定 → 備份 → 從備份還原」，或直接打 API：

```bash
curl -X POST http://127.0.0.1:11120/api/backup/restore \
  -H 'Authorization: Bearer <管理員權杖>' \
  -H 'Content-Type: application/json' \
  -d '{"fileName":"walis-20260905-0200.tar.gz.enc","includeArtifacts":true}'
```

- 只接受**備份目錄裡的檔名**，不接受路徑。
- 通關密語留空時沿用設定裡的那一組。
- 還原會在同一個交易裡取代持久資料，中途失敗不會留下半套。現行的 session、OTP 挑戰、節流狀態與租約不會從備份復活。
- 還原會取得全叢集維護租約，阻止任何新建置開始；若仍有建置在執行則拒絕。為避免舊執行器在還原後回寫，舊版 `force: true` 也不再繞過這項檢查。
- 手動備份、排程備份與還原共用可續租的全叢集互斥鎖，不會同時讀寫備份狀態。
- 管理員限定，由 `AuthenticationMiddleware` 擋。
- **機密欄位在匯出檔裡是密文**（`enc:v1:` 前綴）：`AiProviders.ApiKey`、`EnvironmentVariables.Value`（IsSecret）、
  `Settings.Payload` 裡的密碼與金鑰欄位、`Builds.SecretParametersJson`、`Users.TotpSecret` 都以
  `ISecretProtector` 在 repository 進出時加解密，匯出不解密、匯入也不重新加密。還原到另一台機器前要先把
  `<資料目錄>/secret.key` 複製過去，或兩邊設同一組 `Security:EncryptionKey`；沒帶金鑰時啟動日誌會列出解不開的項目，
  可由 `POST /api/settings/secrets/reset-undecryptable` 只清掉那些值。細節見 `docs/self-hosting.zh-TW.md` 的「機密加密」。

## 從 SQLite 搬到 PostgreSQL

1. 在舊環境按「立即備份」，或呼叫 `POST /api/backup/run`。
2. 把舊環境資料目錄的 `secret.key` 複製到新環境的資料目錄（或兩邊設同一組 `Security:EncryptionKey`），否則加密的機密還原後解不開。
3. 新環境設好 `Database:Provider` 與連線字串後啟動，讓它把結構建起來。
4. 把備份檔放進新環境的備份目錄，呼叫 `POST /api/backup/restore`。
5. 對一下任務數與建置數，然後把舊環境停掉。

要一次上多個節點的話，先讓一個節點跑完還原，再啟動其餘節點。

## 測試

單元測試預設只跑 SQLite。設了環境變數就會連上去多跑那幾種，
測的是同一批案例（`source/Walis.Tests/DatabaseProviderTests.cs`）：

```bash
export WALIS_TEST_POSTGRES="Host=127.0.0.1;Port=5432;Database=walis_test;Username=…;Password=…"
export WALIS_TEST_MYSQL="Server=127.0.0.1;Port=13306;Database=walis_test;Uid=root;Pwd=…;AllowPublicKeyRetrieval=True;SslMode=None"
export WALIS_TEST_SQLSERVER="Server=127.0.0.1,11433;Database=walis_test;User Id=sa;Password=…;TrustServerCertificate=true"
dotnet test source/Walis.Tests/Walis.Tests.csproj
```

本機要開測試用的資料庫：

```bash
docker run -d --name walis-test-mysql -e MYSQL_ROOT_PASSWORD=… -e MYSQL_DATABASE=walis_test \
  -p 13306:3306 mysql:8.4
docker run -d --name walis-test-mssql --platform linux/amd64 -e ACCEPT_EULA=Y \
  -e MSSQL_SA_PASSWORD=… -p 11433:1433 mcr.microsoft.com/mssql/server:2022-latest
```

外部資料庫的測試會把 Walis 的資料表整批砍掉重建，**不要指向正式資料庫**。
