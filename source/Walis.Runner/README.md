# walis-runner

在另一台機器上執行 Walis 建置任務的代理程式。API 內建的 `local` 執行器只跑在 API 本機， 需要在其他機器建置時就用這支程式。

## 用法

```bash
WALIS_TOKEN=<金鑰> dotnet run --project source/Walis.Runner -- \
  --server https://walis.internal \
  --name builder-01 \
  --labels linux,docker \
  --workdir /workspace
```

也可以先 `dotnet publish` 後直接執行 `walis-runner`。權杖請用環境變數 `WALIS_TOKEN` 或 `--token-file <檔案>`
（檔案權限 600、內容只有權杖一行）提供：寫在 `--token` 命令列參數上，同一台機器的其他帳號用 `ps` 就看得到。
程式讀完 `WALIS_TOKEN` 就會從自己的環境把它移除，建置腳本讀不回來。

| 參數                         | 說明                                                                                                                                                      |
|------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------|
| `--server`                   | Walis API 位址，本機預設 `http://127.0.0.1:11126`（環境變數 `WALIS_SERVER`）。跨機器連線建議用 HTTPS；用 HTTP 時會警告權杖與建置機密以明文傳送 |
| `--allow-insecure-http true` | 已不需要：非本機的 HTTP 位址現在可以直接使用。保留是為了讓既有的啟動設定照常運作                                                                          |
| `--name`                     | 執行器名稱，預設為本機電腦名稱（環境變數 `WALIS_RUNNER_NAME`）                                                                                            |
| `--labels`                   | 以逗號分隔的標籤                                                                                                                                          |
| `--slots`                    | 同時可執行的建置數，預設 2                                                                                                                                |
| `--token-file`               | 讀取權杖的檔案（必須是 600，其他帳號讀得到就拒絕啟動；只有權杖一行）；與 `--token`／`WALIS_TOKEN` 只能擇一                                            |
| `--token`                    | 綁定此執行器名稱的 runner API 金鑰（建議改用環境變數 `WALIS_TOKEN` 或 `--token-file`）；服務預設強制登入，因此實際部署必填。綁定後只能認領與回報這台的建置 |
| `--password-file`            | 讀取密碼的檔案（同樣必須是 600）；或用環境變數 `WALIS_PASSWORD`，程式讀完就從自己的環境移除。不要用 `--password`，命令列同機帳號看得到 |
| `--user` / `--password`      | 改用帳號密碼登入，程式以 `?bearer=true` 換取 1 小時的 bearer 權杖，過期時自動重新登入。認領需要管理員身分，所以這個帳號得是管理員；正式環境請改用綁定的金鑰                                                |
| `--workdir`                  | 任務沒有指定工作目錄時的根目錄；每個任務用它底下以任務編號命名的子目錄，不共用同一個目錄。快取放在帳號自己的應用程式資料目錄（`~/.local/share/walis-runner/cache` 等），不放共用暫存目錄 |
| `--external-isolation true`  | 伺服器要求隔離邊界時才需要。僅在整個 runner 已置於專用 VM/container 邊界時宣告（環境變數 `WALIS_EXTERNAL_ISOLATION=true`）；server 管理員也必須把名稱加入「已置於外部隔離的執行器」 |
| `--interval`                 | 輪詢間隔秒數，預設 5                                                                                                                                      |

## 運作方式

1. 啟動時若清單中沒有同名執行器，會自動註冊一台（綁定的金鑰只能註冊自己那個名字）。
2. 每隔 `--interval` 呼叫一次 `POST /api/runners/{name}/claim`：
    - 回應 `204` 代表目前沒有指派給它的工作。
    - 回應 `200` 會一併帶回任務內容與該任務要注入的環境變數。
3. 以與 API 相同的 `ShellCommandExecutor` 執行命令，逾時後會中止整棵程序樹。
4. 建置成功且任務有設定產出路徑時，把檔案上傳到 `POST /api/builds/{id}/artifacts`。
5. 最後以 `POST /api/builds/{id}/result` 回報狀態、結束碼與完整日誌。

只有「執行器」欄位指定為這台執行器的任務會被它認領；沒有指定執行器的任務一律由 API 內建的
`local` 執行器處理。

## 注意事項

- 機密環境變數會以明文傳給獲授權的執行器；伺服器位址是 HTTP 時，傳輸過程也沒有加密。正式環境請讓伺服器提供 HTTPS，並只在受管理的 runner 主機使用。
- runner 不跟隨 HTTP redirect，避免權杖、登入帳密或工作內容被重新導向到未設定的主機。
- 新伺服器只會把工作交給有明確宣告安全協定能力的新版 runner。預設情況下 runner 以未隔離方式執行建置，建置日誌會註明；管理員在「系統設定 → 建置」開啟「沒有隔離邊界時拒絕執行」之後，沒有宣告外部隔離的 runner 就領不到工作，輸出會顯示伺服器回覆的原因。
- 伺服器要求隔離邊界時，Linux/Windows 可把 runner 整體部署在每台只承載 Walis 建置的專用 VM 或外層 container，並設定
  `WALIS_EXTERNAL_ISOLATION=true`，再由 server 管理員把 runner 名稱逐行加入 `ExternallyIsolatedRunners`。兩端都同意才會派工；Walis
  不會自行驗證外層邊界。一般共用主機不可開啟。
- 執行器沒有回報心跳超過 5 分鐘就會被標記為離線，並依通知設定發出提醒。
- 按 Ctrl+C 會等目前的建置回報完再結束。

## 步驟

伺服器認領工作時會一併把規劃好的步驟送過來，執行器依序執行並逐步回報狀態與輸出， 所以遠端建置在管理介面看到的畫面跟本機建置一樣。執行器不需要知道動作的存在——
展開動作流程是伺服器的事。

每個步驟是獨立的 shell，工作目錄共用但環境變數不延續；要把值傳給後面的步驟， 往 `$WALIS_ENV_FILE` 追加 `KEY=VALUE`。
