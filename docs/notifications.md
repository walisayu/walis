# 通知的 webhook 格式與簽章

通知管道有 Slack、Email 與 webhook 三種。本文件說明 webhook 管道送出的內容與驗證方式；
Slack 與 Email 的格式不受影響。

## 請求

- 方法：`POST`，`Content-Type: application/json; charset=utf-8`。
- 不跟隨轉址，回應主體不會被讀取，只看 HTTP 狀態碼（2xx 算成功）。逾時 10 秒。
- Walis 不會自動重送失敗的通知；`eventId` 在同一個事件送往多個管道時相同，接收端可以用它過濾重複。

## 內容欄位

| 欄位 | 型別 | 說明 |
| --- | --- | --- |
| `event` | 字串 | 事件種類：`build.failed`、`build.succeeded`、`build.recovered`、`build.timeout`、`runner.offline`、`security.alert`（登入鎖定、稽核被關閉、新增或升為管理員；不受靜音時段影響）、`test`（管理介面的測試通知） |
| `eventId` | 字串 | 事件識別碼。建置事件是 `<event>:<buildId>`（例如 `build.failed:3f2c…`），一場建置只會結束一次，所以不會重複；其他事件是新的 Guid |
| `title` | 字串 | 標題，例如「建置失敗：release #7」 |
| `body` | 字串 | 多行文字摘要（任務、專案、分支、執行器、觸發來源、耗時、結束碼） |
| `url` | 字串或 null | 建置頁面網址；系統設定沒有填 `BaseUrl` 時為 null |
| `sentAt` | 字串 | 送出時間，ISO 8601 UTC |
| `build` | 物件 | 只有建置事件才有，其他事件不含這個鍵 |

`event`、`title`、`body`、`url`、`sentAt` 是原本就有的欄位，維持不變，既有的接收端不需要修改。

`build` 物件：

| 欄位 | 型別 | 說明 |
| --- | --- | --- |
| `id` | 字串（Guid） | 建置識別碼 |
| `jobId` | 字串（Guid） | 任務識別碼 |
| `jobName` | 字串 | 任務名稱 |
| `number` | 整數 | 該任務的第幾次建置 |
| `status` | 字串 | `Succeeded` 或 `Failed`（逾時也是 `Failed`，以 `event` 的 `build.timeout` 區分）；取消的建置不發通知 |
| `externalRef` | 字串或 null | 觸發時帶的 `externalRef` 或 `Idempotency-Key`，沒有帶時為 null |
| `matrixLabel` | 字串或 null | 矩陣組合的標籤，例如 `NODE=22, OS=mac`；一般建置為 null |
| `startedAt` | 字串或 null | 開始時間，ISO 8601 UTC |
| `finishedAt` | 字串或 null | 結束時間，ISO 8601 UTC |
| `exitCode` | 整數或 null | 結束碼 |

日誌、建置參數與任何機密值都不會出現在通知內容裡。

範例（已設定簽章密鑰時的完整請求；實際內容裡的中文會以 `\uXXXX` 跳脫，這裡為了閱讀方便直接顯示）：

```http
POST /hooks/walis HTTP/1.1
Host: receiver.example.test
Content-Type: application/json; charset=utf-8
Walis-Signature: t=1791000000,v1=5d0c3f…（64 個十六進位字元）
Walis-Event-Id: build.failed:3f2c8a51-6a0e-4d0b-9a63-1f0e2b7c9d10

{"event":"build.failed","eventId":"build.failed:3f2c8a51-6a0e-4d0b-9a63-1f0e2b7c9d10","title":"建置失敗：release #7","body":"任務：release\n專案：—\n分支：main\n執行器：local\n觸發：agentgate\n耗時：00:00:42\n結束碼：2\n","url":"https://walis.example.test/#/builds/3f2c8a51-6a0e-4d0b-9a63-1f0e2b7c9d10","sentAt":"2026-10-03T01:02:46.123Z","build":{"id":"3f2c8a51-6a0e-4d0b-9a63-1f0e2b7c9d10","jobId":"9b1e4f7a-2c3d-4e5f-8a9b-0c1d2e3f4a5b","jobName":"release","number":7,"status":"Failed","externalRef":"agentgate:run-42","matrixLabel":null,"startedAt":"2026-10-03T01:02:03Z","finishedAt":"2026-10-03T01:02:45Z","exitCode":2}}
```

## 簽章

在系統設定的「安全」分頁填入「通知簽章密鑰」（32 到 1,024 個字元，只有管理員能修改；
設定 API 的回應一律遮蔽成 `********`，原樣送回代表不修改；加密的備份檔與 `WebhookSecret` 一樣保留實際值，還原後才能繼續簽章）。設定後每個 webhook 請求多兩個標頭：

```
Walis-Signature: t=<unix 秒>,v1=<hex>
Walis-Event-Id: <eventId>
```

- `t` 是送出當下的 Unix 時間（秒）。
- `v1` 是 `HMAC-SHA256(密鑰, "<t>.<原始 body>")` 的小寫十六進位，密鑰與訊息都以 UTF-8 編碼。
  「原始 body」是收到的位元組本身，**不要先解析 JSON 再重新輸出**，空白與欄位順序一變就對不上。
- 沒有設定密鑰時不加這兩個標頭，行為與之前相同。
- Slack 與 Email 管道不簽章。

接收端建議：

1. 以固定時間比較（constant-time compare）比對 `v1`。
2. 檢查 `t` 與現在的差距，例如超過 5 分鐘就拒絕，避免舊請求被重複送出。
3. 以 `eventId`（或 `Walis-Event-Id` 標頭）記錄已處理的事件，重複的直接回 2xx 略過。
4. 更換密鑰時，Walis 只會用新的密鑰簽章，接收端可以在切換期間同時接受新舊兩把。

### 驗章範例（Python）

```python
import hashlib
import hmac
import time


def verify(secret: str, signature_header: str, raw_body: bytes, tolerance_seconds: int = 300) -> bool:
    parts = dict(item.split("=", 1) for item in signature_header.split(",") if "=" in item)
    timestamp, received = parts.get("t"), parts.get("v1")
    if not timestamp or not received or not timestamp.isdigit():
        return False
    if abs(time.time() - int(timestamp)) > tolerance_seconds:
        return False
    expected = hmac.new(secret.encode("utf-8"), timestamp.encode("ascii") + b"." + raw_body, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, received)
```

### 驗章範例（C#）

```csharp
using System.Security.Cryptography;
using System.Text;

static bool Verify(string secret, string signatureHeader, byte[] rawBody, TimeSpan tolerance)
{
    var parts = signatureHeader.Split(',')
        .Select(item => item.Split('=', 2))
        .Where(pair => pair.Length == 2)
        .ToDictionary(pair => pair[0].Trim(), pair => pair[1].Trim());

    if (!parts.TryGetValue("t", out var t) || !long.TryParse(t, out var timestamp) ||
        !parts.TryGetValue("v1", out var received))
        return false;

    if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) > tolerance.TotalSeconds) return false;

    var message = Encoding.ASCII.GetBytes($"{t}.").Concat(rawBody).ToArray();
    var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));

    return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(received));
}
```
