namespace Walis.Core.Actions;

/// <summary>
///     The second batch of built-in actions: language ecosystems, databases, cloud, Kubernetes, notifications, security scanning, and so on.
///     Like the first batch they are only data (parameter declarations + shell templates); templates may only use their own declared parameters, pinned by tests.
///     Naming convention: <c>domain.action</c>; actions that need a tool say in their description what must be installed first.
/// </summary>
public static partial class BuiltInActions
{
    private static IEnumerable<ActionDefinition> Extended() =>
    [
        // ---------------------------------------------------------------- General
        Define(
            "shell.script-file",
            "執行腳本檔",
            "執行工作目錄裡的 shell 腳本檔。",
            ActionCategory.General,
            "cil-terminal",
            "{{#if interpreter}}{{{interpreter}}} {{/if}}{{path}}{{#if arguments}} {{{arguments}}}{{/if}}",
            Path("path", "腳本路徑", true, "./scripts/build.sh"),
            Text("interpreter", "直譯器", placeholder: "bash", help: "留空就直接執行檔案，需要有執行權限。"),
            Text("arguments", "參數", placeholder: "--release")),

        Define(
            "shell.set-output",
            "傳值給後續步驟",
            "把一個變數寫進 $WALIS_ENV_FILE，後面的步驟就讀得到。",
            ActionCategory.General,
            "cil-code",
            "printf '%s=%s\\n' {{name}} {{value}} >> \"$WALIS_ENV_FILE\"",
            Text("name", "變數名稱", true, "VERSION"),
            Text("value", "值", true, "1.2.3", help: "可以用 $(command) 取命令輸出。")),

        Define(
            "shell.env-file",
            "載入 .env 檔",
            "把 .env 檔裡的 KEY=VALUE 全部傳給後續步驟（略過註解與空行）。",
            ActionCategory.General,
            "cil-code",
            "grep -Ev '^\\s*(#|$)' {{path}} >> \"$WALIS_ENV_FILE\"",
            Path("path", ".env 路徑", true, ".env")),

        Define(
            "shell.wait-for-port",
            "等待連接埠開啟",
            "每秒檢查一次 TCP 連接埠，逾時就失敗；常用在等資料庫或服務啟動。",
            ActionCategory.General,
            "cil-clock",
            "for i in $(seq 1 {{{timeout}}}); do nc -z {{host}} {{{port}}} && exit 0; sleep 1; done; echo {{message}}; exit 1",
            Text("host", "主機", true, "127.0.0.1"),
            Number("port", "連接埠", "5432"),
            Number("timeout", "最多等幾秒", "60"),
            Text("message", "逾時訊息", value: "等待連接埠逾時")),

        Define(
            "shell.wait-for-url",
            "等待網址回應",
            "每秒用 curl 打一次，回 2xx／3xx 就繼續，逾時就失敗。",
            ActionCategory.General,
            "cil-clock",
            "for i in $(seq 1 {{{timeout}}}); do curl -fsS -o /dev/null {{url}} && exit 0; sleep 1; done; echo {{message}}; exit 1",
            Text("url", "網址", true, "http://127.0.0.1:8080/health"),
            Number("timeout", "最多等幾秒", "60"),
            Text("message", "逾時訊息", value: "服務沒有在時間內就緒")),

        Define(
            "shell.retry",
            "重試命令",
            "命令失敗時等一下再試，適合網路不穩的下載或推送。",
            ActionCategory.General,
            "cil-loop-circular",
            "n=0; until {{{command}}}; do n=$((n+1)); [ \"$n\" -ge {{{attempts}}} ] && exit 1; echo \"第 $n 次失敗，{{{delay}}} 秒後重試\"; sleep {{{delay}}}; done",
            Multiline("command", "命令", true, "npm ci"),
            Number("attempts", "最多幾次", "3"),
            Number("delay", "間隔秒數", "5")),

        Define(
            "shell.print-env",
            "列出環境變數",
            "印出目前的環境變數（機密會依設定遮蔽），用來排查建置環境。",
            ActionCategory.General,
            "cil-list-rich",
            "env | sort{{#if filter}} | grep -i {{filter}}{{/if}}",
            Text("filter", "只列出包含", placeholder: "NODE")),

        Define(
            "shell.fail",
            "讓建置失敗",
            "印出訊息並以非零結束碼結束，配合執行條件用來擋住不該繼續的情況。",
            ActionCategory.General,
            "cil-x-circle",
            "echo {{message}} >&2; exit {{{code}}}",
            Text("message", "訊息", true, "手動中止"),
            Number("code", "結束碼", "1")),

        Define(
            "shell.timestamp",
            "印出時間戳",
            "把目前時間寫進日誌，也可以存成變數給後面用。",
            ActionCategory.General,
            "cil-clock",
            "stamp=$(date -u +{{format}}); echo \"$stamp\"{{#if variable}}; printf '%s=%s\\n' {{variable}} \"$stamp\" >> \"$WALIS_ENV_FILE\"{{/if}}",
            Text("format", "格式", value: "%Y%m%d-%H%M%S"),
            Text("variable", "存成變數", placeholder: "BUILD_STAMP")),

        // ---------------------------------------------------------------- Files
        Define(
            "file.download",
            "下載檔案",
            "用 curl 下載到指定路徑，會跟隨轉址。",
            ActionCategory.File,
            "cil-cloud-download",
            "curl -fsSL{{#if header}} -H {{header}}{{/if}} -o {{destination}} {{url}}",
            Text("url", "網址", true, "https://example.com/file.tgz"),
            Path("destination", "存到", true, "downloads/file.tgz"),
            Text("header", "額外標頭", placeholder: "Authorization: Bearer $TOKEN")),

        Define(
            "file.tar",
            "打包成 tar.gz",
            "把目錄或檔案壓縮成 tar.gz。",
            ActionCategory.File,
            "cil-cloud-download",
            "tar -czf {{archive}} {{#if directory}}-C {{directory}} {{/if}}{{{source}}}",
            Path("archive", "輸出檔名", true, "release.tgz"),
            Text("source", "要打包的路徑", true, "dist", help: "多個用空白隔開。"),
            Path("directory", "先切換到目錄", placeholder: "build")),

        Define(
            "file.untar",
            "解開 tar.gz",
            "把 tar.gz 解到指定目錄。",
            ActionCategory.File,
            "cil-cloud-upload",
            "mkdir -p {{destination}} && tar -xzf {{archive}} -C {{destination}}",
            Path("archive", "壓縮檔", true, "release.tgz"),
            Path("destination", "解到", true, "release")),

        Define(
            "file.unzip",
            "解開 zip",
            "把 zip 解到指定目錄，同名檔案直接覆蓋。",
            ActionCategory.File,
            "cil-cloud-upload",
            "mkdir -p {{destination}} && unzip -oq {{archive}} -d {{destination}}",
            Path("archive", "壓縮檔", true, "package.zip"),
            Path("destination", "解到", true, "package")),

        Define(
            "file.write",
            "寫入檔案",
            "把內容寫成檔案（會覆蓋），例如產生設定檔。內容裡的 $VAR 會被展開。",
            ActionCategory.File,
            "cil-save",
            "mkdir -p \"$(dirname {{path}})\" && cat > {{path}} <<WALIS_EOF\n{{{content}}}\nWALIS_EOF",
            Path("path", "檔案路徑", true, "config/app.env"),
            Multiline("content", "內容", true, "API_URL=$API_URL")),

        Define(
            "file.append",
            "附加到檔案",
            "把一行文字接在檔案尾端。",
            ActionCategory.File,
            "cil-pencil",
            "printf '%s\\n' {{line}} >> {{path}}",
            Path("path", "檔案路徑", true),
            Text("line", "內容", true)),

        Define(
            "file.replace",
            "取代文字",
            "用 sed 把檔案裡的字串換掉，常用在把版本號填進設定檔。",
            ActionCategory.File,
            "cil-pencil",
            "sed -i.bak \"s|{{{search}}}|{{{replacement}}}|g\" {{path}} && rm -f {{path}}.bak",
            Path("path", "檔案路徑", true, "package.json"),
            Text("search", "找", true, "__VERSION__", help: "是 sed 的樣式，| 要跳脫。"),
            Text("replacement", "換成", true, "$VERSION")),

        Define(
            "file.chmod",
            "設定權限",
            "改檔案或目錄的權限，例如讓腳本可執行。",
            ActionCategory.File,
            "cil-lock-locked",
            "chmod {{#if recursive}}-R {{/if}}{{{mode}}} {{path}}",
            Path("path", "路徑", true, "scripts/deploy.sh"),
            Text("mode", "權限", true, "+x"),
            Boolean("recursive", "包含子目錄")),

        Define(
            "file.checksum",
            "計算 SHA-256",
            "產生檔案的 SHA-256 校驗檔，給發行檔附上。",
            ActionCategory.File,
            "cil-check-circle",
            "shasum -a 256 {{{files}}} > {{output}} && cat {{output}}",
            Text("files", "檔案", true, "dist/*.tgz", help: "多個用空白隔開，可用萬用字元。"),
            Path("output", "輸出", true, "dist/SHA256SUMS.txt")),

        Define(
            "file.verify-checksum",
            "驗證 SHA-256",
            "用校驗檔驗證檔案，對不上就失敗。",
            ActionCategory.File,
            "cil-check-circle",
            "shasum -a 256 -c {{checksums}}",
            Path("checksums", "校驗檔", true, "SHA256SUMS.txt")),

        Define(
            "file.list",
            "列出目錄內容",
            "把目錄內容印進日誌，方便確認產出。",
            ActionCategory.File,
            "cil-list-rich",
            "ls -la{{#if recursive}}R{{/if}} {{path}}",
            Path("path", "目錄", value: "."),
            Boolean("recursive", "包含子目錄")),

        // ---------------------------------------------------------------- Git
        Define(
            "git.fetch",
            "Git Fetch",
            "取得遠端的最新提交與標籤，不改動工作目錄。",
            ActionCategory.Git,
            "cil-layers",
            "git fetch --prune{{#if tags}} --tags{{/if}} {{remote}}",
            Text("remote", "遠端", value: "origin"),
            Boolean("tags", "包含標籤", true)),

        Define(
            "git.commit",
            "Git Commit",
            "把所有變更加進暫存並提交；沒有變更時不會失敗。",
            ActionCategory.Git,
            "cil-save",
            "git add -A && (git diff --cached --quiet || git -c user.name={{author}} -c user.email={{email}} commit -m {{message}})",
            Text("message", "提交訊息", true, "chore: build artifacts"),
            Text("author", "作者名稱", value: "Walis"),
            Text("email", "作者信箱", value: "walis@localhost")),

        Define(
            "git.tag",
            "Git Tag",
            "在目前的提交上打標籤；已存在時可選擇覆蓋。",
            ActionCategory.Git,
            "cil-layers",
            "git tag{{#if force}} -f{{/if}}{{#if message}} -a -m {{message}}{{/if}} {{tag}}",
            Text("tag", "標籤", true, "v$VERSION"),
            Text("message", "註記", placeholder: "Release $VERSION"),
            Boolean("force", "已存在就覆蓋")),

        Define(
            "git.push",
            "Git Push",
            "推送分支或標籤到遠端。",
            ActionCategory.Git,
            "cil-cloud-upload",
            "git push{{#if tags}} --tags{{/if}}{{#if force}} --force-with-lease{{/if}} {{remote}}{{#if ref}} {{ref}}{{/if}}",
            Text("remote", "遠端", value: "origin"),
            Text("ref", "分支或標籤", placeholder: "main"),
            Boolean("tags", "連標籤一起推"),
            Boolean("force", "安全強推（force-with-lease）")),

        Define(
            "git.submodules",
            "更新子模組",
            "初始化並更新所有子模組到 .gitmodules 記錄的版本。",
            ActionCategory.Git,
            "cil-layers",
            "git submodule update --init --recursive{{#if remote}} --remote{{/if}}",
            Boolean("remote", "抓子模組的最新提交")),

        Define(
            "git.describe",
            "取得版本描述",
            "用 git describe 算出版本字串並傳給後續步驟。",
            ActionCategory.Git,
            "cil-code",
            "printf '%s=%s\\n' {{variable}} \"$(git describe --tags --always{{#if dirty}} --dirty{{/if}})\" >> \"$WALIS_ENV_FILE\"",
            Text("variable", "存成變數", value: "GIT_VERSION"),
            Boolean("dirty", "有未提交變更時加上 -dirty", true)),

        Define(
            "git.short-sha",
            "取得短 commit",
            "把目前提交的短 SHA 傳給後續步驟，常拿來當映像標籤。",
            ActionCategory.Git,
            "cil-code",
            "printf '%s=%s\\n' {{variable}} \"$(git rev-parse --short HEAD)\" >> \"$WALIS_ENV_FILE\"",
            Text("variable", "存成變數", value: "GIT_SHA")),

        Define(
            "git.clean",
            "清理未追蹤檔案",
            "刪掉未追蹤的檔案與目錄，讓工作目錄回到乾淨狀態。",
            ActionCategory.Git,
            "cil-trash",
            "git clean -fd{{#if ignored}}x{{/if}}",
            Boolean("ignored", "連 .gitignore 忽略的也刪", help: "會刪掉 node_modules 這類目錄，下次建置要重裝。")),

        Define(
            "git.require-clean",
            "要求工作目錄乾淨",
            "有未提交的變更就讓建置失敗，避免把本機修改打進發行檔。",
            ActionCategory.Git,
            "cil-check-circle",
            "git diff --quiet && git diff --cached --quiet || { echo {{message}}; git status --short; exit 1; }",
            Text("message", "失敗訊息", value: "工作目錄有未提交的變更")),

        // ---------------------------------------------------------------- Node
        Define(
            "node.build",
            "npm run build",
            "執行專案的 build 腳本。",
            ActionCategory.Node,
            "cil-code",
            "npm run {{script}}{{#if arguments}} -- {{{arguments}}}{{/if}}",
            Text("script", "腳本名稱", value: "build"),
            Text("arguments", "額外參數", placeholder: "--mode production")),

        Define(
            "node.test",
            "npm test",
            "執行專案的測試。",
            ActionCategory.Node,
            "cil-check-circle",
            "npm test{{#if ci}} -- --ci{{/if}}",
            Boolean("ci", "加上 --ci", true, "多數測試框架在 CI 模式下不會進入互動監看。")),

        Define(
            "node.lint",
            "npm run lint",
            "執行 lint 腳本。",
            ActionCategory.Node,
            "cil-check-circle",
            "npm run {{script}}",
            Text("script", "腳本名稱", value: "lint")),

        Define(
            "node.audit",
            "npm audit",
            "檢查依賴的已知弱點，達到指定等級就失敗。",
            ActionCategory.Node,
            "cil-warning",
            "npm audit --audit-level={{{level}}}{{#if production}} --omit=dev{{/if}}",
            Select("level", "失敗門檻", "high", ("low", "low"), ("moderate", "moderate"), ("high", "high"), ("critical", "critical")),
            Boolean("production", "只檢查正式依賴", true)),

        Define(
            "node.pnpm-install",
            "pnpm install",
            "用 pnpm 安裝依賴（需要已裝 pnpm）。",
            ActionCategory.Node,
            "cil-cloud-download",
            "pnpm install{{#if frozen}} --frozen-lockfile{{/if}}",
            Boolean("frozen", "鎖定 lockfile", true)),

        Define(
            "node.pnpm-run",
            "pnpm run",
            "用 pnpm 執行腳本。",
            ActionCategory.Node,
            "cil-code",
            "pnpm run {{script}}{{#if arguments}} {{{arguments}}}{{/if}}",
            Text("script", "腳本名稱", true, "build"),
            Text("arguments", "額外參數")),

        Define(
            "node.yarn-install",
            "yarn install",
            "用 yarn 安裝依賴。",
            ActionCategory.Node,
            "cil-cloud-download",
            "yarn install{{#if frozen}} --frozen-lockfile{{/if}}",
            Boolean("frozen", "鎖定 lockfile", true)),

        Define(
            "node.yarn-run",
            "yarn run",
            "用 yarn 執行腳本。",
            ActionCategory.Node,
            "cil-code",
            "yarn run {{script}}{{#if arguments}} {{{arguments}}}{{/if}}",
            Text("script", "腳本名稱", true, "build"),
            Text("arguments", "額外參數")),

        Define(
            "node.bun-install",
            "bun install",
            "用 bun 安裝依賴。",
            ActionCategory.Node,
            "cil-cloud-download",
            "bun install{{#if frozen}} --frozen-lockfile{{/if}}",
            Boolean("frozen", "鎖定 lockfile", true)),

        Define(
            "node.bun-run",
            "bun run",
            "用 bun 執行腳本。",
            ActionCategory.Node,
            "cil-code",
            "bun run {{script}}{{#if arguments}} {{{arguments}}}{{/if}}",
            Text("script", "腳本名稱", true, "build"),
            Text("arguments", "額外參數")),

        Define(
            "node.publish",
            "npm publish",
            "把套件發布到 registry；需要先設定好 npm 的認證。",
            ActionCategory.Node,
            "cil-cloud-upload",
            "npm publish{{#if tag}} --tag {{tag}}{{/if}}{{#if access}} --access {{access}}{{/if}}{{#if dryRun}} --dry-run{{/if}}",
            Text("tag", "dist-tag", placeholder: "latest"),
            Select("access", "存取範圍", "", ("", "預設"), ("public", "public"), ("restricted", "restricted")),
            Boolean("dryRun", "只演練不真的發布")),

        // ---------------------------------------------------------------- .NET
        Define(
            "dotnet.format",
            "dotnet format",
            "檢查程式碼格式，不合格就失敗。",
            ActionCategory.Dotnet,
            "cil-check-circle",
            "dotnet format {{project}} --verify-no-changes",
            Path("project", "專案或方案", true, "Walis.slnx")),

        Define(
            "dotnet.pack",
            "dotnet pack",
            "打包成 NuGet 套件。",
            ActionCategory.Dotnet,
            "cil-cloud-download",
            "dotnet pack {{project}} -c {{configuration}} -o {{output}}{{#if version}} -p:PackageVersion={{version}}{{/if}}",
            Path("project", "專案", true, "src/Library/Library.csproj"),
            Select("configuration", "組態", "Release", ("Release", "Release"), ("Debug", "Debug")),
            Path("output", "輸出目錄", true, "nupkg"),
            Text("version", "版本", placeholder: "$VERSION")),

        Define(
            "dotnet.nuget-push",
            "推送 NuGet 套件",
            "把 .nupkg 推到 NuGet 來源。",
            ActionCategory.Dotnet,
            "cil-cloud-upload",
            "dotnet nuget push {{{package}}} --source {{source}} --api-key {{apiKey}} --skip-duplicate",
            Text("package", "套件檔", true, "nupkg/*.nupkg"),
            Text("source", "來源", value: "https://api.nuget.org/v3/index.json"),
            Text("apiKey", "API 金鑰", true, "$NUGET_API_KEY")),

        Define(
            "dotnet.ef-migrate",
            "套用 EF 遷移",
            "用 dotnet ef 把資料庫更新到最新的遷移（需要 dotnet-ef 工具）。",
            ActionCategory.Dotnet,
            "cil-storage",
            "dotnet ef database update --project {{project}}{{#if startup}} --startup-project {{startup}}{{/if}}",
            Path("project", "含遷移的專案", true, "src/Data/Data.csproj"),
            Path("startup", "啟動專案", placeholder: "src/Api/Api.csproj")),

        Define(
            "dotnet.tool",
            "執行 dotnet 工具",
            "執行本機或全域的 dotnet 工具。",
            ActionCategory.Dotnet,
            "cil-terminal",
            "dotnet {{{tool}}}{{#if arguments}} {{{arguments}}}{{/if}}",
            Text("tool", "工具", true, "csharpier"),
            Text("arguments", "參數", placeholder: "--check .")),

        Define(
            "dotnet.clean",
            "dotnet clean",
            "清掉建置產物。",
            ActionCategory.Dotnet,
            "cil-trash",
            "dotnet clean {{project}}{{#if configuration}} -c {{configuration}}{{/if}}",
            Path("project", "專案或方案", true, "Walis.slnx"),
            Text("configuration", "組態", placeholder: "Release")),

        // ---------------------------------------------------------------- Python
        Define(
            "python.venv",
            "建立虛擬環境",
            "建立 venv 並把它的 bin 放進 PATH 傳給後續步驟。",
            ActionCategory.Python,
            "cil-code",
            "{{{python}}} -m venv {{path}} && printf '%s=%s\\n' PATH \"$PWD/{{{path}}}/bin:$PATH\" >> \"$WALIS_ENV_FILE\"",
            Text("python", "Python 指令", value: "python3"),
            Path("path", "虛擬環境目錄", value: ".venv")),

        Define(
            "python.pip-install",
            "pip install",
            "安裝 requirements 檔裡的依賴。",
            ActionCategory.Python,
            "cil-cloud-download",
            "python3 -m pip install --disable-pip-version-check{{#if upgrade}} --upgrade{{/if}} -r {{requirements}}",
            Path("requirements", "requirements 檔", value: "requirements.txt"),
            Boolean("upgrade", "升級到最新")),

        Define(
            "python.run",
            "執行 Python 腳本",
            "執行一個 Python 檔或模組。",
            ActionCategory.Python,
            "cil-media-play",
            "python3 {{#if module}}-m {{{target}}}{{/if}}{{#unless module}}{{target}}{{/unless}}{{#if arguments}} {{{arguments}}}{{/if}}",
            Text("target", "腳本或模組", true, "scripts/build.py"),
            Boolean("module", "當成模組執行（-m）"),
            Text("arguments", "參數")),

        Define(
            "python.pytest",
            "pytest",
            "執行 pytest。",
            ActionCategory.Python,
            "cil-check-circle",
            "python3 -m pytest{{#if path}} {{path}}{{/if}}{{#if junit}} --junitxml={{junit}}{{/if}}{{#if arguments}} {{{arguments}}}{{/if}}",
            Path("path", "測試路徑", placeholder: "tests"),
            Path("junit", "JUnit 報告", placeholder: "reports/pytest.xml"),
            Text("arguments", "額外參數", placeholder: "-q")),

        Define(
            "python.ruff",
            "ruff 檢查",
            "用 ruff 做 lint（與可選的格式檢查）。",
            ActionCategory.Python,
            "cil-check-circle",
            "ruff check {{path}}{{#if format}} && ruff format --check {{path}}{{/if}}",
            Path("path", "路徑", value: "."),
            Boolean("format", "連格式一起檢查", true)),

        Define(
            "python.uv-sync",
            "uv sync",
            "用 uv 依 lockfile 安裝依賴（需要已裝 uv）。",
            ActionCategory.Python,
            "cil-cloud-download",
            "uv sync{{#if frozen}} --frozen{{/if}}{{#if noDev}} --no-dev{{/if}}",
            Boolean("frozen", "鎖定 lockfile", true),
            Boolean("noDev", "不裝開發依賴")),

        Define(
            "python.poetry-install",
            "poetry install",
            "用 poetry 安裝依賴。",
            ActionCategory.Python,
            "cil-cloud-download",
            "poetry install{{#if noRoot}} --no-root{{/if}}{{#if noDev}} --without dev{{/if}}",
            Boolean("noRoot", "不安裝專案本身"),
            Boolean("noDev", "不裝開發依賴")),

        Define(
            "python.build",
            "建置 wheel",
            "用 build 模組產生 sdist 與 wheel。",
            ActionCategory.Python,
            "cil-cloud-download",
            "python3 -m build --outdir {{output}}",
            Path("output", "輸出目錄", value: "dist")),

        // ---------------------------------------------------------------- Java
        Define(
            "java.maven",
            "Maven",
            "執行 Maven 目標，預設離線提示關閉、非互動。",
            ActionCategory.Java,
            "cil-code",
            "mvn -B{{#if skipTests}} -DskipTests{{/if}}{{#if settings}} -s {{settings}}{{/if}} {{{goals}}}",
            Text("goals", "目標", true, "clean package"),
            Boolean("skipTests", "略過測試"),
            Path("settings", "settings.xml", placeholder: ".mvn/settings.xml")),

        Define(
            "java.maven-test",
            "Maven 測試",
            "執行 mvn test。",
            ActionCategory.Java,
            "cil-check-circle",
            "mvn -B test{{#if arguments}} {{{arguments}}}{{/if}}",
            Text("arguments", "額外參數", placeholder: "-Dtest=SmokeTest")),

        Define(
            "java.gradle",
            "Gradle",
            "執行 Gradle 任務；有 gradlew 就用它。",
            ActionCategory.Java,
            "cil-code",
            "if [ -x ./gradlew ]; then ./gradlew --no-daemon {{{tasks}}}; else gradle --no-daemon {{{tasks}}}; fi",
            Text("tasks", "任務", true, "build")),

        Define(
            "java.gradle-test",
            "Gradle 測試",
            "執行 test 任務。",
            ActionCategory.Java,
            "cil-check-circle",
            "if [ -x ./gradlew ]; then ./gradlew --no-daemon test; else gradle --no-daemon test; fi"),

        Define(
            "java.run-jar",
            "執行 jar",
            "用 java -jar 執行。",
            ActionCategory.Java,
            "cil-media-play",
            "java{{#if options}} {{{options}}}{{/if}} -jar {{jar}}{{#if arguments}} {{{arguments}}}{{/if}}",
            Path("jar", "jar 檔", true, "target/app.jar"),
            Text("options", "JVM 選項", placeholder: "-Xmx512m"),
            Text("arguments", "程式參數")),

        // ---------------------------------------------------------------- Go
        Define(
            "go.mod-download",
            "go mod download",
            "下載模組依賴並驗證。",
            ActionCategory.Go,
            "cil-cloud-download",
            "go mod download && go mod verify"),

        Define(
            "go.build",
            "go build",
            "編譯 Go 程式。",
            ActionCategory.Go,
            "cil-code",
            "{{#if os}}GOOS={{{os}}} {{/if}}{{#if arch}}GOARCH={{{arch}}} {{/if}}go build{{#if ldflags}} -ldflags {{ldflags}}{{/if}} -o {{output}} {{{package}}}",
            Text("package", "套件", value: "./..."),
            Path("output", "輸出", value: "bin/app"),
            Text("os", "GOOS", placeholder: "linux"),
            Text("arch", "GOARCH", placeholder: "arm64"),
            Text("ldflags", "ldflags", placeholder: "-s -w")),

        Define(
            "go.test",
            "go test",
            "跑測試，可選擇產生覆蓋率。",
            ActionCategory.Go,
            "cil-check-circle",
            "go test{{#if race}} -race{{/if}}{{#if coverage}} -coverprofile={{coverage}}{{/if}} {{{package}}}",
            Text("package", "套件", value: "./..."),
            Boolean("race", "偵測資料競爭"),
            Path("coverage", "覆蓋率檔", placeholder: "coverage.out")),

        Define(
            "go.vet",
            "go vet",
            "靜態檢查。",
            ActionCategory.Go,
            "cil-check-circle",
            "go vet {{{package}}}",
            Text("package", "套件", value: "./...")),

        Define(
            "go.lint",
            "golangci-lint",
            "用 golangci-lint 檢查（需要已安裝）。",
            ActionCategory.Go,
            "cil-check-circle",
            "golangci-lint run{{#if timeout}} --timeout {{{timeout}}}{{/if}}",
            Text("timeout", "逾時", placeholder: "5m")),

        // ---------------------------------------------------------------- Rust
        Define(
            "rust.build",
            "cargo build",
            "編譯 Rust 專案。",
            ActionCategory.Rust,
            "cil-code",
            "cargo build{{#if release}} --release{{/if}}{{#if target}} --target {{target}}{{/if}}",
            Boolean("release", "release 模式", true),
            Text("target", "目標平台", placeholder: "aarch64-unknown-linux-gnu")),

        Define(
            "rust.test",
            "cargo test",
            "跑 Rust 測試。",
            ActionCategory.Rust,
            "cil-check-circle",
            "cargo test{{#if release}} --release{{/if}}{{#if arguments}} {{{arguments}}}{{/if}}",
            Boolean("release", "release 模式"),
            Text("arguments", "額外參數")),

        Define(
            "rust.clippy",
            "cargo clippy",
            "lint，警告視為錯誤。",
            ActionCategory.Rust,
            "cil-check-circle",
            "cargo clippy --all-targets -- -D warnings"),

        Define(
            "rust.fmt-check",
            "cargo fmt 檢查",
            "檢查格式。",
            ActionCategory.Rust,
            "cil-check-circle",
            "cargo fmt --all -- --check"),

        // ---------------------------------------------------------------- Ruby
        Define(
            "ruby.bundle-install",
            "bundle install",
            "安裝 Gemfile 依賴。",
            ActionCategory.Ruby,
            "cil-cloud-download",
            "bundle install{{#if deployment}} --deployment{{/if}}{{#if jobs}} --jobs {{{jobs}}}{{/if}}",
            Boolean("deployment", "部署模式（鎖定 Gemfile.lock）", true),
            Number("jobs", "平行數", "4")),

        Define(
            "ruby.rake",
            "rake",
            "執行 rake 任務。",
            ActionCategory.Ruby,
            "cil-code",
            "bundle exec rake {{{task}}}",
            Text("task", "任務", true, "assets:precompile")),

        Define(
            "ruby.rspec",
            "rspec",
            "跑 RSpec 測試。",
            ActionCategory.Ruby,
            "cil-check-circle",
            "bundle exec rspec{{#if path}} {{path}}{{/if}}{{#if format}} --format {{format}}{{/if}}",
            Path("path", "測試路徑", placeholder: "spec"),
            Text("format", "輸出格式", placeholder: "progress")),

        // ---------------------------------------------------------------- PHP
        Define(
            "php.composer-install",
            "composer install",
            "安裝 Composer 依賴。",
            ActionCategory.Php,
            "cil-cloud-download",
            "composer install --no-interaction --prefer-dist{{#if noDev}} --no-dev{{/if}}{{#if optimize}} --optimize-autoloader{{/if}}",
            Boolean("noDev", "不裝開發依賴"),
            Boolean("optimize", "最佳化 autoloader", true)),

        Define(
            "php.phpunit",
            "PHPUnit",
            "跑 PHPUnit 測試。",
            ActionCategory.Php,
            "cil-check-circle",
            "vendor/bin/phpunit{{#if config}} -c {{config}}{{/if}}{{#if arguments}} {{{arguments}}}{{/if}}",
            Path("config", "設定檔", placeholder: "phpunit.xml"),
            Text("arguments", "額外參數")),

        Define(
            "php.artisan",
            "Laravel artisan",
            "執行 artisan 命令。",
            ActionCategory.Php,
            "cil-terminal",
            "php artisan {{{command}}}",
            Text("command", "命令", true, "migrate --force")),

        // ---------------------------------------------------------------- Containers
        Define(
            "docker.pull",
            "Docker Pull",
            "拉取映像。",
            ActionCategory.Container,
            "cil-cloud-download",
            "docker pull {{image}}",
            Text("image", "映像", true, "node:20")),

        Define(
            "docker.tag",
            "Docker Tag",
            "替映像加另一個標籤。",
            ActionCategory.Container,
            "cil-layers",
            "docker tag {{source}} {{target}}",
            Text("source", "來源", true, "app:$GIT_SHA"),
            Text("target", "新標籤", true, "registry.example.com/app:latest")),

        Define(
            "docker.login",
            "Docker Login",
            "登入登錄伺服器；密碼從標準輸入讀，不會出現在命令列。",
            ActionCategory.Container,
            "cil-lock-locked",
            "printf '%s' {{password}} | docker login {{registry}} -u {{username}} --password-stdin",
            Text("registry", "登錄伺服器", value: "ghcr.io"),
            Text("username", "帳號", true, "$REGISTRY_USER"),
            Text("password", "密碼或權杖", true, "$REGISTRY_TOKEN")),

        Define(
            "docker.run",
            "Docker Run",
            "啟動一個容器（背景），常用在測試需要的資料庫。",
            ActionCategory.Container,
            "cil-media-play",
            "docker rm -f {{name}} >/dev/null 2>&1 || true; docker run -d --name {{name}}{{#if ports}} -p {{{ports}}}{{/if}}{{#if env}} {{{env}}}{{/if}} {{image}}{{#if command}} {{{command}}}{{/if}}",
            Text("name", "容器名稱", true, "ci-postgres"),
            Text("image", "映像", true, "postgres:16"),
            Text("ports", "連接埠對應", placeholder: "5432:5432"),
            Text("env", "環境變數", placeholder: "-e POSTGRES_PASSWORD=ci", help: "原樣接在命令裡，多個用空白隔開。"),
            Text("command", "容器命令")),

        Define(
            "docker.stop",
            "Docker Stop",
            "停止並移除容器；不存在時不會失敗。",
            ActionCategory.Container,
            "cil-ban",
            "docker rm -f {{name}} >/dev/null 2>&1 || true",
            Text("name", "容器名稱", true, "ci-postgres")),

        Define(
            "docker.exec",
            "Docker Exec",
            "在執行中的容器裡跑命令。",
            ActionCategory.Container,
            "cil-terminal",
            "docker exec {{name}} sh -c {{command}}",
            Text("name", "容器名稱", true),
            Multiline("command", "命令", true)),

        Define(
            "docker.save",
            "匯出映像",
            "把映像存成 tar（可壓縮），當產出保存。",
            ActionCategory.Container,
            "cil-cloud-download",
            "docker save {{image}}{{#if gzip}} | gzip{{/if}} > {{output}}",
            Text("image", "映像", true, "app:$GIT_SHA"),
            Path("output", "輸出檔", true, "dist/app.tar.gz"),
            Boolean("gzip", "gzip 壓縮", true)),

        Define(
            "docker.prune",
            "清理 Docker",
            "清掉沒用到的映像、容器與快取，釋放磁碟。",
            ActionCategory.Container,
            "cil-trash",
            "docker system prune -f{{#if all}} -a{{/if}}{{#if volumes}} --volumes{{/if}}",
            Boolean("all", "連沒在用的映像也刪"),
            Boolean("volumes", "連 volume 也刪", help: "會刪掉沒有容器使用的 volume，資料會消失。")),

        Define(
            "docker.compose-build",
            "Compose Build",
            "建置 compose 定義的映像。",
            ActionCategory.Container,
            "cil-layers",
            "docker compose{{#if file}} -f {{file}}{{/if}} build{{#if pull}} --pull{{/if}}{{#if noCache}} --no-cache{{/if}}",
            Path("file", "compose 檔", placeholder: "docker-compose.yml"),
            Boolean("pull", "先拉最新基底映像", true),
            Boolean("noCache", "不用快取")),

        Define(
            "docker.compose-exec",
            "Compose Exec",
            "在 compose 服務裡跑命令。",
            ActionCategory.Container,
            "cil-terminal",
            "docker compose{{#if file}} -f {{file}}{{/if}} exec -T {{service}} sh -c {{command}}",
            Path("file", "compose 檔", placeholder: "docker-compose.yml"),
            Text("service", "服務", true, "app"),
            Multiline("command", "命令", true)),

        Define(
            "docker.compose-logs",
            "Compose 日誌",
            "把服務的日誌印進建置輸出，常放在失敗時執行。",
            ActionCategory.Container,
            "cil-list-rich",
            "docker compose{{#if file}} -f {{file}}{{/if}} logs --no-color --tail {{{tail}}}{{#if service}} {{service}}{{/if}}",
            Path("file", "compose 檔", placeholder: "docker-compose.yml"),
            Text("service", "服務", placeholder: "app"),
            Number("tail", "最後幾行", "200")),

        // ---------------------------------------------------------------- Kubernetes
        Define(
            "k8s.apply",
            "kubectl apply",
            "套用 manifest。",
            ActionCategory.Kubernetes,
            "cil-cloud-upload",
            "kubectl apply{{#if namespace}} -n {{namespace}}{{/if}}{{#if context}} --context {{context}}{{/if}} -f {{path}}",
            Path("path", "manifest 檔或目錄", true, "k8s/"),
            Text("namespace", "命名空間"),
            Text("context", "context")),

        Define(
            "k8s.set-image",
            "更新部署映像",
            "把 Deployment 的容器換成新映像。",
            ActionCategory.Kubernetes,
            "cil-layers",
            "kubectl set image{{#if namespace}} -n {{namespace}}{{/if}} deployment/{{{deployment}}} {{{container}}}={{image}}",
            Text("deployment", "Deployment", true, "app"),
            Text("container", "容器名稱", true, "app"),
            Text("image", "映像", true, "registry.example.com/app:$GIT_SHA"),
            Text("namespace", "命名空間")),

        Define(
            "k8s.rollout-restart",
            "重新啟動部署",
            "rollout restart。",
            ActionCategory.Kubernetes,
            "cil-reload",
            "kubectl rollout restart{{#if namespace}} -n {{namespace}}{{/if}} deployment/{{{deployment}}}",
            Text("deployment", "Deployment", true, "app"),
            Text("namespace", "命名空間")),

        Define(
            "k8s.rollout-status",
            "等待部署完成",
            "等 rollout 完成，逾時就失敗。",
            ActionCategory.Kubernetes,
            "cil-clock",
            "kubectl rollout status{{#if namespace}} -n {{namespace}}{{/if}} deployment/{{{deployment}}} --timeout={{{timeout}}}",
            Text("deployment", "Deployment", true, "app"),
            Text("namespace", "命名空間"),
            Text("timeout", "逾時", value: "5m")),

        Define(
            "k8s.helm-upgrade",
            "Helm Upgrade",
            "helm upgrade --install。",
            ActionCategory.Kubernetes,
            "cil-cloud-upload",
            "helm upgrade --install {{release}} {{chart}}{{#if namespace}} -n {{namespace}} --create-namespace{{/if}}{{#if values}} -f {{values}}{{/if}}{{#if set}} --set {{{set}}}{{/if}} --wait",
            Text("release", "release 名稱", true, "app"),
            Text("chart", "chart", true, "./charts/app"),
            Text("namespace", "命名空間"),
            Path("values", "values 檔", placeholder: "values.prod.yaml"),
            Text("set", "覆寫值", placeholder: "image.tag=$GIT_SHA")),

        // ---------------------------------------------------------------- Databases
        Define(
            "db.postgres-sql",
            "PostgreSQL 執行 SQL",
            "用 psql 執行一段 SQL。",
            ActionCategory.Database,
            "cil-storage",
            "psql {{url}} -v ON_ERROR_STOP=1 -c {{sql}}",
            Text("url", "連線字串", true, "$DATABASE_URL"),
            Multiline("sql", "SQL", true, "SELECT 1;")),

        Define(
            "db.postgres-file",
            "PostgreSQL 執行檔案",
            "用 psql 執行 SQL 檔。",
            ActionCategory.Database,
            "cil-storage",
            "psql {{url}} -v ON_ERROR_STOP=1 -f {{path}}",
            Text("url", "連線字串", true, "$DATABASE_URL"),
            Path("path", "SQL 檔", true, "db/migrate.sql")),

        Define(
            "db.postgres-dump",
            "PostgreSQL 備份",
            "pg_dump 成壓縮檔。",
            ActionCategory.Database,
            "cil-cloud-download",
            "pg_dump {{url}} -Fc -f {{output}}",
            Text("url", "連線字串", true, "$DATABASE_URL"),
            Path("output", "輸出檔", true, "backups/db.dump")),

        Define(
            "db.postgres-restore",
            "PostgreSQL 還原",
            "pg_restore 還原到資料庫。",
            ActionCategory.Database,
            "cil-cloud-upload",
            "pg_restore -d {{url}} --clean --if-exists --no-owner {{path}}",
            Text("url", "連線字串", true, "$DATABASE_URL"),
            Path("path", "備份檔", true, "backups/db.dump")),

        Define(
            "db.mysql-sql",
            "MySQL 執行 SQL",
            "用 mysql 執行一段 SQL；密碼從 MYSQL_PWD 環境變數讀。",
            ActionCategory.Database,
            "cil-storage",
            "mysql -h {{host}} -u {{user}} {{database}} -e {{sql}}",
            Text("host", "主機", value: "127.0.0.1"),
            Text("user", "帳號", true, "root"),
            Text("database", "資料庫", true),
            Multiline("sql", "SQL", true, "SELECT 1;")),

        Define(
            "db.mysql-dump",
            "MySQL 備份",
            "mysqldump 成 gzip；密碼從 MYSQL_PWD 環境變數讀。",
            ActionCategory.Database,
            "cil-cloud-download",
            "mysqldump -h {{host}} -u {{user}} --single-transaction {{database}} | gzip > {{output}}",
            Text("host", "主機", value: "127.0.0.1"),
            Text("user", "帳號", true, "root"),
            Text("database", "資料庫", true),
            Path("output", "輸出檔", true, "backups/db.sql.gz")),

        Define(
            "db.sqlite-sql",
            "SQLite 執行 SQL",
            "用 sqlite3 執行一段 SQL。",
            ActionCategory.Database,
            "cil-storage",
            "sqlite3 {{path}} {{sql}}",
            Path("path", "資料庫檔", true, "data/app.db"),
            Multiline("sql", "SQL", true, "VACUUM;")),

        Define(
            "db.redis-cli",
            "Redis 命令",
            "用 redis-cli 執行命令。",
            ActionCategory.Database,
            "cil-storage",
            "redis-cli{{#if url}} -u {{url}}{{/if}} {{{command}}}",
            Text("url", "連線網址", placeholder: "redis://127.0.0.1:6379"),
            Text("command", "命令", true, "FLUSHDB")),

        // ---------------------------------------------------------------- Cloud and infrastructure
        Define(
            "cloud.aws-s3-sync",
            "同步到 S3",
            "aws s3 sync 到 bucket（需要 AWS CLI 與認證）。",
            ActionCategory.Cloud,
            "cil-cloud-upload",
            "aws s3 sync {{source}} {{destination}}{{#if delete}} --delete{{/if}}{{#if cacheControl}} --cache-control {{cacheControl}}{{/if}}",
            Path("source", "來源目錄", true, "dist/"),
            Text("destination", "s3:// 路徑", true, "s3://my-bucket/app"),
            Boolean("delete", "刪除目的地多餘的檔案"),
            Text("cacheControl", "Cache-Control", placeholder: "max-age=31536000")),

        Define(
            "cloud.aws-cli",
            "AWS CLI",
            "執行任意 aws 命令。",
            ActionCategory.Cloud,
            "cil-terminal",
            "aws{{#if profile}} --profile {{profile}}{{/if}}{{#if region}} --region {{region}}{{/if}} {{{command}}}",
            Text("command", "命令", true, "lambda update-function-code --function-name app --zip-file fileb://dist/app.zip"),
            Text("profile", "profile"),
            Text("region", "區域", placeholder: "ap-northeast-1")),

        Define(
            "cloud.gcloud",
            "gcloud",
            "執行任意 gcloud 命令。",
            ActionCategory.Cloud,
            "cil-terminal",
            "gcloud {{{command}}}{{#if project}} --project {{project}}{{/if}} --quiet",
            Text("command", "命令", true, "run deploy app --image gcr.io/proj/app:$GIT_SHA --region asia-east1"),
            Text("project", "專案")),

        Define(
            "cloud.az",
            "Azure CLI",
            "執行任意 az 命令。",
            ActionCategory.Cloud,
            "cil-terminal",
            "az {{{command}}} --output none",
            Text("command", "命令", true, "webapp deploy --name app --resource-group rg --src-path dist/app.zip")),

        Define(
            "cloud.rclone-sync",
            "rclone 同步",
            "用 rclone 同步到任何它支援的儲存（需要已設定 remote）。",
            ActionCategory.Cloud,
            "cil-cloud-upload",
            "rclone sync {{source}} {{destination}}{{#if checksum}} --checksum{{/if}}",
            Path("source", "來源", true, "dist/"),
            Text("destination", "remote:路徑", true, "backup:app/dist"),
            Boolean("checksum", "用校驗值比對")),

        Define(
            "cloud.terraform-plan",
            "Terraform Plan",
            "init 後 plan，把結果存成檔案給 apply 用。",
            ActionCategory.Cloud,
            "cil-layers",
            "terraform{{#if directory}} -chdir={{directory}}{{/if}} init -input=false && terraform{{#if directory}} -chdir={{directory}}{{/if}} plan -input=false -out={{plan}}{{#if varFile}} -var-file={{varFile}}{{/if}}",
            Path("directory", "工作目錄", placeholder: "infra"),
            Text("plan", "plan 檔名", value: "tfplan"),
            Path("varFile", "變數檔", placeholder: "prod.tfvars")),

        Define(
            "cloud.terraform-apply",
            "Terraform Apply",
            "套用 plan 檔。",
            ActionCategory.Cloud,
            "cil-cloud-upload",
            "terraform{{#if directory}} -chdir={{directory}}{{/if}} apply -input=false -auto-approve {{plan}}",
            Path("directory", "工作目錄", placeholder: "infra"),
            Text("plan", "plan 檔名", value: "tfplan")),

        Define(
            "cloud.ansible-playbook",
            "Ansible Playbook",
            "執行 playbook。",
            ActionCategory.Cloud,
            "cil-terminal",
            "ansible-playbook -i {{inventory}} {{playbook}}{{#if limit}} --limit {{limit}}{{/if}}{{#if extra}} -e {{extra}}{{/if}}",
            Path("inventory", "inventory", true, "inventory/prod.ini"),
            Path("playbook", "playbook", true, "deploy.yml"),
            Text("limit", "只對哪些主機"),
            Text("extra", "額外變數", placeholder: "version=$VERSION")),

        // ---------------------------------------------------------------- Deployment
        Define(
            "deploy.systemd",
            "systemd 服務",
            "重新啟動、啟動或停止 systemd 服務（需要 sudo 免密碼）。",
            ActionCategory.Deploy,
            "cil-reload",
            "sudo systemctl {{{operation}}} {{service}}",
            Select("operation", "操作", "restart", ("restart", "restart"), ("reload", "reload"), ("start", "start"), ("stop", "stop")),
            Text("service", "服務名稱", true, "app.service")),

        Define(
            "deploy.launchd",
            "macOS 服務",
            "用 launchctl kickstart 重新啟動 LaunchDaemon（需要 sudo 免密碼）。",
            ActionCategory.Deploy,
            "cil-reload",
            "sudo launchctl kickstart -k system/{{{label}}}",
            Text("label", "服務標籤", true, "com.example.app")),

        Define(
            "deploy.symlink",
            "切換 current 連結",
            "把 current 符號連結指到新版本目錄，藍綠切換用。",
            ActionCategory.Deploy,
            "cil-share-alt",
            "ln -sfn {{target}} {{link}}",
            Path("target", "新版本目錄", true, "releases/$BUILD_STAMP"),
            Path("link", "連結", true, "current")),

        Define(
            "deploy.pm2",
            "PM2",
            "用 pm2 重新載入或重啟程序。",
            ActionCategory.Deploy,
            "cil-reload",
            "pm2 {{{operation}}} {{name}}",
            Select("operation", "操作", "reload", ("reload", "reload"), ("restart", "restart"), ("stop", "stop")),
            Text("name", "程序名稱或 ecosystem 檔", true, "app")),

        Define(
            "deploy.cloudflare-purge",
            "清除 Cloudflare 快取",
            "呼叫 Cloudflare API 清掉整個 zone 的快取。",
            ActionCategory.Deploy,
            "cil-loop-circular",
            "curl -fsS -X POST \"https://api.cloudflare.com/client/v4/zones/{{{zone}}}/purge_cache\" -H \"Authorization: Bearer {{{token}}}\" -H 'Content-Type: application/json' --data '{\"purge_everything\":true}'",
            Text("zone", "Zone ID", true, "$CF_ZONE_ID"),
            Text("token", "API Token", true, "$CF_API_TOKEN")),

        Define(
            "deploy.healthcheck",
            "部署後健康檢查",
            "打健康檢查網址，回應碼不對就失敗（可等一下再打）。",
            ActionCategory.Deploy,
            "cil-check-circle",
            "sleep {{{delay}}}; code=$(curl -s -o /dev/null -w '%{http_code}' {{url}}); [ \"$code\" = {{{expected}}} ] || { echo \"健康檢查回 $code\"; exit 1; }",
            Text("url", "網址", true, "https://example.com/health"),
            Number("expected", "預期回應碼", "200"),
            Number("delay", "先等幾秒", "5")),

        Define(
            "deploy.ssh-script",
            "SSH 執行本機腳本",
            "把本機的腳本檔送到遠端執行。",
            ActionCategory.Deploy,
            "cil-terminal",
            "ssh -o BatchMode=yes{{#if port}} -p {{{port}}}{{/if}} {{host}} 'bash -s' < {{script}}",
            Text("host", "目標主機", true, "user@host"),
            Path("script", "腳本檔", true, "scripts/remote-deploy.sh"),
            Number("port", "連接埠")),

        // ---------------------------------------------------------------- Checks
        Define(
            "check.env",
            "檢查環境變數",
            "變數沒設或是空的就失敗。",
            ActionCategory.Check,
            "cil-check-circle",
            "[ -n \"${{{name}}}\" ] || { echo {{message}}; exit 1; }",
            Text("name", "變數名稱", true, "DEPLOY_TOKEN"),
            Text("message", "失敗訊息", value: "缺少必要的環境變數")),

        Define(
            "check.port-free",
            "檢查連接埠未被占用",
            "連接埠已有人在聽就失敗。",
            ActionCategory.Check,
            "cil-check-circle",
            "if nc -z 127.0.0.1 {{{port}}}; then echo {{message}}; exit 1; fi",
            Number("port", "連接埠", "8080"),
            Text("message", "失敗訊息", value: "連接埠已被占用")),

        Define(
            "check.url",
            "檢查網址回應碼",
            "回應碼不符就失敗。",
            ActionCategory.Check,
            "cil-check-circle",
            "code=$(curl -s -o /dev/null -w '%{http_code}' {{url}}); [ \"$code\" = {{{expected}}} ] || { echo \"回應碼 $code\"; exit 1; }",
            Text("url", "網址", true, "https://example.com/"),
            Number("expected", "預期回應碼", "200")),

        Define(
            "check.disk",
            "檢查磁碟空間",
            "可用空間低於門檻就失敗。",
            ActionCategory.Check,
            "cil-storage",
            "free=$(df -Pm {{path}} | awk 'NR==2 {print $4}'); [ \"$free\" -ge {{{minimum}}} ] || { echo \"可用空間只剩 ${free} MB\"; exit 1; }",
            Path("path", "路徑", value: "."),
            Number("minimum", "至少要有（MB）", "1024")),

        Define(
            "check.json",
            "檢查 JSON 格式",
            "檔案不是合法 JSON 就失敗。",
            ActionCategory.Check,
            "cil-check-circle",
            "python3 -c 'import json,sys; json.load(open(sys.argv[1]))' {{path}}",
            Path("path", "檔案", true, "config.json")),

        Define(
            "check.version",
            "檢查工具版本",
            "命令輸出裡沒有預期的版本字串就失敗。",
            ActionCategory.Check,
            "cil-check-circle",
            "{{{command}}} 2>&1 | grep -q {{expected}} || { echo {{message}}; {{{command}}}; exit 1; }",
            Text("command", "命令", true, "node --version"),
            Text("expected", "要包含", true, "v20"),
            Text("message", "失敗訊息", value: "工具版本不符")),

        // ---------------------------------------------------------------- Tests
        Define(
            "test.playwright",
            "Playwright",
            "跑 Playwright 端對端測試。",
            ActionCategory.Test,
            "cil-check-circle",
            "npx playwright test{{#if project}} --project {{project}}{{/if}}{{#if reporter}} --reporter {{reporter}}{{/if}}",
            Text("project", "專案", placeholder: "chromium"),
            Text("reporter", "報告格式", placeholder: "line")),

        Define(
            "test.cypress",
            "Cypress",
            "跑 Cypress 測試（headless）。",
            ActionCategory.Test,
            "cil-check-circle",
            "npx cypress run{{#if browser}} --browser {{browser}}{{/if}}{{#if spec}} --spec {{spec}}{{/if}}",
            Text("browser", "瀏覽器", placeholder: "chrome"),
            Text("spec", "測試檔", placeholder: "cypress/e2e/**")),

        Define(
            "test.k6",
            "k6 負載測試",
            "跑 k6 腳本。",
            ActionCategory.Test,
            "cil-speedometer",
            "k6 run{{#if vus}} --vus {{{vus}}}{{/if}}{{#if duration}} --duration {{duration}}{{/if}} {{script}}",
            Path("script", "腳本", true, "load/smoke.js"),
            Number("vus", "虛擬使用者數"),
            Text("duration", "持續時間", placeholder: "30s")),

        Define(
            "test.junit-summary",
            "摘要 JUnit 報告",
            "從 JUnit XML 數出測試數與失敗數印進日誌，有失敗就讓建置失敗。",
            ActionCategory.Test,
            "cil-list-rich",
            "python3 -c 'import glob,sys,xml.etree.ElementTree as E\\nt=f=0\\nfor p in glob.glob(sys.argv[1],recursive=True):\\n r=E.parse(p).getroot()\\n for s in ([r] if r.tag==\"testsuite\" else r.iter(\"testsuite\")):\\n  t+=int(s.get(\"tests\",0)); f+=int(s.get(\"failures\",0))+int(s.get(\"errors\",0))\\nprint(f\"測試 {t}，失敗 {f}\")\\nsys.exit(1 if f else 0)' {{pattern}}",
            Text("pattern", "報告檔樣式", true, "reports/**/*.xml")),

        // ---------------------------------------------------------------- Security
        Define(
            "security.trivy-fs",
            "Trivy 掃描原始碼",
            "掃描依賴與設定檔的弱點，達門檻就失敗（需要 trivy）。",
            ActionCategory.Security,
            "cil-warning",
            "trivy fs --exit-code 1 --severity {{{severity}}}{{#if ignoreUnfixed}} --ignore-unfixed{{/if}} {{path}}",
            Path("path", "路徑", value: "."),
            Text("severity", "門檻", value: "HIGH,CRITICAL"),
            Boolean("ignoreUnfixed", "忽略還沒有修補的", true)),

        Define(
            "security.trivy-image",
            "Trivy 掃描映像",
            "掃描容器映像的弱點。",
            ActionCategory.Security,
            "cil-warning",
            "trivy image --exit-code 1 --severity {{{severity}}}{{#if ignoreUnfixed}} --ignore-unfixed{{/if}} {{image}}",
            Text("image", "映像", true, "app:$GIT_SHA"),
            Text("severity", "門檻", value: "HIGH,CRITICAL"),
            Boolean("ignoreUnfixed", "忽略還沒有修補的", true)),

        Define(
            "security.gitleaks",
            "Gitleaks 找機密",
            "掃描程式碼裡有沒有寫死的金鑰或密碼（需要 gitleaks）。",
            ActionCategory.Security,
            "cil-lock-locked",
            "gitleaks {{{mode}}} --no-banner --redact{{#if config}} -c {{config}}{{/if}}",
            Select("mode", "範圍", "detect", ("detect", "整個歷史"), ("protect --staged", "只看暫存的變更")),
            Path("config", "設定檔", placeholder: ".gitleaks.toml")),

        Define(
            "security.grype",
            "Grype 掃描",
            "用 grype 掃描目錄或映像。",
            ActionCategory.Security,
            "cil-warning",
            "grype {{target}} --fail-on {{{severity}}}",
            Text("target", "目標", true, "dir:.", help: "dir:路徑 或 映像名稱。"),
            Select("severity", "門檻", "high", ("medium", "medium"), ("high", "high"), ("critical", "critical"))),

        Define(
            "sbom.generate",
            "產生軟體物料清單 (SBOM)",
            "為工作目錄或指定路徑產生 CycloneDX 或 SPDX 格式之軟體物料清單（優先使用 syft，亦可退回 npm sbom 或 dotnet-sbom）。",
            ActionCategory.Security,
            "cil-shield-alt",
            """
            TARGET={{path}}
            TARGET="${TARGET:-.}"
            FMT={{format}}
            FMT="${FMT:-cyclonedx-json}"
            OUT_FILE={{output}}
            TOOL={{tool}}
            TOOL="${TOOL:-auto}"
            if [ -z "$OUT_FILE" ]; then
              case "$FMT" in
                spdx-json|spdx-2.2-json) OUT_FILE="bom.spdx.json" ;;
                *) OUT_FILE="bom.cdx.json" ;;
              esac
            fi
            OUT_DIR="$(dirname "$OUT_FILE")"
            if [ -n "$OUT_DIR" ] && [ "$OUT_DIR" != "." ]; then
              mkdir -p "$OUT_DIR"
            fi
            run_syft() {
              echo "=> 使用 syft 產生 SBOM ($FMT)..."
              if [ "$FMT" = "spdx-2.2-json" ]; then
                echo "錯誤：SPDX 2.2 請指定 dotnet 工具；Syft 請選 CycloneDX 或 SPDX 2.3。" >&2
                exit 1
              fi
              syft "$TARGET" -o "$FMT" > "$OUT_FILE"
            }
            run_npm() {
              echo "=> 使用 npm sbom 產生 SBOM ($FMT)..."
              if [ "$FMT" = "spdx-2.2-json" ]; then
                echo "錯誤：npm sbom 不支援指定 SPDX 2.2，請改選 SPDX 2.3。" >&2
                exit 1
              fi
              NPM_FMT="cyclonedx"
              if [ "$FMT" = "spdx-json" ]; then
                NPM_FMT="spdx"
              fi
              (cd "$TARGET" && npm sbom --sbom-format "$NPM_FMT") > "$OUT_FILE"
            }
            run_dotnet() {
              echo "=> 使用 dotnet-sbom 產生 SBOM ($FMT)..."
              if [ "$FMT" != "spdx-2.2-json" ]; then
                echo "錯誤：Microsoft.Sbom.DotNetTool 僅在此動作支援 SPDX 2.2；其他格式請使用 syft。" >&2
                exit 1
              fi
              DOTNET_SBOM_BIN=""
              if command -v dotnet-sbom >/dev/null 2>&1; then
                DOTNET_SBOM_BIN="dotnet-sbom"
              elif dotnet sbom --help >/dev/null 2>&1; then
                DOTNET_SBOM_BIN="dotnet sbom"
              fi
              if [ -z "$DOTNET_SBOM_BIN" ]; then
                echo "錯誤：未找到 dotnet-sbom 工具，請先安裝 Microsoft.Sbom.DotNetTool" >&2
                exit 1
              fi
              MANIFEST_DIR="$(mktemp -d)"
              trap 'rm -rf "$MANIFEST_DIR"' EXIT HUP INT TERM
              $DOTNET_SBOM_BIN generate -b "$TARGET" -bc "$TARGET" -pn "${WALIS_JOB_NAME:-Project}" -pv "${WALIS_BUILD_NUMBER:-1.0.0}" -ps "${WALIS_JOB_NAME:-Walis}" -nsb "https://walis.local" -mi SPDX:2.2 -m "$MANIFEST_DIR"
              cp "$MANIFEST_DIR/_manifest/spdx_2.2/manifest.spdx.json" "$OUT_FILE"
              rm -rf "$MANIFEST_DIR"
              trap - EXIT HUP INT TERM
            }
            case "$TOOL" in
              syft)
                if ! command -v syft >/dev/null 2>&1; then
                  echo "錯誤：指定使用 syft 但系統未安裝 syft" >&2
                  exit 1
                fi
                run_syft
                ;;
              npm)
                run_npm
                ;;
              dotnet)
                run_dotnet
                ;;
              *)
                if [ "$FMT" = "spdx-2.2-json" ]; then
                  run_dotnet
                elif command -v syft >/dev/null 2>&1; then
                  run_syft
                elif [ -f "$TARGET/package.json" ] && command -v npm >/dev/null 2>&1; then
                  run_npm
                elif (find "$TARGET" -maxdepth 2 \( -name "*.sln" -o -name "*.slnx" -o -name "*.csproj" \) 2>/dev/null | grep -q .) && (command -v dotnet-sbom >/dev/null 2>&1 || dotnet sbom --help >/dev/null 2>&1); then
                  run_dotnet
                else
                  echo "錯誤：未找到 syft，且未能自動偵測並執行合適的原生 SBOM 工具" >&2
                  exit 1
                fi
                ;;
            esac
            if [ ! -s "$OUT_FILE" ]; then
              echo "錯誤：SBOM 產出檔案不存在或為空：$OUT_FILE" >&2
              exit 1
            fi
            echo "SBOM 產生成功：$OUT_FILE ($(wc -c < "$OUT_FILE" | tr -d ' ') bytes)"
            if [ -n "$WALIS_OUTPUT_FILE" ]; then
              printf 'path=%s\nformat=%s\n' "$OUT_FILE" "$FMT" >> "$WALIS_OUTPUT_FILE"
            fi
            """,
            Path("path", "目標路徑", value: ".", help: "要掃描之專案目錄或工作目錄路徑。"),
            Select("format", "輸出格式", "cyclonedx-json", ("cyclonedx-json", "CycloneDX 1.5 JSON (*.cdx.json)"), ("spdx-json", "SPDX 2.3 JSON (*.spdx.json)"), ("spdx-2.2-json", "SPDX 2.2 JSON (Microsoft.Sbom.DotNetTool)")),
            Path("output", "產出檔案路徑", placeholder: "bom.cdx.json", help: "產出之檔案路徑，留空時依格式預設為 bom.cdx.json 或 bom.spdx.json。"),
            Select("tool", "工具偏好", "auto", ("auto", "自動偵測 (優先使用 syft，退回原生工具)"), ("syft", "僅使用 Syft"), ("npm", "僅使用 npm sbom"), ("dotnet", "僅使用 dotnet-sbom"))),

        Define(
            "sbom.scan",
            "掃描軟體物料清單 (SBOM)",
            "使用 Grype、Trivy 或 OSV-Scanner 對 SBOM 檔案進行漏洞掃描，並依品質閘門門檻阻擋嚴重漏洞。",
            ActionCategory.Security,
            "cil-shield-alt",
            """
            FILE={{file}}
            TOOL={{tool}}
            TOOL="${TOOL:-auto}"
            FAIL_ON={{failOn}}
            FAIL_ON="${FAIL_ON:-high}"
            if [ ! -f "$FILE" ]; then
              echo "錯誤：找不到 SBOM 檔案：$FILE" >&2
              exit 1
            fi
            run_grype() {
              echo "=> 使用 Grype 掃描 SBOM ($FILE)..."
              if [ "$FAIL_ON" = "all" ]; then
                echo "錯誤：包含未分級漏洞的全部阻擋政策請使用 Trivy 或 OSV-Scanner。" >&2
                exit 1
              fi
              if [ "$FAIL_ON" = "never" ]; then
                grype sbom:"$FILE"
              else
                grype sbom:"$FILE" --fail-on "$FAIL_ON"
              fi
            }
            run_trivy() {
              echo "=> 使用 Trivy 掃描 SBOM ($FILE)..."
              case "$FAIL_ON" in
                never)
                  trivy sbom "$FILE"
                  ;;
                all)
                  trivy sbom --exit-code 1 --severity UNKNOWN,LOW,MEDIUM,HIGH,CRITICAL "$FILE"
                  ;;
                critical)
                  trivy sbom --exit-code 1 --severity CRITICAL "$FILE"
                  ;;
                medium)
                  trivy sbom --exit-code 1 --severity MEDIUM,HIGH,CRITICAL "$FILE"
                  ;;
                *)
                  trivy sbom --exit-code 1 --severity HIGH,CRITICAL "$FILE"
                  ;;
              esac
            }
            run_osv() {
              echo "=> 使用 OSV-Scanner 掃描 SBOM ($FILE)..."
              # OSV's exit status does not implement a severity threshold. Never silently replace the requested policy.
              if [ "$FAIL_ON" != "never" ] && [ "$FAIL_ON" != "all" ]; then
                echo "錯誤：OSV-Scanner 請選擇「所有漏洞」或「不阻擋」；依嚴重度阻擋請使用 Grype 或 Trivy。" >&2
                exit 1
              fi
              OSV_STATUS=0
              osv-scanner --sbom "$FILE" || OSV_STATUS=$?
              case "$OSV_STATUS" in
                0) return 0 ;;
                1) [ "$FAIL_ON" = "never" ] && return 0; return 1 ;;
                *) echo "錯誤：OSV-Scanner 未完成掃描（結束碼 $OSV_STATUS）。" >&2; return "$OSV_STATUS" ;;
              esac
            }
            case "$TOOL" in
              grype)
                if ! command -v grype >/dev/null 2>&1; then
                  echo "錯誤：指定使用 grype 但系統未安裝 grype" >&2
                  exit 1
                fi
                run_grype
                ;;
              trivy)
                if ! command -v trivy >/dev/null 2>&1; then
                  echo "錯誤：指定使用 trivy 但系統未安裝 trivy" >&2
                  exit 1
                fi
                run_trivy
                ;;
              osv-scanner)
                if ! command -v osv-scanner >/dev/null 2>&1; then
                  echo "錯誤：指定使用 osv-scanner 但系統未安裝 osv-scanner" >&2
                  exit 1
                fi
                run_osv
                ;;
              *)
                if [ "$FAIL_ON" = "all" ] && command -v trivy >/dev/null 2>&1; then
                  run_trivy
                elif [ "$FAIL_ON" = "all" ] && command -v osv-scanner >/dev/null 2>&1; then
                  run_osv
                elif command -v grype >/dev/null 2>&1; then
                  run_grype
                elif command -v trivy >/dev/null 2>&1; then
                  run_trivy
                elif command -v osv-scanner >/dev/null 2>&1; then
                  run_osv
                else
                  echo "錯誤：未找到支援的 SBOM 掃描工具（請安裝 grype、trivy 或 osv-scanner）" >&2
                  exit 1
                fi
                ;;
            esac
            """,
            Path("file", "SBOM 檔案路徑", true, "bom.cdx.json", "要掃描之 SBOM 檔案路徑。"),
            Select("tool", "掃描工具", "auto", ("auto", "自動偵測 (grype > trivy > osv-scanner)"), ("grype", "Grype"), ("trivy", "Trivy"), ("osv-scanner", "OSV-Scanner")),
            Select("failOn", "品質閘門門檻", "high", ("critical", "僅重大 (Critical) 漏洞阻擋"), ("high", "高風險及以上 (High, Critical) 阻擋"), ("medium", "中風險及以上 (Medium, High, Critical) 阻擋"), ("all", "所有漏洞皆阻擋（Trivy／OSV）"), ("never", "不阻擋 (僅輸出掃描結果)"))),

        // ---------------------------------------------------------------- Notifications
        Define(
            "notify.slack",
            "Slack 訊息",
            "用 Incoming Webhook 送一則訊息。",
            ActionCategory.Notify,
            "cil-bell",
            "curl -fsS -X POST -H 'Content-Type: application/json' --data \"{\\\"text\\\": \\\"{{{text}}}\\\"}\" {{webhook}}",
            Text("webhook", "Webhook 網址", true, "$SLACK_WEBHOOK"),
            Text("text", "訊息", true, "$WALIS_JOB_NAME #$WALIS_BUILD_NUMBER 完成")),

        Define(
            "notify.discord",
            "Discord 訊息",
            "用 Webhook 送一則訊息。",
            ActionCategory.Notify,
            "cil-bell",
            "curl -fsS -X POST -H 'Content-Type: application/json' --data \"{\\\"content\\\": \\\"{{{text}}}\\\"}\" {{webhook}}",
            Text("webhook", "Webhook 網址", true, "$DISCORD_WEBHOOK"),
            Text("text", "訊息", true, "$WALIS_JOB_NAME #$WALIS_BUILD_NUMBER 完成")),

        Define(
            "notify.teams",
            "Teams 訊息",
            "用 Incoming Webhook 送一則訊息。",
            ActionCategory.Notify,
            "cil-bell",
            "curl -fsS -X POST -H 'Content-Type: application/json' --data \"{\\\"text\\\": \\\"{{{text}}}\\\"}\" {{webhook}}",
            Text("webhook", "Webhook 網址", true, "$TEAMS_WEBHOOK"),
            Text("text", "訊息", true, "$WALIS_JOB_NAME #$WALIS_BUILD_NUMBER 完成")),

        Define(
            "notify.telegram",
            "Telegram 訊息",
            "用 Bot API 送訊息到聊天室。",
            ActionCategory.Notify,
            "cil-bell",
            "curl -fsS -X POST \"https://api.telegram.org/bot{{{token}}}/sendMessage\" -d chat_id={{chat}} --data-urlencode text={{text}}",
            Text("token", "Bot Token", true, "$TELEGRAM_TOKEN"),
            Text("chat", "Chat ID", true, "$TELEGRAM_CHAT"),
            Text("text", "訊息", true, "$WALIS_JOB_NAME #$WALIS_BUILD_NUMBER 完成")),

        Define(
            "notify.webhook",
            "呼叫 Webhook",
            "對任意網址送 JSON。",
            ActionCategory.Notify,
            "cil-bell",
            "curl -fsS -X POST -H 'Content-Type: application/json'{{#if header}} -H {{header}}{{/if}} --data {{body}} {{url}}",
            Text("url", "網址", true, "https://example.com/hook"),
            Text("header", "額外標頭", placeholder: "Authorization: Bearer $TOKEN"),
            Multiline("body", "JSON 內容", true, "{\"job\": \"$WALIS_JOB_NAME\", \"build\": \"$WALIS_BUILD_NUMBER\"}")),

        // ---------------------------------------------------------------- Archives
        Define(
            "archive.stamp",
            "產出加上建置編號",
            "把檔案複製成帶建置編號的檔名，方便保存多個版本。",
            ActionCategory.Archive,
            "cil-copy",
            "cp {{source}} \"{{{prefix}}}-$WALIS_BUILD_NUMBER{{{suffix}}}\"",
            Path("source", "來源檔", true, "dist/app.tgz"),
            Text("prefix", "檔名前綴", true, "dist/app"),
            Text("suffix", "副檔名", value: ".tgz")),

        Define(
            "archive.cleanup",
            "清掉舊檔案",
            "刪掉目錄裡超過 N 天的檔案，保留最近的。",
            ActionCategory.Archive,
            "cil-trash",
            "find {{path}} -type f -name {{pattern}} -mtime +{{{days}}} -print -delete",
            Path("path", "目錄", true, "backups"),
            Text("pattern", "檔名樣式", value: "*"),
            Number("days", "超過幾天", "30")),

        Define(
            "archive.keep-latest",
            "只留最新幾份",
            "依名稱排序，刪掉超出數量的舊檔案或目錄。",
            ActionCategory.Archive,
            "cil-trash",
            "ls -1dt {{{pattern}}} 2>/dev/null | tail -n +{{{keep}}} | xargs -r rm -rf",
            Text("pattern", "路徑樣式", true, "releases/*"),
            Number("keep", "保留幾份", "5", help: "填 5 會保留最新的 4 份；xargs 從第 N 個開始刪。"))
    ];
}
