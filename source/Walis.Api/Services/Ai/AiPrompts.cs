using System.Text;
using Walis.Core.Models;
using Walis.Core.Security;

namespace Walis.Api.Services.Ai;

/// <summary>Builds the prompts sent to the AI; always asks for a Traditional Chinese reply and caps input length to stay within the context.</summary>
public static class AiPrompts
{
    public const int MaximumLogChars = 12_000;

    public const string AnalyzeSystem = """
                                        你是資深的 CI/CD 與 DevOps 工程師，正在協助排查建置與部署失敗。
                                        請用繁體中文回答，並依下列結構輸出 Markdown：

                                        ## 問題判斷
                                        一到兩句話說明失敗的直接原因。

                                        ## 證據
                                        引用日誌中的關鍵片段，說明你是依據哪幾行做出判斷。

                                        ## 建議處理方式
                                        條列可以立刻執行的步驟，每一項都要具體（要改哪個檔案、跑哪個指令）。

                                        ## 需要再確認的事
                                        列出你無法從日誌確定、需要人工檢查的項目。

                                        如果日誌不足以判斷原因，直接說明缺少什麼資訊，不要臆測。
                                        """;

    public const string ReviewSystem = """
                                       你是資深的 shell 腳本審查者，正在檢查 CI/CD 任務要執行的命令。
                                       請用繁體中文回答，並依下列結構輸出 Markdown：

                                       ## 會出錯的地方
                                       列出語法錯誤、找不到的指令、引號與跳脫問題、路徑假設等會直接失敗的問題。
                                       沒有就寫「沒有發現明顯錯誤」。

                                       ## 風險
                                       列出雖然能跑但危險的地方，例如未檢查回傳值、會刪除資料、機密外洩到日誌。

                                       ## 優化建議
                                       列出可以讓腳本更穩定或更快的調整，並附上修改後的片段。

                                       ## 建議的完整腳本
                                       如果有需要調整，給出可直接取代的完整腳本；沒有就寫「維持原樣即可」。

                                       只針對提供的腳本作答，不要假設沒有提到的環境。
                                       """;

    /// <summary>The maximum length of a script included in a prompt.</summary>
    private const int CommandLimit = 20_000;

    public static string BuildAnalysisPrompt(Job job, Build build, string log)
    {
        // Every value here may have been written by someone else: job fields are user input, and build output is whatever the script printed.
        // Everything goes through PromptGuard, so a log cannot close the code fence itself and turn what follows into instructions to the model.
        var builder = new StringBuilder()
            .AppendLine("以下是一次建置的資訊與輸出，請找出失敗原因。")
            .AppendLine()
            .AppendLine($"任務名稱：{PromptGuard.Field(job.Name)}")
            .AppendLine($"專案：{PromptGuard.Field(job.Project)}")
            .AppendLine($"分支：{PromptGuard.Field(job.Branch)}")
            .AppendLine($"執行器：{PromptGuard.Field(build.RunnerName)}")
            .AppendLine($"觸發方式：{PromptGuard.Field(build.TriggeredBy)}")
            .AppendLine($"建置編號：#{build.Number}")
            .AppendLine($"狀態：{PromptGuard.Field(build.Status)}")
            .AppendLine($"結束碼：{build.ExitCode?.ToString() ?? "（無）"}")
            .AppendLine($"工作目錄：{PromptGuard.Field(job.WorkingDirectory)}")
            .AppendLine()
            .AppendLine("執行的命令：")
            .AppendLine(PromptGuard.Fence("sh", job.Command, CommandLimit))
            .AppendLine()
            .AppendLine("建置輸出：")
            .AppendLine(PromptGuard.Fence(string.Empty, TrimLog(log), MaximumLogChars));

        return builder.ToString();
    }

    public static string BuildReviewPrompt(string command, string? workingDirectory, string? jobName,
        IReadOnlyList<string> variableNames)
    {
        var builder = new StringBuilder()
            .AppendLine("請審查以下 CI/CD 任務要執行的命令。")
            .AppendLine();

        if (!string.IsNullOrWhiteSpace(jobName)) builder.AppendLine($"任務名稱：{PromptGuard.Field(jobName)}");

        builder.AppendLine($"工作目錄：{PromptGuard.Field(workingDirectory)}");
        builder.AppendLine(
            $"執行環境：{(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}");

        if (variableNames.Count != 0)
            builder.AppendLine(
                $"建置時會注入的環境變數：{string.Join('、', variableNames.Select(name => PromptGuard.Field(name, 100)))}");

        builder
            .AppendLine()
            .AppendLine("腳本內容：")
            .AppendLine(PromptGuard.Fence("sh", command, CommandLimit));

        return builder.ToString();
    }

    /// <summary>When the log is too long, keep the head and tail; the failure reason usually sits in one of them.</summary>
    public static string TrimLog(string log)
    {
        if (log.Length <= MaximumLogChars) return log;

        var head = log[..(MaximumLogChars / 3)];
        var tail = log[^(MaximumLogChars * 2 / 3)..];

        return $"{head}\n\n［中間省略 {log.Length - MaximumLogChars} 個字元］\n\n{tail}";
    }
}