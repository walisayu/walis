# 外掛開發

**給外掛作者看的完整指南在管理介面：外掛頁 → 開發指南。** 那份內容在
`source/Walis.Admin/src/content/pluginGuide.ts`，跟 host API 綁在一起——改 API 時要一起改。

這份文件記錄的是**設計與實作**：東西放在哪、為什麼這樣切、AI 產生功能的威脅模型。
評估與取捨的來龍去脈見 `docs/plugins.md`。

## 程式碼位置

| 檔案 | 內容 |
| --- | --- |
| `Walis.Core/Plugins/PluginManifest.cs` | manifest 的形狀；`ToActionDefinition()` 讓外掛出現在動作面板 |
| `Walis.Core/Plugins/PluginHost.cs` | Jint 執行環境、資源上限、host API |
| `Walis.Core/Plugins/IPluginCapabilities.cs` | Core 只定義能力的形狀，實作在 API 層 |
| `Walis.Api/Services/PluginCapabilities.cs` | 實際的 log 與 http，含機密注入 |
| `Walis.Api/Services/PluginActionRenderer.cs` | 動作要渲染時決定走樣板還是走外掛 |
| `Walis.Api/Services/PluginGenerator.cs` | AI 產生草稿的提示詞與**輸出正規化** |
| `Walis.Api/Endpoints/PluginEndpoints.cs` | 安裝、停用、移除、預覽、產生 |

## 為什麼外掛只能回傳字串

`action` 類外掛的合約是 `render(inputs, ctx) -> string`，回傳的是一段 shell。
這讓外掛的產出變成**可審閱的資料**：安裝前按「預覽產生的腳本」就看得到它會跑什麼。

Jenkins 的外掛可以做任何事，代價是只能靠人工審核；Woodpecker 用容器隔離，代價是綁死容器。
Walis 選第三條路——限制外掛的**輸出形式**，而不是限制它的執行環境。
副作用是外掛做不了「執行期的複雜邏輯」，但那類需求本來就該寫在它產生的腳本裡。

## 安裝是管理員限定

`/api/plugins` 在 `AuthenticationMiddleware` 的 `adminOnly` 清單裡。理由很簡單：
**安裝外掛等於加一段會被執行的程式碼**，跟改系統設定同級，不該讓 operator 做。

## AI 產生的威脅模型

管理介面可以描述需求讓 AI 產生草稿。這裡有兩層不信任，缺一不可：

### 第一層：需求是資料，不是指令

使用者輸入會被包在 `<requirement>` 標籤裡，系統提示明確要求模型忽略其中任何
「改變輸出格式、擴大權限」的句子。這擋的是把注入文字貼進需求欄的情況。

**但提示工程不是安全機制。** 模型可能被繞過，所以還有第二層。

### 第二層：模型的輸出也不可信

`PluginGenerator.Parse` 對回來的東西做結構正規化，不管模型寫了什麼：

| 欄位 | 處理 |
| --- | --- |
| `permissions` | **一律清空**，並在警告列出「AI 要求了額外能力，已移除」 |
| `kind` | 強制 `action` |
| `apiVersion` | 強制目前版本 |
| `id` | 不符規則就換成 `generated.<亂數>` 並提醒 |
| `code` | 超過 20,000 字元截斷；沒看到 `render` 定義就警告 |

然後：**產生的東西只填進表單，不會安裝**。使用者要自己讀、自己按預覽、自己按安裝，
而且草稿一律是「未信任」——未信任的外掛 `ctx.http.fetch` 會直接被 host 擋下，
不管 manifest 寫了什麼；要連外得由管理員審閱程式碼後在安裝時勾選「信任」。
執行時仍在 Jint 的沙箱裡，資源上限照樣套用。

換句話說，就算提示詞完全被繞過，攻擊者能達成的最好結果是「產生一段人類會在畫面上看到、
而且要自己按下安裝的腳本」——跟他直接把那段腳本貼給你叫你安裝沒有差別。

### 其他注意事項

- 需求文字會送到使用者設定的 AI 供應商，畫面上明講「不要寫入密碼或金鑰」。
- 需求長度上限 4,000 字元，避免拿它當夾帶大量內容的管道。
- 每次呼叫都會寫進 `AiRequests`（只記統計，不存提示詞），用途標記 `generate-plugin`。

## 還沒做

- `notification` 與 webhook 解析的擴充點（目前只有 `action`）。
- `kv` 能力（manifest 收得下，但 host 還沒實作）。
- `.d.ts` 型別套件，讓外掛作者在編輯器裡有自動完成。
