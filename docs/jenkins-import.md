# 從 Jenkins 匯入

「建置任務 → 從 Jenkins 匯入」可以貼上 Jenkinsfile（宣告式或腳本式）或任務的 `config.xml`，
預覽對應結果與警告後在編輯器開啟，確認再儲存。匯入只做轉換與預覽（`POST /api/jobs/import/jenkins`），
不會寫資料庫；儲存走一般的建立任務。

程式碼在 `Walis.Core/Jenkins`：`GroovyParser` 只抓 `name(args) { body }` 的 DSL 結構、看不懂的原樣保留；
`JenkinsfileConverter` 做對應；`JenkinsConfigXmlConverter` 處理 freestyle／matrix 專案與內嵌腳本的 pipeline 任務；
`JenkinsCron` 換排程語法。測試在 `GroovyParserTests` 與 `JenkinsImportTests`。

## 對應表

| Jenkins | Walis |
| --- | --- |
| `stage('X') { steps { sh … } }` | 一個「執行命令」步驟，名稱 X；巢狀 stages 的名稱是「父 / 子」 |
| `parallel { stage … }`、腳本式 `parallel a: {…}, b: {…}` | 連續步驟並勾「與前一步同時執行」 |
| `post { always / success / failure / cleanup }` | 步驟的執行條件：總是／成功／失敗時（`unstable`、`aborted`、`regression` 視為失敗，`fixed` 視為成功，`changed` 視為總是，並提示） |
| `options { retry(n) }`（stage）、`retry(n) { 整個 steps }` | 步驟的重試次數；只包住部分步驟的 retry 會內嵌並提示 |
| `options { timeout(time, unit) }`（pipeline） | 任務逾時（換算成分鐘）；stage 層級的只提示 |
| `options { disableConcurrentBuilds() }`、freestyle 未勾「必要時同時執行」 | 同一時間只跑一筆建置 |
| `parameters { string / text / booleanParam / choice / password }` | 建置參數；`credentials`、`file` 不支援 |
| `environment { A = 'x' }` | 任務專屬環境變數（儲存後自動建立）；參照其他變數的值改成步驟開頭的 `export` |
| `environment { T = credentials('id') }`、`withCredentials([...])` | 機密環境變數，值留空待填；`file`／`sshUserPrivateKey` 類型提示要改成寫檔 |
| `triggers { cron('H 2 * * *') }`、`@daily` | 固定排程；`H` 用任務名稱的雜湊換成固定值，`H/15` 變 `*/15`，多行只取第一條 |
| `triggers { pollSCM / githubPush / gitlab }` | 推送後自動建置（要設 webhook 並填儲存庫） |
| `triggers { upstream(...) }`、freestyle「在其他專案建置後觸發」 | 提示到上游任務的「完成後觸發下游任務」設定 |
| `build job: 'X'`、freestyle「觸發其他專案」 | 下游任務（成功時），以名稱對應；Jenkins 是同步等待、Walis 是完成後觸發 |
| `agent { label 'x' }`、`node('x')`、freestyle 限制節點 | 執行器名稱（不存在時請改成 local） |
| `agent { docker { image; args } }` | 「在容器中執行」動作，工作目錄掛進容器 |
| `when { branch / environment / equals / not / anyOf / allOf }` | 步驟開頭的 shell 判斷，不成立就 `exit 0` 略過；`expression` 等需手動改 |
| `dir('p') { … }` | `( cd 'p' && … )` |
| `withEnv(['A=1']) { … }` | 子 shell 內 `export` |
| `env.X = 'v'`（script 內） | `export X=…` 並寫進 `$WALIS_ENV_FILE` 讓後面的步驟看得到 |
| `echo`、`sleep`、`error`、`writeFile`、`catchError`、`deleteDir`／`cleanWs` | 對應的 shell 命令 |
| `checkout scm`、`git url:`、freestyle Git | 第一次 `clone`、之後 `fetch` 並切到分支的步驟；需要儲存庫網址 |
| `archiveArtifacts`、`junit` | 產出檔案路徑（junit 沒有檢視器，只保存檔案） |
| `matrix { axes … }`、matrix 專案的軸 | 任務矩陣（一個任務只能有一組） |
| `$BUILD_NUMBER`、`${env.JOB_NAME}`、`$BRANCH_NAME`、`$WORKSPACE` | `$WALIS_BUILD_NUMBER`、`$WALIS_JOB_NAME`、`$WALIS_BRANCH`、`$PWD`；`${params.X}` → `${X}` |

## 不支援（會列成警告、留成註解）

- `input`（人工核准）：可以拆成兩個任務，第二個由人手觸發。
- `script {}` 裡的 Groovy 邏輯（`def`、`if`、`for`、`try`）、`readFile`／`fileExists`／`httpRequest` 等 Groovy 層操作。
- `bat`／`powershell`（Windows）。
- `stash`／`unstash`（不需要，步驟共用工作目錄）。
- `tools {}`：只提示要確認執行器的 PATH。
- 共用函式庫、`agent dockerfile`／`kubernetes`、`lock`、報告外掛（cobertura、publishHTML…）、通知步驟（改用 Walis 通知設定）。
