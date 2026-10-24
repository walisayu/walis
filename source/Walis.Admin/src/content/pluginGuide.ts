/**
 * The plugin development guide. Kept in code rather than external docs: the admin UI must be readable offline,
 * and the content is tied to the host API version, so it should change together with the API.
 */
export const developmentGuide = `# 用 SDK 開發（建議）

貼在這個視窗裡改一行還行，認真開發請用 SDK：有型別、本機測試、打包與上傳。

\`\`\`bash
npx @walisayu/plugin-sdk init my-plugin
cd my-plugin && npm install

npx walis-plugin render --input target=stg-01   # 看它會產生什麼
npm test                                        # 跑測試
WALIS_API_KEY=... npx walis-plugin install      # 上傳
\`\`\`

SDK 的 \`ctx.shell.quote\` 與 Walis 的實作完全一致（有測試比對），本機測到的就是實際會跑的；
\`walis-plugin build\` 也會掃出用到 host 沒有的 API（require、await、setTimeout…），
不必等到正式建置才發現。原始碼在 repo 的 \`source/walis-plugin-sdk\`。

下面的說明是**外掛的合約本身**，不管用不用 SDK 都適用。

# 外掛能做什麼

外掛用 JavaScript 寫，做的事只有一件：**依照使用者填的參數，產生一段 shell**。
Walis 拿到那段字串之後，把它當成建置的一個步驟執行。

這個設計是刻意的：外掛的產出是資料，不是行為。所以你安裝別人寫的外掛之前，
可以先按「預覽產生的腳本」看它到底會跑什麼，也保有最後的決定權。

## 一份最小的外掛

manifest（純資料，宣告要顯示哪些欄位）：

\`\`\`json
{
  "id": "acme.deploy",
  "name": "部署到測試機",
  "description": "呼叫團隊共用的部署腳本。",
  "version": "1.0.0",
  "apiVersion": 1,
  "kind": "action",
  "category": "deploy",
  "icon": "cil-cloud-upload",
  "parameters": [
    { "name": "target", "label": "目標主機", "required": true, "placeholder": "stg-01" },
    { "name": "restart", "label": "部署後重啟", "type": "boolean", "default": "false" }
  ]
}
\`\`\`

程式碼（一定要定義 render，回傳字串）：

\`\`\`js
function render(inputs, ctx) {
  const lines = ['scripts/deploy.sh ' + ctx.shell.quote(inputs.target)]

  if (inputs.restart) {
    lines.push('scripts/restart.sh ' + ctx.shell.quote(inputs.target))
  }

  return lines.join('\\n')
}
\`\`\`

存檔之後，這個動作就會出現在流程編輯器的動作面板，跟內建動作一樣拖進任務裡用。

# 參數

| 欄位 | 說明 |
| --- | --- |
| name | 程式碼裡用 inputs.name 取值，只能用英數與底線 |
| label | 表單上顯示的名稱 |
| type | text、multiline、number、boolean、select、path |
| required | 必填時，流程編譯會擋下沒填的步驟 |
| default | 預設值；boolean 用 "true" 或 "false" |
| options | select 專用，格式 [{ "value": "a", "label": "A" }] |

參數宣告放在 manifest 而不是程式碼裡，是為了讓「列出動作清單」不必執行 JavaScript。

# host API

外掛能碰到的東西只有這些，其餘一律沒有：

- ctx.shell.quote(值) — 把值包成安全的 shell 字串
- ctx.log(訊息) — 寫進伺服器日誌，除錯用
- ctx.variables — 這次建置的環境變數（唯讀）
- ctx.http.fetch(網址, 選項) — 只有 manifest 宣告過的網域才能連

**沒有** require、import、fs、process、fetch、setTimeout，也**沒有 npm 套件**。
外掛是膠水程式，需要重工具就交給它產生的 shell 去做。

## 跳脫一定要用 ctx.shell.quote

\`\`\`js
// 對：值被安全地包起來
return 'echo ' + ctx.shell.quote(inputs.message)

// 錯：使用者填 '; rm -rf / #' 就出事了
return 'echo ' + inputs.message
\`\`\`

# 資源上限

| 限制 | 值 |
| --- | --- |
| 執行時間 | 10 秒 |
| 記憶體 | 8 MB |
| 語句數 | 500,000 |
| 遞迴深度 | 64 層 |

超過就中止並記成該步驟的錯誤，不會影響其他步驟或服務本身。

# 能力與信任

manifest 沒宣告的能力就是沒有。要連外或用機密，得在 permissions 寫清楚，安裝時由管理員決定要不要給：

\`\`\`json
"permissions": {
  "http": ["https://acme.atlassian.net"],
  "secrets": ["JIRA_TOKEN"],
  "kv": false
}
\`\`\`

機密的語意是「能用，不能讀」——外掛只能要求把值放進某個標頭，拿不到內容：

\`\`\`js
const response = ctx.http.fetch('https://acme.atlassian.net/rest/api/3/issue', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ fields: { summary: inputs.title } }),
  withSecret: { name: 'JIRA_TOKEN', header: 'Authorization' }
})
\`\`\`

這樣即使外掛被寫壞或被動手腳，也沒辦法把權杖送去別的地方。

# 用 AI 產生草稿

安裝視窗上方可以直接描述需求，讓 AI 產生一份草稿。要注意的是：

- **產生的東西一律當成草稿。** 權限會被自動剝掉、類型與 API 版本會被寫死、識別字會重新驗證。
- **一定要自己讀過程式碼、按過「預覽產生的腳本」再安裝。** 模型的輸出跟網路上抄來的程式碼一樣不可信。
- 需求裡如果混進「忽略上述規則」這類句子，系統會把它當成資料而不是指令；但這只是減少雜訊，
  真正的防線是上面那兩點加上執行時的沙箱。
- 需求文字會送到你設定的 AI 供應商，**不要把密碼或金鑰寫進去**。

# 除錯

- 安裝前用「預覽產生的腳本」看輸出。
- 用 ctx.log 印中間值，訊息會出現在伺服器日誌。
- 外掛丟例外時，那一步會失敗並顯示訊息與行號，其他步驟不受影響。
`
