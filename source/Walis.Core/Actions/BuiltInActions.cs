namespace Walis.Core.Actions;

/// <summary>
///     The catalog of built-in actions. Every action is just data, so adding one never touches the execution flow;
///     third-party actions are installed into the database as JSON in the same shape.
/// </summary>
public static partial class BuiltInActions
{
    public static IReadOnlyList<ActionDefinition> All { get; } = [.. Build().Select(WithOutputs), .. Extended().Select(WithOutputs)];

    private static ActionParameter Text(string name, string label, bool required = false, string? placeholder = null,
        string? help = null, string? value = null)
    {
        return new ActionParameter
        {
            Name = name, Label = label, Type = ActionParameterType.Text, Required = required, Placeholder = placeholder,
            Help = help, Default = value
        };
    }

    private static ActionParameter Multiline(string name, string label, bool required = false,
        string? placeholder = null, string? help = null)
    {
        return new ActionParameter
        {
            Name = name, Label = label, Type = ActionParameterType.Multiline, Required = required,
            Placeholder = placeholder, Help = help
        };
    }

    private static ActionParameter Path(string name, string label, bool required = false, string? placeholder = null,
        string? help = null, string? value = null)
    {
        return new ActionParameter
        {
            Name = name, Label = label, Type = ActionParameterType.Path, Required = required, Placeholder = placeholder,
            Help = help, Default = value
        };
    }

    private static ActionParameter Number(string name, string label, string? value = null, string? help = null)
    {
        return new ActionParameter
            { Name = name, Label = label, Type = ActionParameterType.Number, Default = value, Help = help };
    }

    private static ActionParameter Boolean(string name, string label, bool value = false, string? help = null)
    {
        return new ActionParameter
        {
            Name = name, Label = label, Type = ActionParameterType.Boolean, Default = value ? "true" : "false",
            Help = help
        };
    }

    private static ActionParameter Select(string name, string label, string value,
        params (string Value, string Label)[] options)
    {
        return new ActionParameter
        {
            Name = name,
            Label = label,
            Type = ActionParameterType.Select,
            Default = value,
            Options = options.Select(option => new ActionOption(option.Value, option.Label)).ToList()
        };
    }

    private static ActionDefinition Define(
        string id,
        string name,
        string description,
        string category,
        string icon,
        string template,
        params ActionParameter[] parameters)
    {
        return new ActionDefinition
        {
            Id = id,
            Name = name,
            Description = description,
            Category = category,
            Icon = icon,
            Author = "Walis",
            Template = template,
            Parameters = parameters,
            IsBuiltIn = true
        };
    }

    /// <summary>Built-in actions that write $WALIS_OUTPUT_FILE declare their outputs here so the UI can list them under "insert an earlier step's output".</summary>
    // A method rather than a static field: All is initialized at the top of the file, when a static field would still be null.
    private static ActionOutput[]? DeclaredOutputs(string id)
    {
        return id switch
        {
            "http.request" => [new ActionOutput("status", "HTTP 回應碼"), new ActionOutput("body", "回應內容（單行，最多約 8 KB）")],
            "data.json-get" => [new ActionOutput("value", "取出的欄位值；物件或陣列是 JSON 文字")],
            "sbom.generate" => [new ActionOutput("path", "產出之 SBOM 檔案路徑"), new ActionOutput("format", "SBOM 格式規格")],
            _ => null
        };
    }

    private static ActionDefinition WithOutputs(ActionDefinition definition)
    {
        return DeclaredOutputs(definition.Id) is { } outputs
            ? new ActionDefinition
            {
                Id = definition.Id, Name = definition.Name, Description = definition.Description, Category = definition.Category,
                Icon = definition.Icon, Version = definition.Version, Author = definition.Author, Parameters = definition.Parameters,
                Outputs = outputs, Template = definition.Template, IsBuiltIn = true
            }
            : definition;
    }

    private static List<ActionDefinition> Build()
    {
        return
        [
            Define(
                "shell.run",
                "執行命令",
                "在工作目錄執行一段 shell 命令，最常用的萬用動作。",
                ActionCategory.General,
                "cil-terminal",
                "{{{command}}}",
                Multiline("command", "命令", true, "npm ci\nnpm run build")),

            Define(
                "http.request",
                "HTTP 請求",
                "用 curl 送請求，把回應碼與內容當成這一步的輸出（status、body），後面的步驟可以用條件或參數引用；回應碼 4xx／5xx 不會讓這一步失敗，交給條件判斷。",
                ActionCategory.General,
                "cil-transfer",
                "__WALIS_HTTP_BODY=$(mktemp)\n__WALIS_HTTP_STATUS=$(curl -sS -X {{{method}}}{{#if header}} -H {{header}}{{/if}}{{#if body}} -H 'Content-Type: application/json' --data {{body}}{{/if}} -o \"$__WALIS_HTTP_BODY\" -w '%{http_code}' {{url}})\necho \"status=$__WALIS_HTTP_STATUS\" >> \"$WALIS_OUTPUT_FILE\"\nprintf 'body=' >> \"$WALIS_OUTPUT_FILE\"; head -c 7900 \"$__WALIS_HTTP_BODY\" | tr -d '\\n\\r' >> \"$WALIS_OUTPUT_FILE\"; echo >> \"$WALIS_OUTPUT_FILE\"\necho \"HTTP $__WALIS_HTTP_STATUS\"; head -c 2000 \"$__WALIS_HTTP_BODY\"; echo\nrm -f \"$__WALIS_HTTP_BODY\"",
                Select("method", "方法", "GET", ("GET", "GET"), ("POST", "POST"), ("PUT", "PUT"), ("PATCH", "PATCH"), ("DELETE", "DELETE")),
                Text("url", "網址", true, "https://api.example.com/items"),
                Text("header", "額外標頭", placeholder: "Authorization: Bearer $API_TOKEN", help: "值裡的 $VAR 會在執行時展開，機密請放任務的機密環境變數。"),
                Multiline("body", "JSON 內容", placeholder: "{\"name\": \"$WALIS_JOB_NAME\"}")),

            Define(
                "data.json-get",
                "取出 JSON 欄位",
                "從一段 JSON（通常是前一步 http.request 的 body）取出欄位，輸出成 value；路徑用點分隔，陣列用數字。執行器要有 python3 或 node。",
                ActionCategory.General,
                "cil-braille",
                "__WALIS_JSON={{json}}\n__WALIS_PATH={{path}}\nif command -v python3 >/dev/null 2>&1; then\n  __WALIS_VALUE=$(printf '%s' \"$__WALIS_JSON\" | python3 -c 'import json,sys\nv=json.load(sys.stdin)\nfor p in sys.argv[1].split(\".\"):\n    if p==\"\": continue\n    v=v[int(p)] if isinstance(v,list) else v.get(p)\n    if v is None: break\nprint(\"\" if v is None else v if isinstance(v,str) else json.dumps(v,ensure_ascii=False))' \"$__WALIS_PATH\")\nelse\n  __WALIS_VALUE=$(printf '%s' \"$__WALIS_JSON\" | node -e 'let d=\"\";process.stdin.on(\"data\",c=>d+=c).on(\"end\",()=>{let v=JSON.parse(d);for(const p of process.argv[1].split(\".\")){if(p===\"\")continue;v=Array.isArray(v)?v[Number(p)]:(v??{})[p];if(v==null)break}console.log(v==null?\"\":typeof v===\"string\"?v:JSON.stringify(v))})' \"$__WALIS_PATH\")\nfi\necho \"value=$__WALIS_VALUE\" >> \"$WALIS_OUTPUT_FILE\"\necho \"$__WALIS_PATH = $__WALIS_VALUE\"",
                Multiline("json", "JSON", true, "${{ steps.api.body }}"),
                Text("path", "欄位路徑", true, "data.items.0.name")),

            Define(
                "shell.outputs",
                "設定輸出",
                "把 name=value 寫成這一步的輸出，後面的步驟用 ${{ steps.<識別字>.<name> }} 引用；值可以用 shell 變數。",
                ActionCategory.General,
                "cil-share-boxed",
                "cat >> \"$WALIS_OUTPUT_FILE\" <<WALIS_OUTPUTS\n{{{outputs}}}\nWALIS_OUTPUTS",
                Multiline("outputs", "輸出", true, "version=$(cat VERSION)\nimage=registry/app:$WALIS_BUILD_NUMBER",
                    help: "一行一個 name=value。這裡的 $VAR 會在執行時展開；$(...) 也可以。")),

            Define(
                "shell.echo",
                "輸出訊息",
                "把一段文字寫進建置日誌，方便標記進度。",
                ActionCategory.General,
                "cil-list-rich",
                "echo {{message}}",
                Text("message", "訊息", true, "開始部署")),

            Define(
                "shell.sleep",
                "等待",
                "暫停指定秒數，常用在等待服務啟動。",
                ActionCategory.General,
                "cil-clock",
                "sleep {{{seconds}}}",
                Number("seconds", "秒數", "5")),

            Define(
                "shell.env",
                "設定環境變數",
                "為後續步驟設定一個環境變數。",
                ActionCategory.General,
                "cil-code",
                "export {{{name}}}={{value}}",
                Text("name", "變數名稱", true, "NODE_ENV"),
                Text("value", "值", true, "production")),

            Define(
                "shell.cd",
                "切換目錄",
                "切換到指定目錄，後續步驟都會在這裡執行。",
                ActionCategory.General,
                "cil-storage",
                "cd {{path}}",
                Path("path", "目錄", true, "./src")),

            Define(
                "file.mkdir",
                "建立目錄",
                "建立目錄，已存在時不會失敗。",
                ActionCategory.File,
                "cil-storage",
                "mkdir -p {{path}}",
                Path("path", "目錄", true, "dist")),

            Define(
                "file.copy",
                "複製檔案",
                "複製檔案或目錄。",
                ActionCategory.File,
                "cil-copy",
                "cp {{#if recursive}}-r {{/if}}{{source}} {{destination}}",
                Path("source", "來源", true),
                Path("destination", "目的地", true),
                Boolean("recursive", "包含子目錄", true)),

            Define(
                "file.move",
                "移動檔案",
                "移動或改名。",
                ActionCategory.File,
                "cil-share-alt",
                "mv {{source}} {{destination}}",
                Path("source", "來源", true),
                Path("destination", "目的地", true)),

            Define(
                "file.remove",
                "刪除檔案",
                "刪除檔案或目錄，路徑不存在時不會失敗。",
                ActionCategory.File,
                "cil-trash",
                "rm -rf {{path}}",
                Path("path", "路徑", true, help: "刪除是不可逆的，請確認路徑正確。")),

            Define(
                "file.zip",
                "壓縮成 zip",
                "把目錄或檔案壓縮成 zip。",
                ActionCategory.File,
                "cil-cloud-download",
                "zip -r {{archive}} {{source}}",
                Path("source", "來源", true, "dist"),
                Path("archive", "輸出檔名", true, "dist.zip")),

            Define(
                "git.source",
                "取得原始碼",
                "第一次建置時取回儲存庫，之後更新到最新的提交。預設使用任務設定的儲存庫與分支，直接放進工作目錄。",
                ActionCategory.Git,
                "cil-layers",
                "WALIS_SOURCE_REPOSITORY={{#if repository}}{{repository}}{{/if}}{{#unless repository}}\"$WALIS_REPOSITORY\"{{/unless}}\nWALIS_SOURCE_BRANCH={{#if branch}}{{branch}}{{/if}}{{#unless branch}}\"${WALIS_BRANCH:-main}\"{{/unless}}\nWALIS_SOURCE_DIRECTORY={{#if directory}}{{directory}}{{/if}}{{#unless directory}}.{{/unless}}\nif [ -z \"$WALIS_SOURCE_REPOSITORY\" ]; then\n  echo '沒有可用的儲存庫位址，請在任務設定填寫儲存庫，或在這個步驟填入位址。'\n  exit 1\nfi\nmkdir -p \"$WALIS_SOURCE_DIRECTORY\"\nif [ ! -d \"$WALIS_SOURCE_DIRECTORY/.git\" ]; then git init -q \"$WALIS_SOURCE_DIRECTORY\"; fi\ngit -C \"$WALIS_SOURCE_DIRECTORY\" remote set-url origin \"$WALIS_SOURCE_REPOSITORY\" 2>/dev/null || git -C \"$WALIS_SOURCE_DIRECTORY\" remote add origin \"$WALIS_SOURCE_REPOSITORY\"\ngit -C \"$WALIS_SOURCE_DIRECTORY\" fetch --prune{{#if depth}} --depth {{{depth}}}{{/if}} origin \"$WALIS_SOURCE_BRANCH\"\ngit -C \"$WALIS_SOURCE_DIRECTORY\" checkout --force -B \"$WALIS_SOURCE_BRANCH\" FETCH_HEAD\n{{#if submodules}}git -C \"$WALIS_SOURCE_DIRECTORY\" submodule update --init --recursive\n{{/if}}{{#if clean}}git -C \"$WALIS_SOURCE_DIRECTORY\" clean -ffdx\n{{/if}}WALIS_SOURCE_DISPLAY=$(printf '%s' \"$WALIS_SOURCE_REPOSITORY\" | sed -E 's#^([A-Za-z][A-Za-z0-9+.-]*://)[^/@]*@#\\1#')\necho \"已取得 $WALIS_SOURCE_DISPLAY 的 $WALIS_SOURCE_BRANCH\"\ngit -C \"$WALIS_SOURCE_DIRECTORY\" log -1 --pretty='    %h %s'",
                Text("repository", "儲存庫", placeholder: "https://github.com/org/repo.git",
                    help: "留空使用任務設定的儲存庫。"),
                Text("branch", "分支", placeholder: "main", help: "留空使用任務設定的分支。"),
                Path("directory", "目標目錄",
                    help: "留空直接放進工作目錄。填了子目錄的話，後面每個步驟都要自己切換進去，因為每一步是獨立的 shell。"),
                Number("depth", "淺層深度", help: "只取最近幾筆提交，留空表示完整歷史。"),
                Boolean("submodules", "包含子模組"),
                Boolean("clean", "清掉未追蹤的檔案",
                    help: "連 .gitignore 忽略的檔案一起清掉；工作目錄就是儲存庫時，還原回來的建置快取也會一起被刪除。")),

            Define(
                "git.clone",
                "Git Clone",
                "複製儲存庫，可指定分支與深度。要重複建置或沿用任務設定時用「取得原始碼」比較省事。",
                ActionCategory.Git,
                "cil-copy",
                "git clone {{#if branch}}--branch {{branch}} {{/if}}{{#if depth}}--depth {{{depth}}} {{/if}}{{repository}}{{#if directory}} {{directory}}{{/if}}",
                Text("repository", "儲存庫", true, "https://github.com/org/repo.git"),
                Text("branch", "分支", placeholder: "main"),
                Number("depth", "淺層深度", help: "留空表示完整複製。"),
                Path("directory", "目標目錄", help: "留空會建立與儲存庫同名的目錄。")),

            Define(
                "git.checkout",
                "Git Checkout",
                "切換到指定分支或提交。",
                ActionCategory.Git,
                "cil-layers",
                "git checkout {{reference}}",
                Text("reference", "分支或提交", true, "main")),

            Define(
                "git.pull",
                "Git Pull",
                "拉取最新的提交。工作目錄有未提交的變更時會失敗，想要每次結果一致請用「取得原始碼」。",
                ActionCategory.Git,
                "cil-reload",
                "git pull{{#if rebase}} --rebase{{/if}}{{#if remote}} {{remote}}{{/if}}{{#if branch}} {{branch}}{{/if}}",
                Text("remote", "遠端", value: "origin"),
                Text("branch", "分支"),
                Boolean("rebase", "使用 rebase")),

            Define(
                "node.install",
                "npm 安裝相依套件",
                "依 lock 檔安裝相依套件。",
                ActionCategory.Node,
                "cil-code",
                "{{#if useCi}}npm ci{{/if}}{{#unless useCi}}npm install{{/unless}}",
                Boolean("useCi", "使用 npm ci", true, "有 package-lock.json 時建議使用，安裝結果比較穩定。")),

            Define(
                "node.script",
                "執行 npm script",
                "執行 package.json 裡的 script。",
                ActionCategory.Node,
                "cil-media-play",
                "npm run {{{script}}}",
                Text("script", "script 名稱", true, "build")),

            Define(
                "node.npx",
                "執行 npx 指令",
                "用 npx 執行套件提供的指令。",
                ActionCategory.Node,
                "cil-terminal",
                "npx {{{command}}}",
                Text("command", "指令與參數", true, "vite build")),

            Define(
                "dotnet.restore",
                "dotnet restore",
                "還原 NuGet 相依套件。",
                ActionCategory.Dotnet,
                "cil-cloud-download",
                "dotnet restore{{#if project}} {{project}}{{/if}}",
                Path("project", "專案或方案")),

            Define(
                "dotnet.build",
                "dotnet build",
                "建置專案或方案。",
                ActionCategory.Dotnet,
                "cil-layers",
                "dotnet build{{#if project}} {{project}}{{/if}} --configuration {{{configuration}}}",
                Path("project", "專案或方案"),
                Select("configuration", "組態", "Release", ("Release", "Release"), ("Debug", "Debug"))),

            Define(
                "dotnet.test",
                "dotnet test",
                "執行測試專案。",
                ActionCategory.Dotnet,
                "cil-check-circle",
                "dotnet test{{#if project}} {{project}}{{/if}} --configuration {{{configuration}}}{{#if noBuild}} --no-build{{/if}}",
                Path("project", "測試專案"),
                Select("configuration", "組態", "Release", ("Release", "Release"), ("Debug", "Debug")),
                Boolean("noBuild", "略過建置")),

            Define(
                "dotnet.publish",
                "dotnet publish",
                "產生發行輸出。",
                ActionCategory.Dotnet,
                "cil-cloud-download",
                "dotnet publish{{#if project}} {{project}}{{/if}} --configuration {{{configuration}}} --output {{output}}",
                Path("project", "專案"),
                Select("configuration", "組態", "Release", ("Release", "Release"), ("Debug", "Debug")),
                Path("output", "輸出目錄", true, "publish")),

            Define(
                "docker.build",
                "Docker Build",
                "建置映像檔。",
                ActionCategory.Container,
                "cil-storage",
                "docker build -t {{tag}}{{#if dockerfile}} -f {{dockerfile}}{{/if}} {{context}}",
                Text("tag", "映像標籤", true, "walis/api:latest"),
                Path("context", "建置內容", true, "."),
                Path("dockerfile", "Dockerfile")),

            Define(
                "container.run",
                "在容器中執行",
                "把工作目錄掛進指定映像，在裡面執行一段腳本（Jenkins 的 agent { docker }）。",
                ActionCategory.Container,
                "cil-terminal",
                "docker run --rm {{#if args}}{{{args}}} {{/if}}-v \"$PWD\":/workspace -w /workspace {{image}} sh -ec {{script}}",
                Text("image", "映像", true, "node:20"),
                Multiline("script", "腳本", true, "npm ci\nnpm test"),
                Text("args", "額外參數", placeholder: "-e CI=true", help: "原樣接在 docker run 後面，例如環境變數或掛載。")),

            Define(
                "docker.push",
                "Docker Push",
                "推送映像檔到 registry。",
                ActionCategory.Container,
                "cil-cloud-upload",
                "docker push {{tag}}",
                Text("tag", "映像標籤", true)),

            Define(
                "docker.compose",
                "Docker Compose",
                "以 compose 啟動或更新服務。",
                ActionCategory.Container,
                "cil-layers",
                "docker compose{{#if file}} -f {{file}}{{/if}} {{{command}}}",
                Path("file", "compose 檔"),
                Select("command", "指令", "up -d", ("up -d", "up -d"), ("down", "down"), ("pull", "pull"),
                    ("restart", "restart"))),

            Define(
                "deploy.ssh",
                "SSH 遠端執行",
                "透過 SSH 連到遠端主機執行命令，常用在重新啟動服務或跑部署腳本。",
                ActionCategory.Deploy,
                "cil-terminal",
                "ssh{{#if batchMode}} -o BatchMode=yes{{/if}}{{#if port}} -p {{{port}}}{{/if}}{{#if identity}} -i {{identity}}{{/if}} {{host}} {{command}}",
                Text("host", "目標主機", true, "user@host"),
                Multiline("command", "命令", true, "cd /opt/app\n./restart.sh"),
                Number("port", "連接埠", help: "留空使用預設的 22。"),
                Path("identity", "私鑰檔", help: "留空使用 SSH 預設的金鑰。路徑不會展開 ~，請填絕對路徑。"),
                Boolean("batchMode", "非互動模式", true, "遠端要求輸入密碼時直接失敗，避免建置卡住。")),

            Define(
                "deploy.rsync",
                "rsync 同步",
                "把目錄同步到遠端主機。",
                ActionCategory.Deploy,
                "cil-share-alt",
                "rsync -az{{#if delete}} --delete{{/if}} {{source}} {{destination}}",
                Path("source", "來源目錄", true, "dist/"),
                Text("destination", "目的地", true, "user@host:/var/www/app"),
                Boolean("delete", "刪除目的地多餘的檔案")),

            Define(
                "deploy.scp",
                "scp 上傳",
                "把檔案複製到遠端主機。",
                ActionCategory.Deploy,
                "cil-cloud-upload",
                "scp {{#if recursive}}-r {{/if}}{{source}} {{destination}}",
                Path("source", "來源", true),
                Text("destination", "目的地", true, "user@host:/opt/app"),
                Boolean("recursive", "包含子目錄", true)),

            Define(
                "deploy.http",
                "HTTP 請求",
                "呼叫一個 HTTP 端點，例如觸發部署或送出通知。",
                ActionCategory.Deploy,
                "cil-external-link",
                "curl --fail --silent --show-error -X {{{method}}}{{#if contentType}} -H {{contentType}}{{/if}}{{#if body}} -d {{body}}{{/if}} {{url}}",
                Text("url", "網址", true, "https://example.com/deploy"),
                Select("method", "方法", "POST", ("POST", "POST"), ("GET", "GET"), ("PUT", "PUT"), ("DELETE", "DELETE")),
                Text("contentType", "Content-Type 標頭", placeholder: "Content-Type: application/json"),
                Multiline("body", "內容")),

            Define(
                "check.file",
                "檢查檔案存在",
                "檔案不存在時讓建置失敗。",
                ActionCategory.Check,
                "cil-check-circle",
                "test -e {{path}} || { echo {{message}}; exit 1; }",
                Path("path", "路徑", true),
                Text("message", "失敗訊息", value: "找不到必要的檔案")),

            Define(
                "check.command",
                "檢查指令可用",
                "指令不存在時讓建置失敗。",
                ActionCategory.Check,
                "cil-check-circle",
                "command -v {{{command}}} > /dev/null || { echo {{message}}; exit 1; }",
                Text("command", "指令", true, "node"),
                Text("message", "失敗訊息", value: "缺少必要的指令"))
        ];
    }
}