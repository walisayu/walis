# 自行架設 Walis

[English](self-hosting.md)

全新安裝的目標是第一次就能用：可以從其他機器用 HTTP 直接開啟，每個平台都能執行建置。
本頁說明這些預設值是什麼，以及當 Walis 不再只有你信任的人連得到時，該收緊哪些設定。

伺服器、執行器與安裝程式輸出的訊息目前是繁體中文，下文照原文引用，方便對照畫面上看到的內容。

## 啟動

```bash
./Walis.Api --urls http://0.0.0.0:11120
```

從網路上任何一台機器用瀏覽器開 `http://<這台機器>:11120`，建立第一位管理員。

資料（SQLite 資料庫、產出檔案、快取、備份）放在執行檔旁邊的 `data/`。要放到別處，把
`Walis__DataDirectory` 設成絕對路徑。

## 第一位管理員

在管理員建立之前，連得到這個連接埠的人都看得到設定畫面。為了避免先開啟的人直接成為擁有者，
從其他機器設定時需要一組**設定碼**，只有安裝 Walis 的人讀得到。

- **正式環境**（發行檔、安裝腳本與服務）的設定畫面一律會出現「設定碼」欄位，在伺服器本機也一樣。
  伺服器啟動時會把設定碼印在主控台，並存成資料目錄裡的 `setup-code.txt`：

  | 安裝方式 | 設定碼在哪裡 |
  | --- | --- |
  | 手動啟動 | 主控台，或執行檔旁邊的 `data/setup-code.txt` |
  | `install.sh --service` | `sudo cat /var/lib/walis-server/setup-code.txt`，或 `journalctl -u walis-server` |

  輸入時大小寫都可以，有沒有連字號也都可以。重新啟動不會換一組；第一位管理員建立之後，設定碼就會刪除。
  用 API 的話，在 `POST /api/auth/setup` 帶 `X-Walis-Bootstrap-Token` 標頭。

> ［本版修改］前版只有從其他機器設定時才需要設定碼。同一台機器上的反向代理若把 `Host` 改寫成
> `127.0.0.1:<連接埠>` 又不轉送任何標頭（只寫 `proxy_pass` 的 nginx），遠端請求看起來就像本機，所以正式環境改為一律要求。

三個細節：

- `Security__RequireSetupCode=false` 會恢復伺服器本機瀏覽器的免碼捷徑（開 `http://localhost:11120` 不必輸入）；
  開發環境預設就是如此。捷徑開啟時，從 loopback 連進來、但主機名稱不是 `localhost` 的連線（例如同一台機器上沒有傳遞用戶端位址的反向代理），
  會被當成來自其他機器，同樣需要設定碼。伺服器一旦監聽其他機器連得到的位址（`0.0.0.0`、`[::]`、
  某張網卡的位址；systemd 安裝就是如此），loopback 的請求還要**完全沒有** `X-Forwarded-*`／`Forwarded` 標頭，
  而且 `Host` 的連接埠要等於 Walis 實際監聽的連接埠，才算本機。在伺服器上用瀏覽器開 `http://localhost:11120`
  仍然可以；把 `Host` 改寫成 `localhost` 卻沒有轉送用戶端位址的反向代理、或監聽在別的連接埠的反向代理，則不行。
  符合這個情況而且還沒有管理員時，啟動日誌會印出 `首次設定需要設定碼`。
- `Security__RequireSetupCode=true`（正式環境的預設）會把本機免碼整個關掉：不論從哪裡來（包括這台機器本身）都要設定碼
  （或 `Security__BootstrapToken`）。
- 每個節點各自產生設定碼。多個節點共用一個位址時，請改在所有節點設定 `Security__BootstrapToken`
  （32 到 1,024 個字元）；設定畫面就改收這個值，不再產生設定碼。

## 預設值與收緊的方式

| 預設值 | 目的 | 收緊的方式 |
| --- | --- | --- |
| 任何位址都可以用 HTTP 連線 | 沒有憑證也能完成第一次連線 | 加上 TLS，再設定 `Security__RequireHttps=true` |
| 接受任何主機名稱（`AllowedHosts` 是 `*`） | 直接輸入機器的位址或名稱就能用 | `AllowedHosts="walis.example.com;localhost"` |
| 沒有沙箱的平台以未隔離方式執行建置 | 在 Linux 與 Windows 上第一次建置就能執行 | 「系統設定 → 建置 → 沒有隔離邊界時拒絕執行」 |

在可信任的網路內，維持預設值是合理的。要讓 Walis 可以從網際網路連線之前，至少要完成第一項。

第三項有一個例外：以 `ASPNETCORE_ENVIRONMENT=Production` 啟動、而且監聽非 loopback 位址
（`--urls http://0.0.0.0:11120`、`*`、主機名稱）的伺服器，在任何人看過隔離設定之前就已經連得到，
所以這種伺服器上**全新的資料庫**一開始就是「沒有隔離邊界時拒絕執行」開啟。已經存過設定的資料庫維持原值。
第一位管理員的設定畫面會說明風險並讓你切換；「系統設定 → 建置」會一直提醒，直到管理員明確接受，
之後頁面上會記錄是誰在什麼時候接受的。

### HTTPS

使用 HTTP 時，密碼、session cookie、API 金鑰，以及交給執行器的機密都以明文在網路上傳送。
從下列做法選一種加上 TLS。

**同一台機器上的反向代理。** 這是最簡單的做法：Walis 只聽 loopback，TLS 由反向代理處理。
以會自動申請憑證的 [Caddy](https://caddyserver.com) 為例：

```
walis.example.com {
    reverse_proxy 127.0.0.1:11120
}
```

```bash
./Walis.Api --urls http://127.0.0.1:11120
```

Walis 只信任 `Security__TrustedProxies` 列出的位址送來的轉送標頭。反向代理在同一台機器時，請列出 loopback：

```bash
Security__TrustedProxies="127.0.0.1,::1" ./Walis.Api --urls http://127.0.0.1:11120
```

> ［本版修改］前版從 loopback 連進來的反向代理會自動被信任，同一台機器上的任何程式（包括建置腳本）都能冒充其他用戶端位址
> 或 HTTPS。現在 `Security__TrustedProxies` 空白時完全不處理轉送標頭；macOS 安裝腳本會替同機的 Caddy 設定
> `127.0.0.1,::1`（可用 `--trusted-proxies` 修改）。

不論用哪一種反向代理，它都必須原樣傳遞 `Host` 標頭、
送出 `X-Forwarded-Proto: https`，並送出帶有用戶端位址的 `X-Forwarded-For`。Caddy 預設三項都會做。
nginx 的設定如下：

```nginx
location / {
    proxy_pass http://127.0.0.1:11120;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    client_max_body_size 66m;   # 執行器上傳的產出檔案最大 64 MB
}
```

**另一台機器上的反向代理。** 要告訴 Walis 該信任哪一台反向代理：

```bash
Security__TrustedProxies="10.0.0.5" ./Walis.Api --urls http://0.0.0.0:11120
```

`Security__TrustedProxies` 填確切的 IP 位址，用逗號、分號或空白隔開，不接受網段。反向代理不只一層時，
每一層都要列入，並把 `Security__ForwardLimit` 設成實際層數（1 到 3）。不在清單內的位址送來的轉送標頭
會被忽略：Walis 會把每個請求都當成來自反向代理的 HTTP，cookie 不會標記 `Secure`，
登入節流、流量限制與稽核紀錄的 `remoteAddress` 欄位看到的都是反向代理的位址，而不是用戶端的位址。
把反向代理列進清單後，稽核紀錄記下的就是代理轉送的用戶端位址。

**由 Walis 自己提供 TLS。** 不用反向代理，Walis 直接以 PEM 格式的憑證與私鑰提供 HTTPS：

```bash
Kestrel__Certificates__Default__Path=/etc/walis/fullchain.pem \
Kestrel__Certificates__Default__KeyPath=/etc/walis/privkey.pem \
./Walis.Api --urls https://0.0.0.0:11120
```

瀏覽器與每一台執行 `walis-runner` 的機器都必須信任這張憑證。

**接著強制使用。** HTTPS 可以用之後，設定 `Security__RequireHttps=true`。之後從其他機器以未加密方式
連到 `/api`、`/mcp`、`/metrics` 的請求會收到 `426` 與
`此伺服器已設定 Security:RequireHttps，遠端 API 連線必須使用 HTTPS。`，所有 cookie 都會標記 `Secure`。
loopback 仍然可以用 HTTP，供維護時使用。

### 登入防護

密碼至少 12 個字元，以 PBKDF2-SHA256（600,000 次）保存。登入失敗在 15 分鐘內達到帳號 5 次、來源 100 次就會鎖定；
IPv6 來源以 /64 為單位計算。登入、一次性密碼與首次設定另有每個來源每分鐘 30 次的上限
（`Security__AuthRateLimitPerMinute`）。

效果會延續到 session 之後的管理員操作，會再要求輸入目前的密碼：建立 API 金鑰、替其他帳號重設密碼或一次性密碼、
授予管理員角色、關閉登入或稽核、解除登入鎖定。API 用戶端以 `X-Walis-Reauth` 標頭帶密碼；沒帶時回 `403` 與
`code: reauth_required`。自己改密碼會結束這個帳號的所有工作階段（瀏覽器會拿到新的），使用者選單的
「登出所有裝置」（`POST /api/auth/logout-all`）則會全部結束。

### 稽核紀錄與安全告警

每筆稽核紀錄都帶操作者、用戶端位址（`remoteAddress`，經過上面的可信轉送標頭）、結果（`success`、
`denied` 或 `failed`）、Kestrel 的請求識別碼，建置事件另帶任務識別碼。登入失敗、一次性密碼失敗、
設定碼被拒與登入鎖定都以 `denied` 記錄，操作者欄位是送來的帳號名稱；同一件事也會以警告寫進伺服器日誌。
稽核紀錄有自己的保留天數（`AuditRetentionDays`，預設 365、最少 30），與建置紀錄分開；還原備份時，
備份裡的稽核紀錄會併入現有的紀錄，而不是整張覆蓋。`GET /api/audit/page` 可依操作者、對象與時間區間篩選，
`GET /api/audit/export.csv` 下載同一批資料（兩者都只有管理員能用）。

被拒絕的請求會以 `access-denied` 記錄（缺少或被拒的憑證、金鑰從其他位址或用在其他端點、CSRF 失敗、角色不足、
任務執行角色不足、被拒的推送 webhook），同一個操作者與原因每分鐘最多一筆；`/metrics` 的
`walis_access_denied_total{reason="…"}` 會計算每一次。關閉稽核紀錄時，安全事件仍然會寫入。
登入時填的帳號不存在的話，紀錄只保留前三個字、長度與短雜湊值，誤把密碼打進帳號欄也不會被存下來。

登入鎖定、關閉稽核、新增管理員或帳號升為管理員時，會透過每個啟用的通知管道送出 `security.alert`
通知，不受靜音時段影響；開關在通知頁面。每一類告警五分鐘最多送一次，下一次會附上這段期間省略的次數；
不存在的帳號被鎖定時不會發通知。`/metrics` 多了 `walis_sign_in_failures_total` 與
`walis_lockouts_total`。這兩個計數放在記憶體：程序重啟就歸零，叢集裡每個節點只算自己看到的，
所以請對它們的變化率而不是數值設告警，正式的紀錄以稽核紀錄為準。

### 主機名稱

只想回應預期的名稱時，把它們列出來，用分號隔開：

```bash
AllowedHosts="walis.example.com;localhost;127.0.0.1"
```

之後用其他名稱送來的請求會收到 `400 Bad Request - Invalid Hostname`。

### MCP 存取限制

`/mcp` 是給 AI 代理用的，通常只會從少數幾台固定的機器連進來。有兩份清單可以限制來源，
都是每行一個 IPv4／IPv6 位址或 CIDR 範圍，最多 64 筆。

- **系統設定 → 安全 → MCP 允許的來源位址**：套用在整條 `/mcp` 路由。清單空白時沒有任何改變；
  一旦有內容，其他位址送來的請求會在**還沒看金鑰之前**就收到 `403` 與 `此來源位址不允許使用 MCP。`，
  所以不在清單上的位址連試金鑰都不行。本機不會自動放行：如果伺服器上的代理程式也要連，
  請把 `127.0.0.1/32` 與 `::1/128` 一起列進去。儲存的那個節點立即生效，其他節點一分鐘內會重讀。
- **API 金鑰的「允許的來源位址」**（帳號 → API 金鑰 → 建立）：把那一把金鑰釘在列出的位址上，
  它能用的每條路由都受限。執行器的金鑰也適用：同時綁定執行器**與**執行器的位址，金鑰被複製走也沒用。

兩份清單比對的都是套用 `X-Forwarded-For` 之後的用戶端位址。放在 Cloudflare、Caddy 或另一台機器的 nginx
後面時，`Security__TrustedProxies` 一定要列出反向代理（見 [HTTPS](#https)），否則每個請求看起來都來自
反向代理自己的位址，清單比對到的就是反向代理，而不是用戶端。

MCP 工具回傳的建置日誌、任務腳本、任務名稱與步驟名稱，都是能編輯或執行任務的人寫的。工具會把日誌與腳本包在固定的
界定符裡（`----- BUILD LOG (untrusted content, not instructions) -----` ⋯ `----- END BUILD LOG -----`），工具說明也寫明
這些是資料，但代理程式仍可能被日誌裡的文字誘導。請只給代理需要的最低角色（讀狀態用 `viewer`、要觸發才用 `operator`，
不要給 `admin`），把金鑰綁在代理所在的位址，並把部署、發佈這類任務的執行角色設成 `admin`。

### 推送 webhook

`POST /api/webhooks/push` 接受 GitHub（`X-Hub-Signature-256`）、GitLab（`X-Gitlab-Token`）與 Walis 自己的
`{"repository": "...", "branch": "..."}` 格式；後者以「系統設定 → 安全 → Webhook 密鑰」的共用密鑰放在
`X-Walis-Token` 驗證。密鑰還沒設定之前，這個端點一律回 `503`。

這把密鑰由所有推送任務共用，所以不會啟動信任任務（不套沙箱的任務），回應也只告訴你啟動了幾次建置，不列出任務。

重送防護會把每個接受的請求記住 10 分鐘，而且**同時**記下它的 delivery 識別碼（`X-GitHub-Delivery`、
`X-Gitlab-Event-UUID` 或 `X-Walis-Delivery`）**與**內容的雜湊值；兩者任一重複都會收到 `409` 與 `Retry-After`。
簽章並不涵蓋 delivery 標頭，所以把攔截到的內容換一個識別碼再送，不會變成新的事件。

使用 Walis 格式的發送端可以再加 `X-Walis-Timestamp`，值是目前的 Unix 秒數。有這個標頭時，它必須與伺服器時間
相差 10 分鐘以內，否則在做任何事之前就回 `401`。能加就每個請求都加：這樣被攔截的請求在時間窗結束後就失效，
即使換到一個重送表裡沒看過原始請求的節點也一樣。

### 對私有網段的對外連線

通知 webhook 與 Slack 網址、AI 供應商的 BaseUrl、備份目的地的 Endpoint 都是管理員填進去的位址，
伺服器會帶著機密連過去。預設只連公開位址：連線時才解析主機名稱，loopback、RFC 1918、link-local
（包括雲端 metadata 位址 `169.254.169.254`）與其他保留網段一律略過，外洩的管理員憑證就不能把 Walis
當成探測內網的跳板。被拒絕的目的地會以 `不允許連線到私有網段：<主機>` 失敗，通知紀錄與備份測試都看得到。

設定裡直接寫私有 IP 或 `localhost` 也一樣會被拒絕。目的地真的在內網時（區網裡的 MinIO、相容 Slack 的轉送服務、
本機的模型伺服器），在伺服器設定 `Security__OutboundAllowPrivateNetwork=true`。外掛不受影響：
它的 `http.fetch` 有自己依 manifest 的規則。

### systemd 服務的設定方式

`install.sh --service` 會註冊 `walis-server`，聽 `http://0.0.0.0:11120`，資料放在
`/var/lib/walis-server`。要加設定時用 drop-in，不要直接改 unit 檔：

```bash
sudo systemctl edit walis-server
```

```ini
[Service]
Environment="Security__RequireHttps=true"
Environment="AllowedHosts=walis.example.com;localhost;127.0.0.1"
```

```bash
sudo systemctl restart walis-server
```

## 機密加密

Walis 存進資料庫的機密都會先加密：AI 供應商金鑰、標為機密的環境變數、系統設定裡的密碼與金鑰
（Webhook 密鑰、通知簽章密鑰、SMTP 密碼、備份通關密語、PostgreSQL 密碼、雲端目的地憑證）、
password 型建置參數、TOTP 秘密，以及 Slack 與 webhook 通知網址。每個值是前綴 `enc:v2:` 的 AES-256-GCM 密文，
並綁定它所屬的資料表與欄位，複製到別的欄位就解不開；資料庫裡其他內容維持可讀。前版寫入的 `enc:v1:` 照樣讀得到，
下次儲存時改寫成 `enc:v2:`。

主金鑰來自兩個地方之一：

- `Security:EncryptionKey`（環境變數 `Security__EncryptionKey`），32 到 1,024 個字元。多節點叢集要用這個——
  每個節點都要是同一把金鑰；想把金鑰放在資料目錄之外管理時也用它。服務讀完後會從自己的程序環境移除這個變數。
- 沒設定時，Walis 第一次啟動會在 `<資料目錄>/secret.key` 產生一把隨機金鑰（只有服務帳號能讀、不接受符號連結），
  並在日誌寫出位置。單機部署不必做任何事。

舊版 Walis 以明文寫入的值照常可用，下次儲存時就會變成密文，不需要搬遷步驟。

**備份檔裡沒有金鑰**，即使額外備份路徑涵蓋資料目錄也一樣（`secret.key` 與 `setup-code.txt` 一律排除）。 備份包存的是資料庫裡原樣的密文，所以還原到另一台機器時，那台機器要有同一把金鑰：
第一次啟動前把 `secret.key` 複製到新的資料目錄，或設定相同的 `Security__EncryptionKey`。
金鑰不在時，啟動日誌會記錄錯誤並列出解不開的項目，這些值會被視為未設定；
到「設定 → 安全 → 機密加密」（或管理員呼叫 `POST /api/settings/secrets/reset-undecryptable`）
可以只清掉解不開的值，再重新填寫。解不開的 TOTP 秘密會連同備援碼一起移除，由使用者重新設定。

## 建置隔離

建置就是一段腳本，由誰執行，就擁有誰的權限。講白了：**在 Linux、Windows 與所有遠端執行器上，
任何能編輯或執行任務的人，都讀得到服務帳號讀得到的全部機密**——資料庫（含密碼雜湊與 API 金鑰雜湊）、
所有環境變數（包含機密的）、AI 供應商金鑰與備份憑證——也改得動資料庫，把自己變成管理員。
任務層級的檢視與執行角色只限制 API 給你看什麼，限制不了腳本在磁碟上能做什麼。
在這類執行器上，請把 operator 當成管理員看待。

| 建置在哪裡執行 | 預設情況 |
| --- | --- |
| 伺服器內建的 `local` 執行器，macOS | 每個步驟都在 `sandbox-exec` 裡執行。設定檔是**拒絕清單**：拒絕 Walis 的資料目錄、SQLite 所在目錄、備份位置、安裝目錄（`current`、`releases`、`var`）、服務帳號的家目錄與「額外禁止存取的路徑」，最後再放行任務自己的工作目錄；同時拒絕向其他程序送訊號、查詢 Mach 服務與讀取其他程序的資訊（開關：「沙箱同時拒絕訊號、Mach 服務查詢與讀取其他程序的資訊」）。其餘一切——網路、`/usr`、`/opt`、暫存目錄——都放行。 |
| 內建的 `local` 執行器，Linux 或 Windows | 腳本以 Walis 所用的帳號直接執行，讀得到 Walis 的資料目錄（資料庫、密碼雜湊、雲端金鑰）。 |
| 另一台機器上的 `walis-runner` | 腳本以執行器所用的帳號直接執行。 |

沒有套用沙箱的建置，日誌開頭會註明（`未套用沙箱：腳本以執行器帳號的權限直接執行。`）；
內建執行器屬於這種情況時，「系統設定 → 建置」會顯示警告。在 Linux 與 Windows 上，
請只讓跟管理員一樣可信任的人編輯任務，或是要求隔離邊界。

macOS 沙箱做不到的事：

- 它不是允許清單。拒絕清單擋得住腳本順手讀資料庫或安裝目錄，擋不住持久化：`crontab`、`launchctl`、
  `ssh localhost` 都是把指令交給另一個 daemon，在沙箱**外**以服務帳號執行。在意這點的話，
  請把建置放到專用 VM 或容器裡的遠端執行器。
- 它管不到 Docker daemon 做的事。服務帳號只要能連 Docker（Linux 的 `docker` 群組、macOS 的 Docker Desktop），
  `container.run` 與腳本裡任何 `docker` 指令在 Linux 上就是 root、在 macOS 上就繞過沙箱：
  給服務帳號 Docker 權限，等於給每個能編輯任務的人 root。容器類工作請用 rootless Docker 或遠端執行器。
- 任務可以自己放棄網路：任務編輯器的「拒絕網路」會在它的設定檔加上 `(deny network*)`。
  任務也可以整個不進沙箱：「信任任務：不套用沙箱」是給自我更新這類確實必須寫安裝目錄的任務用的，
  只有管理員能啟用、修改或刪除這種任務，也只有管理員能修改它的環境變數。
  operator 仍可依任務的執行角色啟動建置，但修改時即使把信任旗標拿掉，也會回 `403`。
  套用時每個步驟日誌的第一行都是 `信任任務：…`。

> ［本版修改］前版允許 operator 移除信任旗標後修改任務，且環境變數沿用執行權限。現在信任任務及其環境變數都須由管理員修改，避免改變免沙箱執行的內容。

### 執行帳號

請用一個什麼都沒有的專用帳號執行 Walis：沒有 SSH 金鑰、沒有 `~/.gitconfig` 的 credential helper、
沒有 `gh`／npm／NuGet 登入、在任何地方都沒有發布權限。沙箱雖然拒絕家目錄，但沙箱是減輕手段；
一個空的帳號才是邊界。macOS 的 `deploy/install.sh --user <帳號>` 已經支援，安裝程式產生的 systemd unit 用的是
`DynamicUser`。在 macOS 上安裝程式讓 `$PREFIX`、`$PREFIX/releases` 與 `$PREFIX/current` 由 root 擁有，
建置因此換不掉服務重啟時要跑的執行檔與 `wwwroot`；自我更新任務建置到 `$PREFIX/var/staging`，
由 root 的 restarter 驗證、搬進 releases、切換 current 再重啟。

經環境變數交給程式的部署機密（`Security__BootstrapToken`、`ConnectionStrings__Walis`、執行器的 `WALIS_TOKEN`）
在程式讀完後就會從程序環境移除，同帳號的建置用 `ps -E` 或 `/proc/<pid>/environ` 讀不回來。
systemd unit 把它們放在 `/etc/walis/*.env`（root、`0600`），不寫進 unit 檔。

### 要求隔離邊界

在「系統設定 → 建置」開啟「沒有隔離邊界時拒絕執行」。之後：

- Linux 或 Windows 上的內建執行器不再執行建置，建置會失敗，訊息是
  `系統要求隔離建置，但此執行器沒有可套用的沙箱；已拒絕執行，避免靜默降級。` 請把任務指派給遠端執行器。
- 遠端執行器所在的實體機、VM 或容器只用來跑 Walis 的建置，而且兩端都宣告時，才會領到工作：
  在同一頁的「已置於外部隔離的執行器」加入它的名稱，並以 `--external-isolation true`
  （或 `WALIS_EXTERNAL_ISOLATION=true`）啟動它。Walis 無法替你檢查那道邊界是否存在，
  共用主機或一般用途的主機上的執行器不要這樣宣告。

上面的「隔離建置」是另一個獨立的開關：它決定有沙箱的平台要不要使用沙箱。請保持開啟。

### 工作目錄

「系統設定 → 建置」指定了預設工作目錄時，沒有自訂目錄的任務會用它底下以任務編號命名的子目錄，不再共用同一個目錄；
`walis-runner --workdir` 也一樣。任務自訂的工作目錄不能與儲存者無法修改的任務（管理員的任務或信任任務）的目錄重疊，
建置開始前也會再檢查一次。任務與「系統設定 → 建置」都沒有指定工作目錄時，內建執行器會給每個任務一個專屬的私有目錄
（資料目錄下的 `workspaces/<任務編號>`，服務啟動時會回收已刪除任務留下的目錄）。
「每次建置後清空工作目錄」（系統設定，任務可個別覆寫）會在建置結束、產出檔案與快取保存之後把目錄清空，
不論成功或失敗；有密碼參數或任務專屬機密的任務建議開啟，避免簽出的原始碼與腳本寫下的憑證留到下一次建置。
如果任務指到自己選的目錄，又啟用了清空工作目錄、快取或產出收集，要把那個目錄列進「允許的工作目錄」。
一般任務如果指定服務帳號家目錄或安裝目錄內的位置，也必須先由管理員加入允許清單，才可在沙箱裡開放該工作目錄。
只加入專案實際使用的子目錄；不要把整個家目錄或安裝目錄加入清單。未指定工作目錄時，由系統建立的任務專屬目錄會自動取得許可。
未設定必要的允許清單時，建置會在執行腳本之前停下來，訊息是 `使用此工作目錄前必須設定允許的工作目錄。`

> ［本版修改］前版只說明清空與快取時須設定允許清單，且一般任務可自行指定家目錄或安裝目錄內的位置後取得存取權。現在這些位置須有管理員明確許可，並補充產出收集也需要允許清單。

> ［本版修改］前版沒有自訂目錄的任務共用預設工作目錄，兩個任務也可以指定同一個目錄。

步驟檔案（`$WALIS_ENV_FILE`、`$WALIS_OUTPUT_FILE`）在建置期間放在工作目錄裡私有的 `.walis/` 目錄，git 會忽略它。
macOS 的沙箱會讓 `/opt/homebrew`、`/usr/local` 與服務 PATH 上服務帳號可寫的目錄維持可讀但不可寫，並完全拒絕動作套件目錄，
因為這些位置的內容之後都會在沙箱外執行。

## 遠端執行器

1. **替執行器建立金鑰。** 在「帳號管理 → API 金鑰」按「建立金鑰」，把執行器名稱填進「綁定執行器」。
   綁定的金鑰只能以那一台執行器的身分註冊、認領與回報。金鑰只顯示一次。

2. **啟動執行器。**

   ```bash
   WALIS_TOKEN=<金鑰> ./walis-runner \
     --server http://walis.internal:11120 \
     --name pi-01 --labels linux,arm64
   ```

   或是用一行指令裝成 systemd 服務：

   ```bash
   curl -fsSL https://walis.io/install.sh | sudo WALIS_TOKEN=<金鑰> sh -s -- \
     --runner --service --server http://walis.internal:11120 \
     --name pi-01
   ```

   安裝程式會把金鑰寫進 `/etc/walis/runner.env`（root、`0600`），unit 以 `EnvironmentFile=` 讀取；
   要換金鑰就改那個檔案再 `systemctl restart walis-runner`。手動啟動執行器時，金鑰請用
   `WALIS_TOKEN=<金鑰>` 或 `--token-file <檔案>` 提供，不要用 `--token <金鑰>`：
   命令列參數同一台機器的任何帳號用 `ps` 都看得到。檔案不可讓其他帳號讀取（`chmod 600`），否則執行器會拒絕；
   `--password-file`（或 `WALIS_PASSWORD`）也一樣。

   伺服器位址是另一台機器的 `http://` 時，執行器啟動時會警告金鑰與建置機密以明文傳送。
   伺服器有 HTTPS 之後，請改用 `https://` 的位址。

3. **把任務指派給它。** 執行器第一次連線時會自己註冊，之後就能在任務的「執行器」欄位選到它。
   沒有選執行器的任務由內建的 `local` 執行器處理。執行器不套 Walis 的沙箱；放了部署憑證的主機，
   請在執行器設定把「接受的任務」設為「只接受管理員任務」，之後只有管理員儲存、執行角色為 `admin` 的任務能送過去。

伺服器要求隔離邊界時，另外加上 `--external-isolation true`（安裝程式是 `--external-isolation`），
並依上文在伺服器列入執行器名稱。

所有參數列在 [`source/Walis.Runner/README.md`](../source/Walis.Runner/README.md)。

## 容器

`source/Dockerfile` 建置 API 映像，`source/Walis.Admin/Dockerfile` 建置放在 nginx 後面的管理介面。
它們是開發時使用的映像，不是現成的部署方案，這個 repo 也沒有附 compose 檔。要以它們為基礎時：

- API 映像從 repo 根目錄建置（`docker build -f source/Dockerfile .`），restore 才讀得到 `global.json`、
  `nuget.config` 與已提交的 `packages.lock.json`；基底映像以 digest 釘住，restore 以 locked mode 執行。

- API 聽 8080 連接埠，資料放在 `/app/data`，工作目錄預期在 `/workspace`。
- 介面映像的 `nginx.conf` 會把 `/api` 轉給名為 `walis-dev-api` 的主機的 8080 連接埠。
- 第一個請求不是來自 loopback，所以設定時需要設定碼；它在 API 容器的日誌裡，也在
  `/app/data/setup-code.txt`。
- 把 `Security__TrustedProxies` 設成反向代理容器的確切位址。
- 容器是 Linux：內建執行器的建置在容器內以未隔離方式執行，讀得到 `/app/data`。
  改用外部資料庫時，傳進去的連線字串啟動後就會從程序環境移除，但腳本仍然透過 Walis 本身碰得到資料庫；
  不可信的任務請改放遠端執行器。

只有一台機器時，用發行檔比較省事：單一執行檔，同時提供管理介面。

## 疑難排解

| 看到的訊息 | 出現位置 | 原因 | 處理方式 |
| --- | --- | --- | --- |
| `403`、`從其他機器進行首次設定需要設定碼。…` | 設定畫面 | 從其他機器建立第一位管理員，而設定碼沒填或不正確。 | [找到設定碼](#第一位管理員)。 |
| `426`、`此伺服器已設定 Security:RequireHttps，遠端 API 連線必須使用 HTTPS。` | 瀏覽器或任何用戶端 | 已開啟 `Security__RequireHttps`，而請求是未加密的 HTTP，或經過的反向代理其轉送標頭未被信任。 | 改用 HTTPS；檢查 `X-Forwarded-Proto` 與 `Security__TrustedProxies`。 |
| `400 Bad Request - Invalid Hostname` | 瀏覽器或任何用戶端 | `AllowedHosts` 已設定限制，而網址裡的名稱不在其中。 | [加入主機名稱](#主機名稱)。 |
| `建置前安全檢查失敗：系統要求隔離建置，但此執行器沒有可套用的沙箱；已拒絕執行，避免靜默降級。` | 建置日誌 | 已要求隔離邊界，而內建執行器在這個平台沒有沙箱。 | [要求隔離邊界](#要求隔離邊界)。 |
| `系統要求隔離建置，但此 runner 僅宣告未隔離模式；工作未被認領。…`（409） | 執行器的輸出 | 已要求隔離邊界，而執行器沒有宣告外部隔離。 | 以 `--external-isolation true` 啟動，並在伺服器列入它的名稱。 |
| `此 runner 未列入管理員核准的外部隔離清單；工作未被認領。…`（403） | 執行器的輸出 | 執行器宣告了外部隔離，但伺服器的清單裡沒有它的名稱。 | 加進「已置於外部隔離的執行器」。 |
| `找不到執行器「…」。`（409） | 觸發建置時 | 任務指定的執行器從未連線過。 | 先啟動執行器一次，讓它註冊。 |
| `有 N 筆已加密的機密無法用目前的金鑰解密…` | 啟動時的伺服器日誌 | 備份還原到沒有原本 `secret.key` 的機器，或 `Security__EncryptionKey` 改了。 | [機密加密](#機密加密)：把金鑰放回去，或在「設定 → 安全」清除解不開的值後重新填寫。 |
