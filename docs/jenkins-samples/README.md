# Jenkins 匯入範例

拿來試「建置任務 → 從 Jenkins 匯入」的樣本，每一份都刻意混進幾個 Walis 對不上的東西，
好看清楚警告長什麼樣。`JenkinsImportTests` 會把這個目錄的每一份都轉一次，確保不會壞掉。

| 檔案 | 涵蓋 |
| --- | --- |
| `node-app.Jenkinsfile` | docker agent、credentials、choice／boolean 參數、cron 的 `H`、parallel、stage retry、`when` branch／environment、withCredentials、dir、stage 與 pipeline 層的 post、junit、slackSend |
| `dotnet-service.Jenkinsfile` | agent label、tools、pipeline 層 retry、pollSCM、matrix、巢狀 stages、build job 下游、cleanWs、unstable |
| `docker-release.Jenkinsfile` | password 參數、換行字串的 choice、參照其他變數的 environment、git 指定儲存庫、三引號 sh、catchError、input（不支援）、script 裡的 env 指派與 Groovy if、error、mail |
| `scripted-legacy.Jenkinsfile` | 腳本式：properties 的 triggers／parameters／disableConcurrentBuilds、node 標籤、checkout scm、Groovy if／try、withEnv、stash／unstash、parallel 具名分支、timeout |
| `freestyle-config.xml` | freestyle：三種參數、Git、限制節點、TimerTrigger、多個 shell 步驟、Windows 批次、產出、下游、Mailer、逾時、憑證綁定 |
